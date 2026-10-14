using System;
using System.Runtime.Versioning;
using System.Threading;

namespace WinTester.Tools.Fans.Gigabyte;

// Dedicated background thread that owns every GB_WMIACPI call.
//
// This thread is required for correctness, not just an optimization. FanControlWindow polls on a WPF
// DispatcherTimer (the UI thread), and a single GB_WMIACPI invoke can take tens to hundreds of
// milliseconds (it traps into ACPI). Several of them per tier change, on the UI thread, would visibly
// freeze the window and make the sliders unusable while being dragged. So the window's poll only ever
// reads an in-memory snapshot this thread maintains, and a slider drag only signals a target; nothing on
// the UI thread touches WMI.
[SupportedOSPlatform("windows")]
internal sealed class GigabyteFanWorker
{
    private const int PollIntervalMs = 350;
    private const int HeartbeatIntervalMs = 2500;

    private readonly GigabyteWmiDriver _driver;
    private readonly GigabyteFanStateMachine _stateMachine;
    private readonly AutoResetEvent _applySignal = new(false);
    private readonly object _targetLock = new();

    private Thread? _thread;
    private volatile bool _running;

    private byte _pendingCpuDuty = GigabyteFanTarget.Unset;
    private byte _pendingGpuDuty = GigabyteFanTarget.Unset;
    private bool _pendingReset;

    private DateTime _lastHeartbeat = DateTime.MinValue;

    // Written only by the worker thread, read only by the UI thread. Reference assignment of a record
    // class is atomic, so a reader always sees a whole consistent snapshot rather than a half-updated
    // one — which is why this is a swapped immutable object rather than four separate volatile fields.
    private volatile GigabyteTelemetry _telemetry = GigabyteTelemetry.Empty;

    public GigabyteTelemetry Telemetry => _telemetry;
    public GigabyteFanMode CurrentMode => _stateMachine.CurrentMode;
    public GigabyteFanStateMachine StateMachine => _stateMachine;

    public GigabyteFanWorker(GigabyteWmiDriver driver, GigabyteFanStateMachine stateMachine)
    {
        _driver = driver;
        _stateMachine = stateMachine;
    }

    public void Start()
    {
        if (_thread is not null) return;

        _running = true;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "WinTester_GigabyteFanWorker"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _applySignal.Set();

        try
        {
            // Long enough for a slow in-flight WMI tick to finish, so the caller's own reset-to-auto on
            // window close cannot interleave with a pending manual write and leave the EC in a mode
            // nobody asked for.
            _thread?.Join(2000);
        }
        catch
        {
            // A worker that won't join in 2s is already wedged in WMI; blocking window close further
            // would be worse than proceeding, and the caller still issues its own reset afterwards.
        }

        _thread = null;
    }

    public void SetTargetDuty(double percent, bool isGpu)
    {
        var duty = (byte)Math.Clamp(Math.Round(percent), 0, 100);

        lock (_targetLock)
        {
            if (isGpu) _pendingGpuDuty = duty;
            else _pendingCpuDuty = duty;

            _pendingReset = false;
        }

        _applySignal.Set();
    }

    public void RequestResetToAuto()
    {
        lock (_targetLock)
        {
            _pendingCpuDuty = GigabyteFanTarget.Unset;
            _pendingGpuDuty = GigabyteFanTarget.Unset;
            _pendingReset = true;
        }

        _applySignal.Set();
    }

    // Also called directly (outside the loop) on the close path, so a reset still reaches the hardware
    // after Stop() has already joined the thread.
    public void Tick()
    {
        if (!_driver.EnsureConnected()) return;

        GigabyteFanTarget target;
        lock (_targetLock)
        {
            target = new GigabyteFanTarget(_pendingCpuDuty, _pendingGpuDuty, _pendingReset);
        }

        _stateMachine.ProcessTarget(target);

        DateTime now = DateTime.UtcNow;
        if ((now - _lastHeartbeat).TotalMilliseconds >= HeartbeatIntervalMs)
        {
            _stateMachine.Heartbeat();
            _lastHeartbeat = now;
        }

        // getRpm1/getRpm2 come back byte-swapped on AORUS-line boards. Temps are unaffected.
        uint cpuRpm = GigabyteRpmByteSwap.Apply(_driver.GetMethod("getRpm1"));
        uint gpuRpm = GigabyteRpmByteSwap.Apply(_driver.GetMethod("getRpm2"));
        uint cpuTemp = _driver.GetMethod("getCpuTemp");
        uint gpuTemp = _driver.GetMethod("getGpuTemp1");

        GigabyteTelemetry last = _telemetry;

        // During MaxTurbo, Overdrive cycles ADJF every few seconds and the tachometer briefly reads 0
        // across the transition; hold the last good reading rather than flashing 0 RPM while the fan is
        // audibly at full speed. In every other mode a 0 is trusted, since EC auto can legitimately park
        // the fans at a cool idle.
        bool maskZeroRpm = _stateMachine.CurrentMode == GigabyteFanMode.MaxTurbo;

        _telemetry = new GigabyteTelemetry(
            CpuRpm: cpuRpm == 0 && maskZeroRpm ? last.CpuRpm : cpuRpm,
            GpuRpm: gpuRpm == 0 && maskZeroRpm ? last.GpuRpm : gpuRpm,
            CpuTemp: cpuTemp > 0 ? cpuTemp : last.CpuTemp,
            GpuTemp: gpuTemp > 0 ? gpuTemp : last.GpuTemp,
            HasRead: true);
    }

    private void WorkerLoop()
    {
        while (_running)
        {
            _applySignal.WaitOne(PollIntervalMs);
            if (!_running) break;

            try
            {
                Tick();
            }
            catch
            {
                // Never let a single bad WMI tick kill the worker, or the fans would be stranded in
                // whatever manual mode was last applied with nothing left running to reset them.
            }
        }
    }
}

// Immutable telemetry snapshot swapped in wholesale by the worker thread. HasRead distinguishes "the
// hardware genuinely reports 0 RPM" from "no successful read has happened yet," so the window can show
// "--" rather than a misleading 0 before the first tick lands.
internal sealed record GigabyteTelemetry(
    uint CpuRpm,
    uint GpuRpm,
    uint CpuTemp,
    uint GpuTemp,
    bool HasRead)
{
    public static readonly GigabyteTelemetry Empty = new(0, 0, 0, 0, HasRead: false);
}
