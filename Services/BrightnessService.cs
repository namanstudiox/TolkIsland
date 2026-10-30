using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Timer = System.Threading.Timer;

namespace TolkIsland.Services;

public sealed class BrightnessService : IDisposable
{
    private readonly object _sync = new();
    private readonly List<IntPtr> _physicalMonitors = new();

    private IntPtr _target;
    private Timer? _poll;
    private int _lastPercent = -1;
    private volatile bool _disposed;

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return _target != IntPtr.Zero;
            }
        }
    }

    public event Action<int>? BrightnessChanged;

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _target != IntPtr.Zero)
            {
                return;
            }

            DiscoverLocked();
            if (_target != IntPtr.Zero)
            {
                _poll ??= new Timer(_ => Poll(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
        }
    }

    public bool TrySet(int percent)
    {
        lock (_sync)
        {
            if (_disposed || _target == IntPtr.Zero)
            {
                return false;
            }

            percent = Math.Clamp(percent, 0, 100);
            if (!GetMonitorBrightness(_target, out var min, out _, out var max))
            {
                return false;
            }

            var value = min + (uint)Math.Round((max - min) * (percent / 100.0));
            if (!SetMonitorBrightness(_target, value))
            {
                return false;
            }

            _lastPercent = percent;
            return true;
        }
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        if (!SettingsService.Current.BrightnessEnabled)
        {
            return;
        }

        int percent;
        lock (_sync)
        {
            if (_target == IntPtr.Zero
                || !GetMonitorBrightness(_target, out var min, out var current, out var max)
                || max <= min)
            {
                return;
            }

            percent = (int)Math.Round((current - min) * 100.0 / (max - min));
        }

        if (percent < 0 || percent > 100)
        {
            return;
        }

        var previous = Interlocked.Exchange(ref _lastPercent, percent);
        if (previous >= 0 && previous != percent)
        {
            BrightnessChanged?.Invoke(percent);
        }
    }

    private void DiscoverLocked()
    {
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, OnMonitor, IntPtr.Zero);
        }
        catch
        {
        }
    }

    private bool OnMonitor(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data)
    {
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0)
            {
                return true;
            }

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors))
            {
                return true;
            }

            foreach (var physical in monitors)
            {
                _physicalMonitors.Add(physical.hPhysicalMonitor);

                if (_target == IntPtr.Zero
                    && GetMonitorBrightness(physical.hPhysicalMonitor, out var min, out _, out var max)
                    && max > min)
                {
                    _target = physical.hPhysicalMonitor;
                }
            }
        }
        catch
        {
        }

        return true;
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
            _poll?.Dispose();
            _poll = null;

            if (_physicalMonitors.Count > 0)
            {
                try
                {
                    DestroyPhysicalMonitors((uint)_physicalMonitors.Count,
                        _physicalMonitors.ConvertAll(handle => new PHYSICAL_MONITOR
                        {
                            hPhysicalMonitor = handle,
                            szPhysicalMonitorDescription = string.Empty
                        }).ToArray());
                }
                catch
                {
                }
            }

            _physicalMonitors.Clear();
            _target = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr monitor,
        uint count,
        [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(
        IntPtr monitor,
        out uint minimum,
        out uint current,
        out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
}
