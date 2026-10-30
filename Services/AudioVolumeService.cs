using System;
using System.Runtime.InteropServices;
using System.Threading;
using Timer = System.Threading.Timer;

namespace TolkIsland.Services;

public sealed class AudioVolumeService : IDisposable
{
    private readonly object _sync = new();

    private IAudioEndpointVolume? _endpoint;
    private VolumeCallback? _callback;
    private string? _endpointId;
    private Timer? _watch;
    private volatile bool _disposed;

    public event Action<double, bool>? VolumeChanged;

    public bool IsAvailable => _endpoint is not null;

    public bool TryGetVolume(out double level, out bool muted)
    {
        lock (_sync)
        {
            level = 0;
            muted = false;
            if (_endpoint is null)
            {
                return false;
            }

            try
            {
                if (_endpoint.GetMasterVolumeLevelScalar(out var scalar) < 0
                    || _endpoint.GetMute(out var isMuted) < 0)
                {
                    return false;
                }

                level = Math.Clamp(scalar, 0, 1);
                muted = isMuted;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool AdjustVolume(double delta)
    {
        lock (_sync)
        {
            if (_endpoint is null)
            {
                return false;
            }

            try
            {
                if (_endpoint.GetMasterVolumeLevelScalar(out var scalar) < 0
                    || _endpoint.GetMute(out var muted) < 0)
                {
                    return false;
                }

                var next = Math.Clamp(scalar + delta, 0, 1);
                var context = Guid.Empty;
                if (muted && next > 0 && _endpoint.SetMute(false, ref context) < 0)
                {
                    return false;
                }

                if (_endpoint.SetMasterVolumeLevelScalar((float)next, ref context) < 0)
                {
                    return false;
                }

                VolumeChanged?.Invoke(next, false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Start()
    {
        EnsureEndpoint();

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _watch ??= new Timer(_ => EnsureEndpoint(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        }
    }

    private void EnsureEndpoint()
    {
        if (!SettingsService.Current.VolumeEnabled)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) < 0 ||
                    device is null)
                {
                    return;
                }

                string? id = null;
                if (device.GetId(out var deviceId) < 0)
                {
                    id = null;
                }
                else
                {
                    id = deviceId;
                }

                if (_endpoint is not null && (id is null || id == _endpointId))
                {
                    return;
                }

                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var comObject) < 0 ||
                    comObject is not IAudioEndpointVolume endpoint)
                {
                    return;
                }

                var callback = new VolumeCallback(this);
                if (endpoint.RegisterControlChangeNotify(callback) < 0)
                {
                    return;
                }

                var oldEndpoint = _endpoint;
                var oldCallback = _callback;

                _endpoint = endpoint;
                _callback = callback;
                _endpointId = id;

                if (oldEndpoint is not null && oldCallback is not null)
                {
                    try
                    {
                        oldEndpoint.UnregisterControlChangeNotify(oldCallback);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
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
            _watch?.Dispose();
            _watch = null;

            try
            {
                if (_endpoint is not null && _callback is not null)
                {
                    _endpoint.UnregisterControlChangeNotify(_callback);
                }
            }
            catch
            {
            }

            _endpoint = null;
            _callback = null;
            _endpointId = null;
        }
    }

    private void OnNotification(IntPtr data)
    {
        if (_disposed || data == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var info = Marshal.PtrToStructure<AudioVolumeNotificationData>(data);
            VolumeChanged?.Invoke(Math.Clamp(info.MasterVolume, 0, 1), info.Muted != 0);
        }
        catch
        {
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly AudioVolumeService _owner;

        public VolumeCallback(AudioVolumeService owner) => _owner = owner;

        public void OnNotify(IntPtr data) => _owner.OnNotification(data);
    }
}

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioVolumeNotificationData
{
    public Guid EventContext;
    public int Muted;
    public float MasterVolume;
    public uint ChannelCount;
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? device);

    [PreserveSig]
    int GetDevice(string id, out IMMDevice? device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

    [PreserveSig]
    int OpenPropertyStore(int stgmAccess, out IntPtr properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

    [PreserveSig]
    int GetState(out int state);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    [PreserveSig]
    int GetChannelCount(out uint count);

    [PreserveSig]
    int SetMasterVolumeLevel(float level, ref Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float level);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComVisible(true)]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback
{
    void OnNotify(IntPtr data);
}
