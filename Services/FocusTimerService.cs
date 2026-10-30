using System;
using System.Diagnostics;
using System.Timers;
using Timer = System.Timers.Timer;

namespace TolkIsland.Services;

public enum FocusTimerRunState
{
    Stopped,
    Running,
    Paused
}

public sealed class FocusTimerService : IDisposable
{
    private const double TickIntervalMs = 1000;

    private readonly object _sync = new();
    private readonly Stopwatch _running = new();

    private readonly Timer _tick = new(TickIntervalMs) { AutoReset = false };

    private TimeSpan _accumulated;
    private TimeSpan _target;
    private FocusTimerRunState _status = FocusTimerRunState.Stopped;
    private bool _stopwatchMode;
    private bool _pomodoro;
    private bool _breakPhase;
    private int _pomodoroRound;
    private int _pomodoroRounds = 4;
    private TimeSpan _workLength = TimeSpan.FromMinutes(25);
    private TimeSpan _breakLength = TimeSpan.FromMinutes(5);
    private TimeSpan _longBreakLength = TimeSpan.FromMinutes(15);
    private bool _disposed;

    public FocusTimerRunState Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_sync)
            {
                return CurrentElapsedLocked();
            }
        }
    }

    public TimeSpan Target
    {
        get
        {
            lock (_sync)
            {
                return _target;
            }
        }
    }

    public double Progress
    {
        get
        {
            lock (_sync)
            {
                return _target <= TimeSpan.Zero
                    ? 0
                    : Math.Clamp(CurrentElapsedLocked().TotalMilliseconds / _target.TotalMilliseconds, 0, 1);
            }
        }
    }

    public bool IsStopwatch
    {
        get
        {
            lock (_sync)
            {
                return _stopwatchMode;
            }
        }
    }

    public bool IsPomodoro
    {
        get
        {
            lock (_sync)
            {
                return _pomodoro;
            }
        }
    }

    public bool IsBreak
    {
        get
        {
            lock (_sync)
            {
                return _breakPhase;
            }
        }
    }

    public int PomodoroRound
    {
        get
        {
            lock (_sync)
            {
                return _pomodoroRound;
            }
        }
    }

    public event Action? StateChanged;

    public event Action<TimeSpan>? ElapsedChanged;

    public FocusTimerService()
    {
        _tick.Elapsed += (_, _) => Tick();
    }

    public void Start(TimeSpan target)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _target = target < TimeSpan.Zero ? TimeSpan.Zero : target;
            _stopwatchMode = false;
            _pomodoro = false;
            _breakPhase = false;
            _pomodoroRound = 0;
            _accumulated = TimeSpan.Zero;
            _running.Reset();

            if (_target <= TimeSpan.Zero)
            {
                _status = FocusTimerRunState.Stopped;
                _tick.Stop();
            }
            else
            {
                _running.Restart();
                _status = FocusTimerRunState.Running;
                ScheduleNextTickLocked();
            }
        }

        RaiseStateChanged();
    }

    public void StartStopwatch()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _target = TimeSpan.Zero;
            _accumulated = TimeSpan.Zero;
            _stopwatchMode = true;
            _pomodoro = false;
            _breakPhase = false;
            _pomodoroRound = 0;
            _running.Restart();
            _status = FocusTimerRunState.Running;
            ScheduleNextTickLocked();
        }

        RaiseStateChanged();
    }

    public void StartPomodoro(
        TimeSpan? work = null,
        TimeSpan? rest = null,
        int rounds = 4,
        TimeSpan? longBreak = null)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _workLength = work is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromMinutes(25);
            _breakLength = rest is { } r && r > TimeSpan.Zero ? r : TimeSpan.FromMinutes(5);
            _longBreakLength = longBreak is { } lb && lb > TimeSpan.Zero ? lb : TimeSpan.FromMinutes(15);
            _pomodoroRounds = Math.Clamp(rounds, 1, 12);
            _pomodoro = true;
            _stopwatchMode = false;
            _breakPhase = false;
            _pomodoroRound = 1;
            BeginPhaseLocked(_workLength);
        }

        RaiseStateChanged();
    }

    public void Toggle()
    {
        var changed = false;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_status == FocusTimerRunState.Running)
            {
                _accumulated = CurrentElapsedLocked();
                _running.Reset();
                _status = FocusTimerRunState.Paused;
                _tick.Stop();
                changed = true;
            }
            else if (_status == FocusTimerRunState.Paused)
            {
                _running.Restart();
                _status = FocusTimerRunState.Running;
                ScheduleNextTickLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaiseStateChanged();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _running.Reset();
            _accumulated = TimeSpan.Zero;
            _target = TimeSpan.Zero;
            _stopwatchMode = false;
            _pomodoro = false;
            _breakPhase = false;
            _pomodoroRound = 0;
            _status = FocusTimerRunState.Stopped;
            _tick.Stop();
        }

        RaiseStateChanged();
    }

    private void Tick()
    {
        TimeSpan elapsed;
        var completed = false;

        lock (_sync)
        {
            if (_disposed || _status != FocusTimerRunState.Running)
            {
                return;
            }

            elapsed = CurrentElapsedLocked();

            if (!_stopwatchMode && _target > TimeSpan.Zero && elapsed >= _target)
            {
                elapsed = _target;
                _accumulated = _target;
                _running.Reset();
                _tick.Stop();

                if (_pomodoro)
                {
                    AdvancePomodoroLocked();
                    completed = true;
                }
                else
                {
                    _status = FocusTimerRunState.Stopped;
                    completed = true;
                }
            }
            else
            {
                ScheduleNextTickLocked();
            }
        }

        if (completed)
        {
            RaiseStateChanged();
        }

        ElapsedChanged?.Invoke(elapsed);
    }

    private void AdvancePomodoroLocked()
    {
        if (_breakPhase)
        {
            if (_pomodoroRound >= _pomodoroRounds)
            {
                _status = FocusTimerRunState.Stopped;
                _pomodoro = false;
                _breakPhase = false;
                return;
            }

            _pomodoroRound++;
            _breakPhase = false;
            BeginPhaseLocked(_workLength);
            return;
        }

        _breakPhase = true;
        var rest = _pomodoroRound >= _pomodoroRounds ? _longBreakLength : _breakLength;
        BeginPhaseLocked(rest);
    }

    private void BeginPhaseLocked(TimeSpan target)
    {
        _target = target;
        _accumulated = TimeSpan.Zero;
        _running.Restart();
        _status = FocusTimerRunState.Running;
        ScheduleNextTickLocked();
    }

    private void ScheduleNextTickLocked()
    {
        if (_stopwatchMode)
        {
            _tick.Interval = TickIntervalMs;
            _tick.Start();
            return;
        }

        var remaining = _target - CurrentElapsedLocked();
        var interval = remaining <= TimeSpan.Zero
            ? 20
            : Math.Min(TickIntervalMs, remaining.TotalMilliseconds);

        _tick.Interval = Math.Max(20, interval);
        _tick.Start();
    }

    private TimeSpan CurrentElapsedLocked()
    {
        if (_status != FocusTimerRunState.Running)
        {
            return _accumulated;
        }

        var elapsed = _accumulated + _running.Elapsed;
        return _target > TimeSpan.Zero && elapsed > _target ? _target : elapsed;
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke();
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
            _tick.Stop();
            _tick.Dispose();
            _running.Reset();
            _accumulated = TimeSpan.Zero;
            _target = TimeSpan.Zero;
            _stopwatchMode = false;
            _status = FocusTimerRunState.Stopped;
        }
    }
}
