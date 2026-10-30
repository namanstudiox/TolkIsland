using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TolkIsland.Services;

public sealed class AudioVisualizerService : IDisposable
{
    public const int BandCount = 5;

    private const int FftSize = 1024;
    private const int FftBits = 10;
    private const int FrameIntervalMs = 33;
    private const long LoopbackBufferDuration = 2_500_000; // 100 ns units
    private const int PollSleepMs = 6;
    private const int DeviceCheckMs = 1500;
    private const int StallTimeoutMs = 2500;

    private const float SilencePeak = 3e-4f;
    private const float FloorDb = -60f;
    private const float CeilingDb = -10f;

    private static readonly double[] BandEdges = { 40, 160, 500, 1600, 5000, 16000 };
    private static readonly float[] BandTiltDb = { 0f, 1f, 2f, 3f, 4f };

    private static readonly float[] Window = BuildWindow();
    private static readonly int[] BitReverse = BuildBitReverse();
    private static readonly float[][] TwiddleCos = BuildTwiddles(cosine: true);
    private static readonly float[][] TwiddleSin = BuildTwiddles(cosine: false);
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly object _sync = new();
    private readonly float[] _levels = new float[BandCount];
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly CaptureLease _lease = new();

    private Thread? _thread;
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private bool _disposed;

    public string? SourceFormat { get; private set; }

    private int _ringPos;
    private long _totalSamples;
    private float[] _scratch = Array.Empty<float>();
    private byte[] _scratchBytes = Array.Empty<byte>();

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _thread is not null;
            }
        }
    }

    public bool TryGetLevels(Span<float> destination)
    {
        if (destination.Length < BandCount)
        {
            return false;
        }

        lock (_sync)
        {
            for (var i = 0; i < BandCount; i++)
            {
                destination[i] = _levels[i];
            }
        }

        return true;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || !_lease.Acquire())
            {
                return;
            }

            SpawnLocked(_lease.StartSession());
        }
    }

    private void SpawnLocked(long generation)
    {
        _stopSignal.Reset();
        if (_thread is { IsAlive: true })
        {
            return;
        }

        _thread = new Thread(() => CaptureLoop(generation))
        {
            IsBackground = true,
            Name = "Tolk Island audio visualizer",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_lease.Release())
        {
            return;
        }

        _stopSignal.Set();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _stopSignal.Set();
    }

    private void CaptureLoop(long generation)
    {
        var com = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
        try
        {
            while (!_stopSignal.IsSet)
            {
                if (!RunSession() && _stopSignal.Wait(400))
                {
                    break;
                }
            }
        }
        finally
        {
            if (com >= 0)
            {
                CoUninitialize();
            }

            lock (_sync)
            {
                if (ReferenceEquals(_thread, Thread.CurrentThread))
                {
                    _thread = null;
                }

                if (!_disposed && _lease.Count > 0)
                {
                    SpawnLocked(_lease.StartSession());
                }
                else if (_lease.IsCurrent(generation) || _lease.Count == 0)
                {
                    Array.Clear(_levels);
                }
            }
        }
    }

    private bool RunSession()
    {
        IAudioClient? client = null;
        IntPtr formatPtr = IntPtr.Zero;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) < 0
                || device is null)
            {
                return false;
            }

            if (device.GetId(out var endpointId) < 0)
            {
                endpointId = null;
            }

            var clientIid = typeof(IAudioClient).GUID;
            if (device.Activate(ref clientIid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var comObject) < 0
                || comObject is not IAudioClient audioClient)
            {
                return false;
            }

            client = audioClient;

            if (client.GetMixFormat(out formatPtr) < 0 || formatPtr == IntPtr.Zero)
            {
                return false;
            }

            var format = Marshal.PtrToStructure<WaveFormatEx>(formatPtr);
            if (format.Channels == 0 || format.SamplesPerSec == 0)
            {
                return false;
            }

            var isFloat = format.FormatTag == 3;
            if (format.FormatTag == 0xFFFE && format.ExtraSize >= 22)
            {
                var subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(formatPtr, 24));
                isFloat = subFormat == IeeeFloatSubFormat;
            }

            SourceFormat = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} Hz, {1} ch, {2}-bit {3} (tag 0x{4:X4})",
                format.SamplesPerSec,
                format.Channels,
                format.BitsPerSample,
                isFloat ? "float" : "PCM",
                format.FormatTag);

            if (client.Initialize(
                    0 /* AUDCLNT_SHAREMODE_SHARED */,
                    AudioClientLoopback,
                    LoopbackBufferDuration,
                    0,
                    formatPtr,
                    IntPtr.Zero) < 0)
            {
                return false;
            }

            var captureIid = typeof(IAudioCaptureClient).GUID;
            if (client.GetService(ref captureIid, out var service) < 0
                || service is not IAudioCaptureClient capture)
            {
                return false;
            }

            ResetCaptureState();
            if (client.Start() < 0)
            {
                return false;
            }

            Poll(capture, format, isFloat, endpointId);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                client?.Stop();
            }
            catch
            {
            }

            if (formatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(formatPtr);
            }
        }
    }

    private void Poll(IAudioCaptureClient capture, WaveFormatEx format, bool isFloat, string? endpointId)
    {
        var channels = (int)format.Channels;
        var bits = (int)format.BitsPerSample;
        var sampleRate = (int)format.SamplesPerSec;

        var lastFrame = Environment.TickCount64;
        var lastDeviceCheck = lastFrame;
        var lastPacket = lastFrame;

        while (!_stopSignal.IsSet)
        {
            if (capture.GetNextPacketSize(out var packetFrames) < 0)
            {
                return;
            }

            while (packetFrames > 0 && !_stopSignal.IsSet)
            {
                if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) < 0)
                {
                    return;
                }

                if (frames > 0)
                {
                    if (data == IntPtr.Zero || (flags & AudioClientBufferSilent) != 0)
                    {
                        FeedSilence(frames);
                    }
                    else
                    {
                        Feed(data, frames, channels, bits, isFloat);
                    }

                    lastPacket = Environment.TickCount64;
                }

                capture.ReleaseBuffer(frames);

                if (capture.GetNextPacketSize(out packetFrames) < 0)
                {
                    return;
                }
            }

            var now = Environment.TickCount64;

            if (now - lastFrame >= FrameIntervalMs)
            {
                lastFrame = now;
                ComputeFrame(sampleRate);
            }

            if (now - lastPacket >= StallTimeoutMs)
            {
                return;
            }

            if (now - lastDeviceCheck >= DeviceCheckMs)
            {
                lastDeviceCheck = now;
                if (TryGetDefaultEndpointId(out var current)
                    && endpointId is not null
                    && !string.Equals(current, endpointId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            _stopSignal.Wait(PollSleepMs);
        }
    }

    private void Feed(IntPtr data, uint frames, int channels, int bits, bool isFloat)
    {
        var count = (int)frames * channels;

        if (bits == 16)
        {
            if (_scratchBytes.Length < count * 2)
            {
                _scratchBytes = new byte[count * 2];
            }

            Marshal.Copy(data, _scratchBytes, 0, count * 2);
            for (var f = 0; f < frames; f++)
            {
                var offset = f * channels;
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    sum += BitConverter.ToInt16(_scratchBytes, (offset + c) * 2) / 32768f;
                }

                Push(sum / channels);
            }

            return;
        }

        if (bits == 32 && isFloat)
        {
            if (_scratch.Length < count)
            {
                _scratch = new float[count];
            }

            Marshal.Copy(data, _scratch, 0, count);
            for (var f = 0; f < frames; f++)
            {
                var offset = f * channels;
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    sum += _scratch[offset + c];
                }

                Push(sum / channels);
            }

            return;
        }

        if (bits == 32)
        {
            if (_scratchBytes.Length < count * 4)
            {
                _scratchBytes = new byte[count * 4];
            }

            Marshal.Copy(data, _scratchBytes, 0, count * 4);
            for (var f = 0; f < frames; f++)
            {
                var offset = f * channels;
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    sum += BitConverter.ToInt32(_scratchBytes, (offset + c) * 4) / 2147483648f;
                }

                Push(sum / channels);
            }

            return;
        }

        FeedSilence(frames);
    }

    private void FeedSilence(uint frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Push(0f);
        }
    }

    private void Push(float sample)
    {
        _ring[_ringPos] = sample;
        _ringPos = (_ringPos + 1) & (FftSize - 1);
        _totalSamples++;
    }

    private void ResetCaptureState()
    {
        Array.Clear(_ring);
        _ringPos = 0;
        _totalSamples = 0;
    }

    private void ComputeFrame(int sampleRate)
    {
        if (_totalSamples < FftSize)
        {
            return;
        }

        var peak = 0f;
        for (var i = 0; i < FftSize; i++)
        {
            var sample = _ring[(_ringPos + i) & (FftSize - 1)];
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }

            _re[i] = sample * Window[i];
            _im[i] = 0f;
        }

        RunFft(_re, _im);

        var binHz = (double)sampleRate / FftSize;
        var scale = 4.0 / FftSize; // Hann coherent gain 0.5, single-sided x2
        Span<float> targets = stackalloc float[BandCount];

        for (var band = 0; band < BandCount; band++)
        {
            var first = Math.Max(1, (int)Math.Ceiling(BandEdges[band] / binHz));
            var last = Math.Min(FftSize / 2 - 1, (int)Math.Floor(BandEdges[band + 1] / binHz));
            if (last < first)
            {
                last = first;
            }

            var sumSquares = 0.0;
            for (var bin = first; bin <= last; bin++)
            {
                double re = _re[bin];
                double im = _im[bin];
                sumSquares += re * re + im * im;
            }

            var amplitude = Math.Sqrt(sumSquares) * scale;
            var db = 20.0 * Math.Log10(amplitude + 1e-7) + BandTiltDb[band];
            targets[band] = (float)Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0, 1);
        }

        var silent = peak < SilencePeak;

        lock (_sync)
        {
            for (var band = 0; band < BandCount; band++)
            {
                var target = silent ? 0f : targets[band];
                var coefficient = silent ? 0.4f : target > _levels[band] ? 0.55f : 0.16f;
                _levels[band] += (target - _levels[band]) * coefficient;
            }
        }
    }

    private void ResetLevels()
    {
        lock (_sync)
        {
            Array.Clear(_levels);
        }
    }

    private static bool TryGetDefaultEndpointId(out string? endpointId)
    {
        endpointId = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) < 0
                || device is null)
            {
                return false;
            }

            return device.GetId(out endpointId) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static float[] BuildWindow()
    {
        var window = new float[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))));
        }

        return window;
    }

    private static int[] BuildBitReverse()
    {
        var table = new int[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            var value = i;
            var reversed = 0;
            for (var bit = 0; bit < FftBits; bit++)
            {
                reversed = (reversed << 1) | (value & 1);
                value >>= 1;
            }

            table[i] = reversed;
        }

        return table;
    }

    private static float[][] BuildTwiddles(bool cosine)
    {
        var table = new float[FftBits + 1][];
        for (var stage = 1; stage <= FftBits; stage++)
        {
            var length = 1 << stage;
            var half = length >> 1;
            table[stage] = new float[half];
            for (var i = 0; i < half; i++)
            {
                var angle = -2 * Math.PI * i / length;
                table[stage][i] = (float)(cosine ? Math.Cos(angle) : Math.Sin(angle));
            }
        }

        return table;
    }

    private static void RunFft(float[] re, float[] im)
    {
        for (var i = 0; i < FftSize; i++)
        {
            var j = BitReverse[i];
            if (j <= i)
            {
                continue;
            }

            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        for (var stage = 1; stage <= FftBits; stage++)
        {
            var length = 1 << stage;
            var half = length >> 1;
            var cos = TwiddleCos[stage];
            var sin = TwiddleSin[stage];

            for (var start = 0; start < FftSize; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var a = start + k;
                    var b = a + half;
                    var vRe = re[b] * cos[k] - im[b] * sin[k];
                    var vIm = re[b] * sin[k] + im[b] * cos[k];
                    var uRe = re[a];
                    var uIm = im[a];
                    re[a] = uRe + vRe;
                    im[a] = uIm + vIm;
                    re[b] = uRe - vRe;
                    im[b] = uIm - vIm;
                }
            }
        }
    }

    private const uint AudioClientLoopback = 0x00020000;
    private const uint AudioClientBufferSilent = 0x00000002;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
        IntPtr format, IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint paddingFrames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr format);

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint frames, out uint flags,
        out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint framesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint frames);
}
