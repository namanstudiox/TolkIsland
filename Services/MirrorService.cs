using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace TolkIsland.Services;

public enum MirrorState
{
    Off,
    Starting,
    Live,
    Denied,
    NoCamera,
    Failed
}

public sealed record MirrorFrameInfo(int Width, int Height, int Stride);

/// <summary>
/// Front-camera preview for the Mirror panel. Frames live in one reusable
/// buffer and are only copied to a window's bitmap; nothing is recorded,
/// encoded or written to disk. Capture starts with the first owner and stops
/// with the last one, so closing the panel releases the camera immediately.
/// </summary>
public sealed class MirrorService : IDisposable
{
    // The island is 480 DIP wide, so a 960×540 stream is already supersampled.
    private const int PreferredWidth = 960;
    private const int PreferredHeight = 540;

    private readonly object _ownerGate = new();
    private readonly HashSet<object> _owners = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly object _frameGate = new();

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private byte[] _frame = Array.Empty<byte>();
    private IBuffer? _copyBuffer;
    private MirrorFrameInfo? _format;
    private MirrorState _state;
    private volatile bool _disposed;

    public MirrorState State => _state;

    public event Action<MirrorState>? StateChanged;
    public event Action? FrameReady;

    public MirrorFrameInfo? Format
    {
        get
        {
            lock (_frameGate)
            {
                return _format;
            }
        }
    }

    /// <summary>
    /// The panel owns the camera while it is open. Several shells can watch the
    /// same stream; capture stops only when every owner has let go.
    /// </summary>
    public void SetActive(object owner, bool active)
    {
        if (_disposed)
        {
            return;
        }

        lock (_ownerGate)
        {
            if (active)
            {
                _owners.Add(owner);
            }
            else
            {
                _owners.Remove(owner);
            }
        }

        _ = RefreshCaptureAsync();
    }

    public void Retry(object owner)
    {
        SetActive(owner, false);
        SetActive(owner, true);
    }

    /// <summary>Copies the newest frame into a window-owned bitmap.</summary>
    public bool CopyLatestFrame(WriteableBitmap target)
    {
        lock (_frameGate)
        {
            if (_format is not { } format
                || _frame.Length != format.Stride * format.Height
                || target.PixelWidth != format.Width
                || target.PixelHeight != format.Height)
            {
                return false;
            }

            target.WritePixels(
                new Int32Rect(0, 0, format.Width, format.Height),
                _frame,
                format.Stride,
                0);
            return true;
        }
    }

    private bool Wanted
    {
        get
        {
            lock (_ownerGate)
            {
                return _owners.Count > 0;
            }
        }
    }

    private async Task RefreshCaptureAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _captureGate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            if (Wanted)
            {
                if (_capture is null && _state != MirrorState.Starting)
                {
                    await StartCoreAsync();
                }
            }
            else
            {
                await StopCoreAsync();
            }
        }
        catch (Exception exception)
        {
            DiagnosticsService.Write("Mirror", exception);
            await CleanupAsync();
            SetState(MirrorState.Failed);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        if (!Wanted)
        {
            SetState(MirrorState.Off);
            return;
        }

        SetState(MirrorState.Starting);

        try
        {
            var device = await FindCameraAsync();

            // The panel may have closed while devices were enumerated. Never
            // open the camera for a request that is already over.
            if (!Wanted)
            {
                SetState(MirrorState.Off);
                return;
            }

            if (device is null)
            {
                SetState(MirrorState.NoCamera);
                return;
            }

            // The camera is a local preview only; ask for no audio and shared
            // access so a running meeting app is not forcibly stopped.
            var capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            };

            await capture.InitializeAsync(settings);
            _capture = capture;

            if (!Wanted)
            {
                await StopCoreAsync();
                return;
            }

            var source = capture.FrameSources.Values.FirstOrDefault(
                frameSource => frameSource.Info.SourceKind == MediaFrameSourceKind.Color);

            if (source is null)
            {
                await CleanupAsync();
                SetState(MirrorState.Failed);
                return;
            }

            var format = ChooseFormat(source);
            if (format is not null)
            {
                try
                {
                    await source.SetFormatAsync(format);
                }
                catch
                {
                    // Keep the camera's default format; frames still work.
                }
            }

            MediaFrameReader reader;
            try
            {
                reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            }
            catch
            {
                // Some virtual cameras cannot emit BGRA; convert in the handler.
                reader = await capture.CreateFrameReaderAsync(source);
            }

            _reader = reader;
            capture.Failed += OnCaptureFailed;
            reader.FrameArrived += OnFrameArrived;

            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                await CleanupAsync();
                SetState(MirrorState.Failed);
                return;
            }

            if (!Wanted)
            {
                await StopCoreAsync();
                return;
            }

            SetState(MirrorState.Live);
        }
        catch (Exception exception)
        {
            await CleanupAsync();

            if (IsDenied(exception))
            {
                SetState(MirrorState.Denied);
                return;
            }

            DiagnosticsService.Write("Mirror", exception);
            SetState(MirrorState.Failed);
        }
    }

    private async Task StopCoreAsync()
    {
        await CleanupAsync();
        if (_state != MirrorState.Off)
        {
            SetState(MirrorState.Off);
        }
    }

    private async Task CleanupAsync()
    {
        if (_reader is { } reader)
        {
            reader.FrameArrived -= OnFrameArrived;
            _reader = null;

            try
            {
                await reader.StopAsync();
            }
            catch
            {
                // The device may already be gone.
            }

            reader.Dispose();
        }

        if (_capture is { } capture)
        {
            capture.Failed -= OnCaptureFailed;
            _capture = null;
            capture.Dispose();
        }

        lock (_frameGate)
        {
            _frame = Array.Empty<byte>();
            _copyBuffer = null;
            _format = null;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                return;
            }

            if (!Monitor.TryEnter(_frameGate))
            {
                return;
            }

            var copied = false;
            try
            {
                SoftwareBitmap? converted = null;
                try
                {
                    if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                    {
                        converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                        bitmap = converted;
                    }

                    var width = bitmap.PixelWidth;
                    var height = bitmap.PixelHeight;
                    var byteCount = width * height * 4;

                    if (_frame.Length != byteCount)
                    {
                        _frame = new byte[byteCount];
                    }

                    if (_copyBuffer is null || _copyBuffer.Capacity < (uint)byteCount)
                    {
                        _copyBuffer = new Windows.Storage.Streams.Buffer((uint)byteCount);
                    }

                    bitmap.CopyToBuffer(_copyBuffer);

                    // A reused IBuffer can still report the previous, larger
                    // length; pin it to this frame before reading.
                    _copyBuffer.Length = (uint)byteCount;

                    using var dataReader = DataReader.FromBuffer(_copyBuffer);
                    dataReader.ReadBytes(_frame);

                    _format = new MirrorFrameInfo(width, height, width * 4);
                    copied = true;
                }
                finally
                {
                    converted?.Dispose();
                }
            }
            finally
            {
                Monitor.Exit(_frameGate);
            }

            if (copied)
            {
                FrameReady?.Invoke();
            }
        }
        catch
        {
            // A dropped frame must never take the app down.
        }
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        DiagnosticsService.WriteLine("Mirror", $"Capture failed: 0x{args.Code:X8} {args.Message}");
        _ = FailCaptureAsync();
    }

    private async Task FailCaptureAsync()
    {
        try
        {
            await _captureGate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await CleanupAsync();
            SetState(MirrorState.Failed);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private void SetState(MirrorState state)
    {
        if (_disposed || _state == state)
        {
            return;
        }

        _state = state;

        try
        {
            StateChanged?.Invoke(state);
        }
        catch
        {
            // A subscriber must not be able to break the capture pipeline.
        }
    }

    private static async Task<DeviceInformation?> FindCameraAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (devices.Count == 0)
            {
                return null;
            }

            var colorDevices = devices.Where(device => !LooksInfrared(device)).ToList();
            var pool = colorDevices.Count > 0 ? colorDevices : devices.ToList();

            return pool.FirstOrDefault(
                       device => device.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Front)
                   ?? pool[0];
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksInfrared(DeviceInformation device)
        => device.Name.Contains("infrared", StringComparison.OrdinalIgnoreCase)
           || device.Name.Contains("windows hello", StringComparison.OrdinalIgnoreCase)
           || device.Name.StartsWith("IR ", StringComparison.OrdinalIgnoreCase);

    private static MediaFrameFormat? ChooseFormat(MediaFrameSource source)
    {
        try
        {
            var formats = source.SupportedFormats
                .Where(format => format.VideoFormat.Width >= 320 && format.VideoFormat.Height >= 180)
                .ToList();

            if (formats.Count == 0)
            {
                return null;
            }

            // The panel is wider than it is tall; prefer 16:9 before raw pixels.
            return formats
                .OrderBy(format => Math.Abs(
                    (double)format.VideoFormat.Width / format.VideoFormat.Height - 16.0 / 9.0))
                .ThenBy(format => Math.Abs(
                    format.VideoFormat.Width * format.VideoFormat.Height
                    - PreferredWidth * PreferredHeight))
                .First();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDenied(Exception exception)
        => exception is UnauthorizedAccessException
           || exception.HResult == unchecked((int)0x80070005);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_ownerGate)
        {
            _owners.Clear();
        }

        if (_reader is { } reader)
        {
            reader.FrameArrived -= OnFrameArrived;
            reader.Dispose();
            _reader = null;
        }

        if (_capture is { } capture)
        {
            capture.Failed -= OnCaptureFailed;
            capture.Dispose();
            _capture = null;
        }

        lock (_frameGate)
        {
            _frame = Array.Empty<byte>();
            _copyBuffer = null;
            _format = null;
        }
    }
}
