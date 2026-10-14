using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WinTester.App.Controls;
using WinTester.App.Win32;
using WinTester.Branding;
using WinTester.Core;
using WinTester.Core.Fans;
using WinTester.Core.Session;
using WinTester.Sensors.HwInfo;
using WinTester.Tests.Cpu;
using WinTester.Tests.Gpu;
using WinTester.Tests.Memory;
using WinTester.Tests.Storage;
using WinTester.Tools.Activation;
using WinTester.Tools.BatteryDrain;
using WinTester.Tools.BatteryCharge;
using WinTester.Tools.Fans;

namespace WinTester.App.ViewModels;

// Backs MainWindow: owns the Tab 1 module list, Start/Select-All state, and the running-state modal.
// Owns one SessionDataStore per run, written to Sessions/<timestamp>/ next to the executable, plus a
// 5s hardware snapshot log distinct from the modal's 1.5s display poll.
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly BrandProfile _brand;
    private readonly ISensorService _sensorService;

    // Shared Advanced Mode settings for the GPU and Power tiles; both modules reference the same
    // instance so their Advanced Mode dialogs edit the same object.
    private readonly GlobalTestOptions _globalTestOptions = new();
    private CancellationTokenSource? _runCts;
    private readonly Window _ownerWindow;
    private readonly DispatcherTimer _telemetryTimer;

    // Always-on, unlike _telemetryTimer. Backs the footer's live CPU/GPU/RAM readout on both tabs.
    private readonly DispatcherTimer _footerTimer;

    // Root folder for session subfolders, next to the executable so the app stays copy-and-run.
    private static readonly string SessionsRootDir = Path.Combine(AppContext.BaseDirectory, "Sessions");

    // Root folder for bundled vendor tools/binaries, next to the executable.
    private static readonly string VendorRootDir = Path.Combine(AppContext.BaseDirectory, "vendor");

    // Owns the HWiNFO process for the main test suite, separate from the instance SensorsWindow owns for
    // its own window session. Restarted fresh at the start of every run (see RunQueueAsync) so a
    // multi-pass suite never reuses one HWiNFO process, and its own running Min/Max/Avg tracking, across
    // passes.
    private readonly HwInfoProcessManager _hwInfoProcess = new(VendorRootDir);

    // How long a run waits for HWiNFO's shared memory to become readable after a fresh launch before
    // giving up and proceeding without HWiNFO data for that run.
    private static readonly TimeSpan HwInfoReadyTimeout = TimeSpan.FromMinutes(1);

    // True only between this view model launching HWiNFO for a run and stopping it again, so closing the
    // window never kills an instance the operator started themselves.
    private bool _hwInfoLaunchedByRun;

    // Non-null only for a run whose HWiNFO shared memory came up in time; polled on the 5s session tick.
    private HwInfoEventSampler? _hwInfoSampler;

    // Stops the run's HWiNFO instance if one is still up (crash or forced close mid-run).
    public void ShutdownHwInfo()
    {
        if (!_hwInfoLaunchedByRun) return;
        _hwInfoProcess.Shutdown();
        _hwInfoLaunchedByRun = false;
    }

    // 5s hardware poll written to the current run's samples.jsonl, separate from _telemetryTimer
    // (1.5s, UI-only).
    private readonly DispatcherTimer _sessionSampleTimer;

    private SessionDataStore? _currentSession;

    // Id of the currently running Tab1Modules entry, or null when idle. Drives which telemetry rows
    // apply for the current tick; separate from CurrentTestName, the modal header's display name.
    private string? _currentModuleId;

    // Bindable mirror of _currentModuleId for TestPatternControl, which swaps LCD test patterns for
    // the GPU output placeholder when the GPU test is running. Kept separate so XAML never binds
    // straight to backing state.
    private string? _currentTestId;
    public string? CurrentTestId
    {
        get => _currentTestId;
        private set { _currentTestId = value; OnPropertyChanged(); }
    }

    // Category accent colors for the top-left system stats overlay.
    private static readonly Brush CpuAccentBrush = FrozenBrush("#3ED6FF");
    private static readonly Brush GpuAccentBrush = FrozenBrush("#FF4FC3");
    private static readonly Brush RamAccentBrush = FrozenBrush("#FFC94D");
    private static readonly Brush FanAccentBrush = FrozenBrush("#8CFF6B");
    private static readonly Brush WarningAccentBrush = FrozenBrush("#FF5C5C");

    private static Brush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public ObservableCollection<TestModuleViewModel> Tab1Modules { get; }

    // Tab 2 standalone tools: separate, thinner collections from Tab1Modules, split by category
    // so MainWindow.xaml can render each as its own labeled group.
    public ObservableCollection<StandaloneToolViewModel> SystemTools { get; }
    public ObservableCollection<StandaloneToolViewModel> PowerTools { get; }
    public ObservableCollection<StandaloneToolViewModel> HidTools { get; }
    public ObservableCollection<StandaloneToolViewModel> DeploymentTools { get; }

    // Backs the running-state modal's telemetry grid; rebuilt wholesale each poll tick.
    public ObservableCollection<TelemetryRowViewModel> TelemetryRows { get; } = new();

    // Backs the always-on system stats overlay.
    public ObservableCollection<SystemStatRowViewModel> SystemStatRows { get; } = new();

    // Backs the main window footer (both tabs). Pre-formatted display strings ("62°C",
    // "19.6 / 32.0 GB", "N/A"). Refreshed by RefreshFooterTelemetry on _footerTimer's 2s tick.
    private string _footerCpuTemp = "N/A";
    public string FooterCpuTemp
    {
        get => _footerCpuTemp;
        private set { _footerCpuTemp = value; OnPropertyChanged(); }
    }

    private string _footerGpuCoreTemp = "N/A";
    public string FooterGpuCoreTemp
    {
        get => _footerGpuCoreTemp;
        private set { _footerGpuCoreTemp = value; OnPropertyChanged(); }
    }

    private string _footerGpuHotspotTemp = "N/A";
    public string FooterGpuHotspotTemp
    {
        get => _footerGpuHotspotTemp;
        private set { _footerGpuHotspotTemp = value; OnPropertyChanged(); }
    }

    private string _footerRamFreeTotal = "N/A";
    public string FooterRamFreeTotal
    {
        get => _footerRamFreeTotal;
        private set { _footerRamFreeTotal = value; OnPropertyChanged(); }
    }

    // Exposed as a bound property rather than x:Static so it sits alongside the other footer segments
    // consistently.
    public string AppVersionDisplay => $"ver. {AppInfo.Version}";

    // Backs StorageBenchmarkControl, the storage test's running-state visual. Updated from
    // OnStorageLiveSpeedChanged rather than exposing StorageStressTestModule's raw event args to XAML.
    public StorageBenchmarkViewModel StorageBenchmarkViewModel { get; } = new();

    public ICommand StartCommand { get; }
    public ICommand SelectAllToggleCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RunStandaloneCommand { get; }
    public ICommand OpenAdvancedSettingsCommand { get; }

    public MainViewModel(BrandProfile brand, Window ownerWindow, ISensorService sensorService)
    {
        _brand = brand;
        _ownerWindow = ownerWindow;
        _sensorService = sensorService;

        // 1.5s modal display refresh, independent of and faster than the 5s on-disk SensorSample
        // cadence. Drives display only, never writes to disk.
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _telemetryTimer.Tick += (_, _) => RefreshLiveDisplays();

        _sessionSampleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _sessionSampleTimer.Tick += (_, _) => AppendSessionSnapshot();

        // Footer's live CPU/GPU/RAM readout runs on its own 2s timer, independent of _telemetryTimer
        // (which only runs while IsRunning).
        _footerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _footerTimer.Tick += (_, _) => RefreshFooterTelemetry();
        RefreshFooterTelemetry();
        _footerTimer.Start();

        // Composition root for Tab 1 tests; the tile grid is fully data-driven off this collection.
        Tab1Modules = new ObservableCollection<TestModuleViewModel>
        {
            Tab1Tile("Tests.Cpu", "cpu", "CPU", "TileCpu", () => new CpuStressTestModule()),
            // sensorService sizes the post-sweep VRAM test off detected VRAM and cross-checks the
            // Vulkan sweep against ISensorService's GPU list, which lists integrated GPUs more
            // reliably than FurMark's own --gpuinfo probe.
            Tab1Tile("Tests.Gpu", "gpu", "GPU", "TileGpu", () => new GpuStressTestModule(
                getEmbedRect: () => GetGpuEmbedRect(),
                getParentHwnd: () => GetGpuEmbedParentHwnd(),
                sensorService: _sensorService,
                omitGravityMarkDeviceIndex: false,
                // Re-asserts _runOverlayWindow's topmost z-order after GravityMark's window is
                // positioned. GravityMark always runs top-level, never reparented, reparenting
                // breaks its Vulkan surface.
                reassertOverlayTopmost: () => ReassertOverlayTopmost(),
                globalOptions: _globalTestOptions)),
            // LiveSpeedChanged/DisksAnnounced are StorageStressTestModule's extra events outside
            // ITestModule, subscribed once for the module's lifetime.
            Tab1Tile("Tests.Storage", "storage", "Storage", "TileStorage", () => CreateStorageModule()),
            // No sensorService here: RAM usage/temp is already covered by the shared 5s hardware
            // snapshot. This module's own telemetry is progress/errors from the memtest engine,
            // not hardware sensors.
            Tab1Tile("Tests.Memory", "memory", "Memory", "TileMemory", () => new MemoryStressTestModule()),
            // Battery Health (laptop-only) plus PSU/Adapter Power Budget (every machine). sensorService
            // feeds the battery/fan snapshot and the calibration pass's peak-power sampling.
            Tab1Tile("Tests.Power", "power", "Power", "TilePower", () => new WinTester.Tests.Power.PowerTestModule(sensorService: _sensorService, globalOptions: _globalTestOptions)),
        };

        foreach (var vm in Tab1Modules)
            vm.PropertyChanged += OnModuleViewModelPropertyChanged;

        // Tab 2 composition root, split into three category groups (System / Power / HID) so
        // MainWindow.xaml can render each under its own header. iconFileName args are raster PNGs
        // (vendor/icons/, 52x52, white-on-transparent). iconKey is a fallback for a tile missing
        // its own PNG.
        SystemTools = new ObservableCollection<StandaloneToolViewModel>
        {
            new("Windows Activation", () => ActivationWindow.Show(_ownerWindow), iconKey: "keyIcon", iconFileName: "product.png", moduleFolder: "Tools.Activation"),
            new("BitLocker Status", () => BitLockerWindow.Show(_ownerWindow), iconKey: "lockIcon", iconFileName: "locker.png", moduleFolder: "Tools.Bitlocker"),
            // Read-only view of every device Windows reports a Configuration Manager problem for, grouped
            // the way Device Manager groups them. No brand/report: nothing it shows is ever exported, and
            // every fix path hands off to Device Manager itself.
            new("Devices", () => WinTester.App.Controls.DevicesWindow.Show(_ownerWindow), iconKey: "gearIcon", iconFileName: "devices.png", moduleFolder: "Tools.Devices"),
            // System Restore manager: list, create, delete and restore to a point, plus the disk space limit,
            // interval and time to live. Needs administrator rights. Falls back to the vector gearIcon until
            // restore.png is dropped into vendor/icons/.
            new("Restore Points", () => WinTester.App.Controls.RestorePointsWindow.Show(_ownerWindow), iconKey: "gearIcon", iconFileName: "restore.png", moduleFolder: "Tools.RestorePoints"),
            // Read-only disk health view: S.M.A.R.T. status and key counters, TRIM state and the partition layout of one
            // physical disk chosen in a drop-down (starts on the Windows disk). Every change is handed off to Disk
            // Management. No brand/report. Falls back to the vector gearIcon until smart.png is in vendor/icons/.
            new("Disk Info", () => WinTester.App.Controls.DiskInfoWindow.Show(_ownerWindow), iconKey: "gearIcon", iconFileName: "smart.png", moduleFolder: "Tools.DiskInfo"),
            // Read-only list of WHEA hardware errors from the System event log, grouped where identical, with a
            // web-search link per error. No brand/report: nothing it shows is exported. Falls back to the
            // vector gearIcon until hwerrors.png is dropped into vendor/icons/.
            new("Hardware Errors", () => WinTester.App.Controls.HardwareErrorsWindow.Show(_ownerWindow), iconKey: "gearIcon", iconFileName: "hwerrors.png", moduleFolder: "Tools.HardwareErrors"),
            // Fan tool. Placed in System alongside Activation/BitLocker/Devices — the safety contract
            // that motivates keeping fan control isolated lives in the window itself, not in where its
            // tile sits.
            new("Fans", OpenFanControlWindow, iconKey: "gpuFanIcon", iconFileName: "fan.png", moduleFolder: "Tools.Fans"),
            // Standalone live sensor grid — every sensor either backend (LibreHardwareMonitor, plus
            // HWiNFO's shared-memory section when available) reports, unfiltered, with live value and
            // running min/max. No brand/report: this is a diagnostic view, nothing it shows is ever
            // exported. Falls back to the vector "cpuIcon" geometry if sensors.png isn't present yet in
            // vendor/icons/, same as any tile whose PNG hasn't been dropped in.
            new("Sensors", () => SensorsWindow.Show(_ownerWindow, _sensorService), iconKey: "cpuIcon", iconFileName: "sensors.png"),
        };

        PowerTools = new ObservableCollection<StandaloneToolViewModel>
        {
            // Long-running background operation with a live view. The window owns its own runner
            // and lifecycle; MainViewModel just opens it.
            new("Battery Drain", () => BatteryDrainWindow.Show(_ownerWindow, _sensorService, _brand), iconKey: "batteryIcon", iconFileName: "drain.png", moduleFolder: "Tools.BatteryDrain"),
            // Second sub-test in the Battery tile family (Wide Sweep / Quick Test / Peak Sweep
            // charge-curve logging). BrandProfile threads through so its HTML report picks up the
            // shop's white-label header/footer. Distinct icon from Battery Drain (charge.png vs drain.png).
            new("Battery Charge", () => BatteryChargeWindow.Show(_ownerWindow, _sensorService, _brand), iconKey: "batteryIcon", iconFileName: "charge.png", moduleFolder: "Tools.BatteryCharge"),
        };

        HidTools = new ObservableCollection<StandaloneToolViewModel>
        {
            // A resizable window is the one deliberate exception to every other Tab 2 tile's fixed-size
            // modal, so the (data-driven, quite wide) key diagram has room to breathe.
            new("Keyboard", () => WinTester.App.Controls.KeyboardTesterWindow.Show(_ownerWindow, _brand), iconKey: "keyIcon", iconFileName: "keyboard.png", moduleFolder: "Tools.KeyboardTester"),
            // Fullscreen, borderless, no report — unlike every other Tab 2 tile, this one has no brand
            // threaded through at all: nothing it shows is ever exported or printed.
            new("Mouse & Touch", () => WinTester.App.Controls.MouseTouchTesterWindow.Show(_ownerWindow), iconKey: "keyIcon", iconFileName: "mouse.png", moduleFolder: "Tools.MouseTester"),
            // Normal fixed-size modal like Activation/BitLocker, not fullscreen like Keyboard/Mouse & Touch
            // — this one has a device-picker UI to interact with rather than a full-screen input surface.
            // No brand/report either: it's a live preview + a RAM-only 30s clip, nothing gets exported.
            new("Webcam & Mic", () => WinTester.App.Controls.WebcamMicTesterWindow.Show(_ownerWindow), iconKey: "keyIcon", iconFileName: "webcam.png", moduleFolder: "Tools.WebcamMic"),
            // Normal fixed-size modal, same shape as Webcam & Mic. Plays vendor\Sounds\Title.mp3 through
            // the default output device with play/pause/seek/volume controls to confirm speakers/
            // headphones actually work. No brand/report: nothing here is ever exported.
            new("Audio", () => WinTester.App.Controls.AudioTesterWindow.Show(_ownerWindow), iconKey: "keyIcon", iconFileName: "speaker.png"),
            // Normal fixed-size modal, same shape as Webcam & Mic/Audio. One tile per physical USB port
            // (SetupAPI + usbioctl.h IOCTLs, WinTester.Tools.UsbValidator), live-polled so an operator can
            // plug something into every port in turn and watch it go green then PASS. No brand: the export
            // is its own simple self-contained HTML table, same posture as Hardware Errors/Devices.
            new("USB Validator", () => WinTester.App.Controls.UsbValidatorWindow.Show(_ownerWindow), iconKey: "keyIcon", iconFileName: "usb.png", moduleFolder: "Tools.UsbValidator"),
        };

        DeploymentTools = new ObservableCollection<StandaloneToolViewModel>
        {
            // Normal fixed-size modal. Edits the Windows OEM support info/logo (Settings > About) and can stage a
            // Push-Button Reset hook so it survives a reset. No brand/report: nothing it shows is exported. Falls
            // back to the vector gearIcon until oem.png is dropped into vendor/icons/.
            new("OEM Branding", () => WinTester.App.Controls.OemBrandingWindow.Show(_ownerWindow), iconKey: "gearIcon", iconFileName: "oem.png"),
            // Wizard that captures this install with USMT ScanState into C:\Recovery\Customizations\usmt.ppkg so
            // Reset this PC restores the shop's deployed state. Currently runs a simulation only (nothing is
            // captured or written). Falls back to the vector gearIcon until recovery.png is dropped into vendor/icons/.
            new("Recovery Provisioning", () => WinTester.App.Controls.RecoveryProvisioningWindow.Show(_ownerWindow), iconKey: "gearIcon", iconFileName: "recovery.png"),
        };

        StartCommand = new RelayCommand(async () => await RunSelectedAsync(), () => CanStart);
        SelectAllToggleCommand = new RelayCommand(ToggleSelectAll);
        CancelCommand = new RelayCommand(async () => await RequestCancelAsync());
        RunStandaloneCommand = new RelayCommand<TestModuleViewModel>(async vm => await RunStandaloneAsync(vm));
        OpenAdvancedSettingsCommand = new RelayCommand<TestModuleViewModel>(OpenAdvancedSettings);
    }

    // S147: builds one Tab 1 tile. create runs only when the module's assembly is next to the exe (ModuleScan); a missing or
    // unloadable assembly gives a greyed-out placeholder tile instead of stopping the app. create is always a lambda so the
    // module's type is not touched (loaded) until it is called.
    private static TestModuleViewModel Tab1Tile(string folder, string id, string name, string colorKey, Func<ITestModule> create)
    {
        TestModuleViewModel Placeholder() => new(new WinTester.App.Mocks.UnavailableTestModule(id, name, colorKey), folder);
        if (!ModuleScan.IsPresent(folder)) return Placeholder();
        try { return new TestModuleViewModel(create()); }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.FileLoadException or TypeLoadException or BadImageFormatException)
        {
            return Placeholder();
        }
    }

    private void OnModuleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TestModuleViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(IsNumberOfRunsEnabled));
            OnPropertyChanged(nameof(SelectAllButtonLabel));
            (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool CanStart => !IsRunning && Tab1Modules.Any(m => m.IsSelected);

    // Applies to the run it's set before; read fresh at the top of each RunQueueAsync call.
    // Default false: a full queue runs to completion regardless of individual failures.
    private bool _abortOnFail;
    public bool AbortOnFail
    {
        get => _abortOnFail;
        set { if (_abortOnFail == value) return; _abortOnFail = value; OnPropertyChanged(); }
    }

    // Repeats the same selected-tests queue back to back. Read fresh in RunSelectedAsync at Start
    // time. Setter clamps below 1 up to 1, since a bound TextBox has no other way to recover from
    // an empty field.
    private int _numberOfRuns = 1;
    public int NumberOfRuns
    {
        get => _numberOfRuns;
        set
        {
            var clamped = Math.Max(1, value);
            if (_numberOfRuns == clamped) return;
            _numberOfRuns = clamped;
            OnPropertyChanged();
        }
    }

    // When checked, RunSelectedAsync loops until manually cancelled instead of stopping after
    // NumberOfRuns. The Number of Runs field is disabled (IsNumberOfRunsEnabled) rather than
    // cleared, so the last-entered count is preserved if this is unchecked again.
    private bool _runInfinitely;
    public bool RunInfinitely
    {
        get => _runInfinitely;
        set
        {
            if (_runInfinitely == value) return;
            _runInfinitely = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNumberOfRunsEnabled));
        }
    }

    public bool IsNumberOfRunsEnabled => !RunInfinitely && CanStart;

    // When checked, RunQueueAsync skips HTML report generation (and the activation/system-identity/
    // voltage-range reads that only feed it) for every run this triggers. Session data
    // (events.jsonl/samples.jsonl under Sessions/<timestamp>/) is still written either way.
    private bool _disableReports;
    public bool DisableReports
    {
        get => _disableReports;
        set { if (_disableReports == value) return; _disableReports = value; OnPropertyChanged(); }
    }

    // Saved so the window restores to its actual pre-run state rather than a hardcoded default.
    private WindowState _preRunWindowState;
    private WindowStyle _preRunWindowStyle;
    private ResizeMode _preRunResizeMode;
    private bool _preRunTopmost;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(IsNumberOfRunsEnabled));
            (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();

            if (_isRunning)
            {
                RefreshLiveDisplays(); // show data immediately, don't wait for the first tick
                _telemetryTimer.Start();
                EnterFullscreen();
            }
            else
            {
                _telemetryTimer.Stop();
                TelemetryRows.Clear();
                SystemStatRows.Clear();
                CurrentTestProgressPercent = 0;
                CurrentStageText = "";
                CountdownText = "";
                ExitFullscreen();
            }
        }
    }

    // Auto-maximizing and removing chrome for the run's duration makes the window fullscreen; the
    // previous window state is saved so it returns afterward instead of staying maximized.

    // Whole-run keyboard lockdown (Escape/Alt+F4), owned here so its lifetime matches the fullscreen run.
    private InputLockdown? _inputLockdown;

    // Stats/Cancel/progress-bar UI lives in a separate top-level window to avoid WPF airspace
    // conflicts with FurmarkEmbedHost. Created in EnterFullscreen, closed in ExitFullscreen.
    private RunOverlayWindow? _runOverlayWindow;

    private void EnterFullscreen()
    {
        _preRunWindowState = _ownerWindow.WindowState;
        _preRunWindowStyle = _ownerWindow.WindowStyle;
        _preRunResizeMode = _ownerWindow.ResizeMode;
        _preRunTopmost = _ownerWindow.Topmost;

        // Order matters: WPF sizes a borderless maximized window using the un-chromed WindowStyle, so
        // WindowStyle must switch to None before maximizing, or a sliver of desktop can remain visible.
        _ownerWindow.WindowStyle = WindowStyle.None;
        _ownerWindow.ResizeMode = ResizeMode.NoResize;
        _ownerWindow.WindowState = WindowState.Maximized;
        _ownerWindow.Topmost = true; // no third-party UI or the taskbar should be reachable mid-run

        // Must be constructed on the UI thread. The hook swallows Escape system-wide, which also
        // blocks MainWindow's own Escape→CancelCommand keybinding, so this callback routes Escape
        // into the same Cancel flow via the hook's Dispatcher-posted path.
        _inputLockdown = new InputLockdown(onEscapePressed: () => CancelCommand.Execute(null));

        // Created and shown after _ownerWindow goes fullscreen/topmost; a freshly shown Topmost
        // window takes precedence in z-order over one shown earlier.
        _runOverlayWindow = new RunOverlayWindow(this);
        _runOverlayWindow.CoverPrimaryScreen();
        _runOverlayWindow.Show();
    }

    // Called from GravityMarkRunner's reassertOverlayTopmost callback after GravityMark's window is
    // positioned. Both windows are HWND_TOPMOST and near-monitor-sized, so topmost-band ordering
    // can't be trusted; toggling Topmost off then on re-triggers "freshly shown wins" precedence
    // without hiding/reshowing. No-op outside a run. Assumes this call chain resumes on the UI
    // thread's SynchronizationContext; needs Dispatcher.Invoke if ConfigureAwait(false) is added
    // upstream.
    private void ReassertOverlayTopmost()
    {
        if (_runOverlayWindow is null) return;
        _runOverlayWindow.Topmost = false;
        _runOverlayWindow.Topmost = true;
    }

    private void ExitFullscreen()
    {
        _runOverlayWindow?.Close();
        _runOverlayWindow = null;

        _ownerWindow.Topmost = _preRunTopmost;
        _ownerWindow.WindowState = _preRunWindowState;
        _ownerWindow.WindowStyle = _preRunWindowStyle;
        _ownerWindow.ResizeMode = _preRunResizeMode;

        _inputLockdown?.Dispose();
        _inputLockdown = null;
    }

    // Converts MainWindow's picture-frame bounds into the plain WindowScreenRect GpuStressTestModule/
    // Win32Interop use, so WinTester.Tests.Gpu doesn't need a WPF dependency. Falls back to the full
    // primary screen if _ownerWindow isn't a MainWindow.
    private WindowScreenRect GetGpuEmbedRect()
    {
        if (_ownerWindow is not MainWindow mainWindow)
            return WindowScreenRect.FullPrimaryScreen();

        var rect = mainWindow.GetGpuFramePixelRect();
        return new WindowScreenRect((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);
    }

    // Native HWND to reparent FurMark's window into. A cast failure returns zero, which FurmarkRunner
    // treats as "no parent supplied, use the positioned-overlay path."
    private IntPtr GetGpuEmbedParentHwnd()
    {
        if (_ownerWindow is not MainWindow mainWindow)
            return IntPtr.Zero;

        return mainWindow.GetGpuEmbedParentHandle();
    }

    // Constructs StorageStressTestModule and wires its LiveSpeedChanged/DisksAnnounced/
    // DriveTelemetryChanged events into StorageBenchmarkViewModel for the app's lifetime. Reuses
    // the same ISensorService instance GpuStressTestModule gets, for live drive temperature/SMART health.
    private StorageStressTestModule CreateStorageModule()
    {
        var module = new StorageStressTestModule(sensorService: _sensorService);
        module.DisksAnnounced += (_, disks) => StorageBenchmarkViewModel.Reset(disks);
        module.LiveSpeedChanged += OnStorageLiveSpeedChanged;
        module.DriveTelemetryChanged += (_, e) => StorageBenchmarkViewModel.ApplyTelemetry(e);
        return module;
    }

    // Assumes StorageStressTestModule's StartAsync call chain resumes on the UI thread's
    // SynchronizationContext; needs Dispatcher.Invoke if FioRunner's read loop gets
    // ConfigureAwait(false). Forwards to StorageBenchmarkViewModel.ApplyEvent, which finds the
    // right cell by DriveLetter and updates the matching bar.
    private void OnStorageLiveSpeedChanged(object? sender, StorageLiveEventArgs e) =>
        StorageBenchmarkViewModel.ApplyEvent(e);

    // Formats a nullable sensor reading, or "N/A" when not found. SensorSnapshot fields are
    // nullable rather than defaulting to 0 to avoid a misleading "0°C".
    private static string Fmt(double? value, string format, string suffix = "") =>
        value is { } v ? v.ToString(format) + suffix : "N/A";

    // Falls back to shared VRAM when dedicated VRAM is null; an iGPU has no "D3D Dedicated Memory"
    // sensor, so MemUsedGB/MemTotalGB stay null even though LHM's "D3D Shared Memory" numbers exist.
    // Labels the fallback "(shared)" to distinguish borrowed system RAM from real VRAM.
    private static string FormatVram(GpuTelemetry gpu)
    {
        if (gpu.MemTotalGB is not null)
            return $"{Fmt(gpu.MemUsedGB, "0.0")}/{Fmt(gpu.MemTotalGB, "0.0")}GB";

        if (gpu.SharedMemTotalGB is not null)
            return $"{Fmt(gpu.SharedMemUsedGB, "0.0")}/{Fmt(gpu.SharedMemTotalGB, "0.0")}GB (shared)";

        return "N/A";
    }

    // Builds both live-display collections from one shared sensor poll: TelemetryRows
    // (per-test-relevant rows) and SystemStatRows (the always-on overlay). Shows every detected GPU at once.
    // Footer's live readout, separate from RefreshLiveDisplays: runs continuously on both tabs and
    // only touches four fields. Primary GPU only (index 0).
    private void RefreshFooterTelemetry()
    {
        SensorSnapshot snapshot;
        try
        {
            snapshot = _sensorService.GetSnapshot();
        }
        catch
        {
            return; // sensor read failed; keep last good reading, retry next tick
        }

        FooterCpuTemp = Fmt(snapshot.Cpu?.PackageTempC, "0", "\u00B0C");

        var gpu = snapshot.Gpus.Count > 0 ? snapshot.Gpus[0] : null;
        FooterGpuCoreTemp = Fmt(gpu?.TempC, "0", "\u00B0C");
        FooterGpuHotspotTemp = Fmt(gpu?.HotspotTempC, "0", "\u00B0C");

        // Each field formats independently so a partial reading still shows something. "Free" needs
        // both Total and Used, falling back to N/A only when Total is unknown. RAM speed is appended
        // Task-Manager-style ("15.1 / 32.0 GB @ 4800MT/s"), omitted when unreadable.
        var speedSuffix = snapshot.Memory?.SpeedMhz is { } speed ? $" @ {speed:0}MT/s" : "";
        FooterRamFreeTotal = snapshot.Memory is { } mem
            ? mem.TotalGB is { } totalGb
                ? mem.UsedGB is { } usedGb
                    ? $"{Math.Max(totalGb - usedGb, 0):0.0} / {totalGb:0.0} GB{speedSuffix}"
                    : $"N/A / {totalGb:0.0} GB{speedSuffix}"
                : $"{Fmt(mem.UsedGB, "0.0")} used / N/A GB{speedSuffix}"
            : "N/A";
    }

    private void RefreshLiveDisplays()
    {
        SensorSnapshot snapshot;
        try
        {
            snapshot = _sensorService.GetSnapshot();
        }
        catch
        {
            return; // sensor read failed; skip this tick, retry next
        }

        var rows = new List<TelemetryRowViewModel>();

        if (snapshot.Cpu is { } cpu)
        {
            rows.Add(new TelemetryRowViewModel
            {
                Label = "CPU",
                Value = $"{Fmt(cpu.LoadPercent, "0", "%")}, {Fmt(cpu.PackageTempC, "0", "\u00B0C")}"
            });

            if (cpu.VrmTempC is { } vrm)
            {
                rows.Add(new TelemetryRowViewModel { Label = "CPU VRM", Value = $"{vrm:0}\u00B0C" });
            }
        }

        switch (_currentModuleId)
        {
            case "gpu":
                bool multiGpu = snapshot.Gpus.Count > 1;
                for (int i = 0; i < snapshot.Gpus.Count; i++)
                {
                    var gpu = snapshot.Gpus[i];
                    var label = multiGpu ? $"GPU{i}: {gpu.Name}" : $"GPU ({gpu.Name})";
                    rows.Add(new TelemetryRowViewModel
                    {
                        Label = label,
                        Value = $"{Fmt(gpu.LoadPercent, "0", "%")}, {Fmt(gpu.TempC, "0", "\u00B0C")}, " +
                                $"{FormatVram(gpu)}"
                    });
                }
                break;

            case "memory":
                if (snapshot.Memory is { } mem)
                {
                    rows.Add(new TelemetryRowViewModel
                    {
                        Label = "RAM",
                        Value = $"{Fmt(mem.UsedGB, "0.0")}/{Fmt(mem.TotalGB, "0.0")}GB"
                    });
                    if (mem.TempC is { } memTemp)
                        rows.Add(new TelemetryRowViewModel { Label = "RAM Temp", Value = $"{memTemp:0}\u00B0C" });
                }
                break;

            case "storage":
                // No signal yet for which physical drive is under test: shows the first detected device.
                var storage = snapshot.Storages.FirstOrDefault();
                if (storage is not null)
                {
                    rows.Add(new TelemetryRowViewModel { Label = storage.Name, Value = Fmt(storage.TempC, "0", "\u00B0C") });
                }
                break;

            case "power":
                // Shows battery telemetry on a laptop; desktops get nothing extra beyond the shared CPU row.
                if (snapshot.Battery is { } battery)
                {
                    rows.Add(new TelemetryRowViewModel
                    {
                        Label = "Battery",
                        Value = $"{Fmt(battery.Percent, "0", "%")}, {Fmt(battery.VoltageV, "0.00", "V")} ({battery.ChargeState})"
                    });
                }
                break;
        }

        TelemetryRows.Clear();
        foreach (var row in rows)
            TelemetryRows.Add(row);

        BuildSystemStatRows(snapshot);
    }

    // Builds the overlay's row set: every available reading, color-coded by category, shown
    // regardless of which test is running: unlike TelemetryRows' per-test filtering.
    private void BuildSystemStatRows(SensorSnapshot snapshot)
    {
        var rows = new List<SystemStatRowViewModel>();

        if (snapshot.SensorWarning is { } warning)
        {
            rows.Add(new SystemStatRowViewModel
            {
                Label = "SENSORS",
                Value = warning,
                AccentBrush = WarningAccentBrush,
                IsWarning = true
            });
        }

        if (snapshot.Cpu is { } cpu)
        {
            rows.Add(new SystemStatRowViewModel { Label = cpu.Name, Value = "", AccentBrush = CpuAccentBrush });
            rows.Add(new SystemStatRowViewModel { Label = "CPU Load", Value = Fmt(cpu.LoadPercent, "0", "%"), AccentBrush = CpuAccentBrush });
            rows.Add(new SystemStatRowViewModel { Label = "CPU Temp", Value = Fmt(cpu.PackageTempC, "0", "\u00B0C"), AccentBrush = CpuAccentBrush });
            rows.Add(new SystemStatRowViewModel { Label = "CPU Clock", Value = Fmt(cpu.ClockMhz, "0", " MHz"), AccentBrush = CpuAccentBrush });
            // HWiNFO's own package reading wins when HWiNFO is running: on some laptops the LibreHardwareMonitor
            // figure in PackagePowerW is the whole-platform sensor and tracks GPU load, not CPU load.
            rows.Add(new SystemStatRowViewModel { Label = "CPU Power", Value = Fmt(cpu.HwInfoPackagePowerW ?? cpu.PackagePowerW, "0.0", " W"), AccentBrush = CpuAccentBrush });
        }

        foreach (var gpu in snapshot.Gpus)
        {
            rows.Add(new SystemStatRowViewModel { Label = gpu.Name, Value = "", AccentBrush = GpuAccentBrush });
            rows.Add(new SystemStatRowViewModel { Label = "GPU Load", Value = Fmt(gpu.LoadPercent, "0", "%"), AccentBrush = GpuAccentBrush });
            rows.Add(new SystemStatRowViewModel { Label = "GPU Temp", Value = Fmt(gpu.TempC, "0", "\u00B0C"), AccentBrush = GpuAccentBrush });
            rows.Add(new SystemStatRowViewModel { Label = "GPU Clock", Value = Fmt(gpu.ClockMhz, "0", " MHz"), AccentBrush = GpuAccentBrush });
            rows.Add(new SystemStatRowViewModel { Label = "GPU Power", Value = Fmt(gpu.PowerW, "0.0", " W"), AccentBrush = GpuAccentBrush });
            rows.Add(new SystemStatRowViewModel
            {
                Label = "VRAM",
                Value = FormatVram(gpu),
                AccentBrush = GpuAccentBrush
            });
        }

        if (snapshot.Memory is { } mem)
        {
            rows.Add(new SystemStatRowViewModel
            {
                Label = "RAM",
                Value = $"{Fmt(mem.UsedGB, "0.0")}/{Fmt(mem.TotalGB, "0.0")} GB",
                AccentBrush = RamAccentBrush
            });
        }

        foreach (var fan in snapshot.Fans)
        {
            rows.Add(new SystemStatRowViewModel { Label = fan.Name, Value = $"{fan.Rpm:0} RPM", AccentBrush = FanAccentBrush });
        }

        SystemStatRows.Clear();
        foreach (var row in rows)
            SystemStatRows.Add(row);
    }

    // Ticked by _sessionSampleTimer every 5s while a run is active; appends a sensor read to the
    // current session's samples.jsonl. Swallows read failures; skip this tick, don't take down the run.
    private void AppendSessionSnapshot()
    {
        if (_currentSession is null) return;
        try
        {
            var snapshot = _sensorService.GetSnapshot();

            // Sensor Fallback, Phase B: when GetSnapshot() went through the HWiNFO fallback decorator
            // (WinTester.Sensors.HwInfo.HwInfoBackedSensorService) and it had to fill in a null LHM field
            // this poll, LastResolutionNotes says which fields and which raw HWiNFO label won — carried
            // into samples.jsonl alongside the snapshot it describes (Section 6, point 3). Null on every
            // ordinary tick, and on any ISensorService that isn't wrapped in the decorator at all.
            var fallbackNotes = _sensorService is HwInfoBackedSensorService withHwInfoFallback
                ? withHwInfoFallback.LastResolutionNotes
                : null;

            _currentSession.AppendSnapshot(snapshot, fallbackNotes);
        }
        catch
        {
            // skip this tick; telemetry logging must never abort the run
        }

        try
        {
            if (_hwInfoSampler?.Capture(DateTime.Now) is { } hwInfoSample)
                _currentSession.AppendHwInfoSample(hwInfoSample);
        }
        catch
        {
            // HWiNFO data is optional; a failed poll just leaves a gap for this tick
        }
    }

    public string SelectAllButtonLabel =>
        Tab1Modules.Any(m => m.IsAvailable) && Tab1Modules.Where(m => m.IsAvailable).All(m => m.IsSelected) ? "Deselect All" : "Select All";

    private double _overallProgressPercent;
    public double OverallProgressPercent
    {
        get => _overallProgressPercent;
        private set { _overallProgressPercent = value; OnPropertyChanged(); }
    }

    private string _currentTestName = "";
    public string CurrentTestName
    {
        get => _currentTestName;
        private set { _currentTestName = value; OnPropertyChanged(); }
    }

    // Progress (0-100) of just the currently-running module, distinct from OverallProgressPercent
    // (tests-completed / tests-in-queue). Drives the running-state modal's "CURRENT TEST" bar.
    private double _currentTestProgressPercent;
    public double CurrentTestProgressPercent
    {
        get => _currentTestProgressPercent;
        private set { _currentTestProgressPercent = value; OnPropertyChanged(); }
    }

    // What the current module says it's doing right now, sourced from ITestModule.ProgressChanged's
    // StatusText. Shown inside the current-test bar.
    private string _currentStageText = "";
    public string CurrentStageText
    {
        get => _currentStageText;
        private set { _currentStageText = value; OnPropertyChanged(); }
    }

    // Formatted mm:ss countdown for the current module, or "" when it reported no SecondsRemaining; the UI
    // hides the countdown chip entirely rather than showing a stale/zero timer.
    private string _countdownText = "";
    public string CountdownText
    {
        get => _countdownText;
        private set { _countdownText = value; OnPropertyChanged(); }
    }

    private static string FormatCountdown(double? secondsRemaining)
    {
        if (secondsRemaining is not { } seconds || seconds < 0) return "";
        var span = TimeSpan.FromSeconds(seconds);
        return span.Hours > 0
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private void ToggleSelectAll()
    {
        // S147: a tile whose module is missing is never selected.
        var available = Tab1Modules.Where(m => m.IsAvailable).ToList();
        bool selectAll = !(available.Count > 0 && available.All(m => m.IsSelected));
        foreach (var vm in available)
            vm.IsSelected = selectAll;
    }

    // RunSelectedAsync loops this for a multi-run session. RunQueueAsync doesn't show the pass/fail
    // popup itself; it returns a RunOutcome, and callers decide when to show it.
    private sealed record RunOutcome(
        bool WasManualCancel,
        bool OverallPassed,
        bool StoppedEarlyDueToFailure,
        List<ResultPopupWindow.TestOutcomeRow> Rows,
        string? ReportPath);

    // A manual Stop is treated as an explicit abort, not a reported fail; never shown as a
    // pass/fail popup. Shared by every RunQueueAsync caller so this rule stays in one place.
    private void ShowResultPopupIfNeeded(RunOutcome? outcome)
    {
        if (outcome is null || outcome.WasManualCancel) return;
        ResultPopupWindow.Show(
            _ownerWindow,
            overallPassed: outcome.OverallPassed,
            stoppedEarly: outcome.StoppedEarlyDueToFailure,
            rows: outcome.Rows,
            reportPath: outcome.ReportPath);
    }

    private async Task RunSelectedAsync()
    {
        var selected = Tab1Modules.Where(m => m.IsSelected).ToList();
        if (selected.Count == 0) return;

        // Repeats the same queue, in order. Defaults (NumberOfRuns=1, RunInfinitely=false) reduce
        // this to a single RunQueueAsync call.
        var isMultiRun = RunInfinitely || NumberOfRuns > 1;
        var totalRuns = RunInfinitely ? int.MaxValue : NumberOfRuns;
        var suppressReport = DisableReports;

        // Asked once before the loop starts. The same folder is reused for every run; each run's
        // filename disambiguates itself (Report-<session id>_RUN01.html, ..._RUN02.html, ...). Cancelling
        // aborts the run, matching Battery Charge/Drain's convention.
        string? reportOutputDir = null;
        if (!suppressReport)
        {
            reportOutputDir = PromptForReportOutputDir();
            if (reportOutputDir is null) return; // cancelled; don't start the run
        }

        RunOutcome? lastOutcome = null;
        for (var runNumber = 1; runNumber <= totalRuns; runNumber++)
        {
            lastOutcome = await RunQueueAsync(
                selected,
                runNumber: isMultiRun ? runNumber : (int?)null,
                suppressReport: suppressReport,
                reportOutputDir: reportOutputDir);

            // A cancelled run stops the whole multi-run loop: RunQueueAsync exits its own queue
            // early on cancellation; this stops the next RunQueueAsync call from starting a fresh run.
            if (lastOutcome is null || lastOutcome.WasManualCancel) break;
        }

        ShowResultPopupIfNeeded(lastOutcome);
    }

    private async Task RunStandaloneAsync(TestModuleViewModel? vm)
    {
        if (vm is null || IsRunning) return;

        var confirmed = ConfirmDialog.Show(
            _ownerWindow,
            $"Do you want to run {vm.DisplayName} standalone?");

        if (!confirmed) return;

        var suppressReport = DisableReports;
        string? reportOutputDir = null;
        if (!suppressReport)
        {
            reportOutputDir = PromptForReportOutputDir();
            if (reportOutputDir is null) return; // cancelled; don't start the run
        }

        var outcome = await RunQueueAsync(new[] { vm }, suppressReport: suppressReport, reportOutputDir: reportOutputDir);
        ShowResultPopupIfNeeded(outcome);
    }

    // Shared by RunSelectedAsync/RunStandaloneAsync. Folder picker rather than SaveFileDialog since
    // a multi-run/infinite loop writes multiple Report-<session id>_RUNnn.html files. Returns null on cancel;
    // callers abort the run before it starts, matching Charge/Drain's SaveFileDialog-cancel convention.
    private string? PromptForReportOutputDir()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to save the test report(s)"
        };
        return dialog.ShowDialog(_ownerWindow) == true ? dialog.FolderName : null;
    }

    // Gear-icon handler; blocked while a run is in progress, though the fullscreen overlay already
    // eats the click before it gets here. TestOptionsWindow.Show is a simple modal, so this stays synchronous.
    // Guards the ISensorService->IFanControlService cast in one place; becomes unreachable once a real
    // DI seam for the generic backend exists but costs nothing to keep as a guard until then, since a bad
    // cast here would otherwise surface as an unhandled exception on tile click. The selection itself is
    // delegated to FanBackendSelector rather than opening the generic backend directly.
    //
    // Vendor backend entries:
    //   AsusDesktop — read-only RPM; no known write path for this WMI class.
    //   Gigabyte    — full read + write, tiered (GB_WMIACPI).
    //   AsusLaptop  — full read + write, proportional PWM via bundled AsusWinIO64.dll. Needs elevation
    //                 and the ASUS System Control Interface present; degrades to generic when absent.
    //   Dell        — full read + write, tiered (3 firmware levels) via the bundled
    //                 bzh_dell_smm_io_x64.sys kernel driver. Needs elevation, and needs driver signature
    //                 enforcement to permit an unsigned third-party driver; degrades to generic when
    //                 either is missing, rather than trying to change the machine's security posture.
    //
    // Lenovo still falls through to the generic backend. FanControlWindow surfaces the detection result
    // (BackendDescription) either way, so it's clear why nothing beyond RPM is controllable instead of
    // it looking unexplained.
    //
    // Factories, not instances: a backend is only constructed if its vendor is actually detected, so a
    // non-Dell machine never attempts to install a kernel driver and a non-ASUS machine never loads
    // AsusWinIO64.dll.
    private static readonly Dictionary<FanVendor, Func<IFanControlService>> FanVendorBackendFactories = new()
    {
        [FanVendor.AsusDesktop] = () => new AsusDesktopFanBackend(),
        [FanVendor.Gigabyte] = () => new GigabyteLaptopFanBackend(),
        [FanVendor.AsusLaptop] = () => new AsusLaptopFanBackend(),
        [FanVendor.Dell] = () => new DellLaptopFanBackend(),
    };

    private void OpenFanControlWindow()
    {
        if (_sensorService is not IFanControlService genericFanBackend) return;

        var selection = FanBackendSelector.Select(genericFanBackend, FanVendorBackendFactories);
        FanControlWindow.Show(_ownerWindow, selection);
    }

    private void OpenAdvancedSettings(TestModuleViewModel? vm)
    {
        if (vm is null || IsRunning || !vm.SupportsAdvancedSettings) return;

        TestOptionsWindow.Show(_ownerWindow, vm);
    }

    private async Task<RunOutcome?> RunQueueAsync(
        System.Collections.Generic.IReadOnlyList<TestModuleViewModel> queue,
        int? runNumber = null,
        bool suppressReport = false,
        string? reportOutputDir = null)
    {
        if (queue.Count == 0 || IsRunning) return null;

        IsRunning = true;
        OverallProgressPercent = 0;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        // Restarts HWiNFO fresh for this run and waits up to HwInfoReadyTimeout for its shared memory to
        // become readable. hwInfoDetected reflects exe presence only, independent of whether the wait
        // below succeeds, and is what the report's status line shows. A timeout or missing exe is not an
        // error: the run proceeds normally with no HWiNFO data.
        bool hwInfoDetected = _hwInfoProcess.IsExePresent();
        bool hwInfoReady = false;
        if (hwInfoDetected)
        {
            _hwInfoLaunchedByRun = true;
            CurrentStageText = "Starting sensors";
            hwInfoReady = await _hwInfoProcess.RestartAndWaitForReadyAsync(HwInfoReadyTimeout, ct);
            CurrentStageText = "";

            // Cancelled while waiting: no session exists yet, so tear down and skip session/report creation.
            if (ct.IsCancellationRequested)
            {
                ShutdownHwInfo();
                IsRunning = false;
                _runCts = null;
                return new RunOutcome(true, false, false, new List<ResultPopupWindow.TestOutcomeRow>(), null);
            }
        }

        // One SessionDataStore per run; every module in the queue shares the same session
        // folder/SessionId. Assigned to _currentSession so the 5s timer callback can reach it.
        var session = SessionDataStore.CreateNew(
            SessionsRootDir,
            _brand.CompanyName,
            _brand.AppName,
            queue.Select(m => m.Id).ToList(),
            abortOnFail: AbortOnFail,
            hwInfoDetected: hwInfoDetected);
        _currentSession = session;
        _hwInfoSampler = hwInfoReady ? new HwInfoEventSampler() : null;
        AppendSessionSnapshot(); // capture a baseline reading now, don't wait for the first 5s tick
        _sessionSampleTimer.Start();

        bool allPassed = true;
        int completed = 0;
        bool stoppedEarlyDueToFailure = false;
        var resultRows = new List<ResultPopupWindow.TestOutcomeRow>();
        foreach (var vm in queue)
        {
            if (ct.IsCancellationRequested) break;

            CurrentTestName = vm.DisplayName;
            _currentModuleId = vm.Id;
            CurrentTestId = vm.Id;
            CurrentTestProgressPercent = 0;
            CurrentStageText = vm.DisplayName;
            CountdownText = "";
            RefreshLiveDisplays(); // update immediately on test transition, don't wait for the next tick
            vm.Status = TestStatus.Running;

            session.AppendEvent(new SessionEvent
            {
                Type = SessionEventType.TestStarted,
                TestId = vm.Id,
                Message = $"{vm.DisplayName} started."
            });

            var context = new TestRunContext
            {
                SessionId = session.Metadata.SessionId,
                BrandProfile = _brand,
                AbortOnFail = AbortOnFail,
                Ct = ct,
                SessionDirectory = session.SessionDirectory
            };

            TestResult? result = null;
            void OnCompleted(object? s, TestResultEventArgs e) => result = e.Result;
            void OnProgress(object? s, TestProgressEventArgs e)
            {
                session.AppendEvent(new SessionEvent
                {
                    Type = SessionEventType.TestProgress,
                    TestId = vm.Id,
                    ProgressPercent = e.ProgressPercent,
                    Message = e.StatusText
                });

                // Drives the modal's "current test" bar; separate from OverallProgressPercent, which only advances per completed module.
                CurrentTestProgressPercent = e.ProgressPercent;
                CurrentStageText = e.StatusText;
                CountdownText = FormatCountdown(e.SecondsRemaining);
            }
            void OnModuleSample(object? s, SensorSampleEventArgs e) => session.AppendModuleSample(vm.Id, e.Sample);

            vm.Module.Completed += OnCompleted;
            vm.Module.ProgressChanged += OnProgress;
            vm.Module.SensorSampleCaptured += OnModuleSample;

            try
            {
                await vm.Module.StartAsync(context, ct);
            }
            catch (OperationCanceledException)
            {
                vm.Status = TestStatus.Idle;
                vm.Module.Completed -= OnCompleted;
                vm.Module.ProgressChanged -= OnProgress;
                vm.Module.SensorSampleCaptured -= OnModuleSample;
                session.AppendEvent(new SessionEvent
                {
                    Type = SessionEventType.SessionAborted,
                    TestId = vm.Id,
                    Message = $"{vm.DisplayName} cancelled."
                });
                break;
            }

            vm.Module.Completed -= OnCompleted;
            vm.Module.ProgressChanged -= OnProgress;
            vm.Module.SensorSampleCaptured -= OnModuleSample;

            bool passed = result?.Passed ?? false;
            vm.Status = passed ? TestStatus.Pass : TestStatus.Fail;
            allPassed &= passed;

            resultRows.Add(new ResultPopupWindow.TestOutcomeRow
            {
                DisplayName = vm.DisplayName,
                Passed = passed,
                FailureReason = result?.FailureReason
            });

            // Folds any raw log file the module points at into this session's raw/ folder so the whole run
            // stays self-contained under one Sessions/<timestamp>/ directory.
            if (result?.RawLogPath is { } rawPath && File.Exists(rawPath))
            {
                try { session.WriteRawLog(vm.Id, File.ReadAllText(rawPath)); }
                catch { /* best-effort; a missing/locked raw log shouldn't fail the run */ }
            }

            session.AppendEvent(new SessionEvent
            {
                Type = SessionEventType.TestCompleted,
                TestId = vm.Id,
                Passed = passed,
                FailureReason = result?.FailureReason,
                Message = passed ? $"{vm.DisplayName} passed." : $"{vm.DisplayName} failed.",
                KeyMetrics = result?.KeyMetrics?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "")
            });

            completed++;
            OverallProgressPercent = completed * 100.0 / queue.Count;

            // On first failure with AbortOnFail set, stop the queue. The current module has already
            // finished by this point, so "stop" means not starting the next one.
            if (!passed && AbortOnFail)
            {
                stoppedEarlyDueToFailure = true;
                session.AppendEvent(new SessionEvent
                {
                    Type = SessionEventType.SessionAborted,
                    TestId = vm.Id,
                    Message = $"Queue stopped after {vm.DisplayName} failed (Abort on first failure)."
                });
                break;
            }
        }

        // Read before _runCts is cleared below. Distinguishes a manual Stop (not shown as a fail popup)
        // from a normal completion or an abort-on-fail early stop (both of which do show the popup).
        bool wasManualCancel = ct.IsCancellationRequested;

        _sessionSampleTimer.Stop();
        _hwInfoSampler?.Dispose();
        _hwInfoSampler = null;

        // WHEA and the PCIe link capability read are part of the HWiNFO feature set, so they stay off when
        // HWiNFO isn't bundled. Both readers never throw and are started together since each is a
        // separate process launch.
        if (hwInfoDetected)
        {
            var runStart = session.Metadata.StartTime;
            var wheaTask = Task.Run(() => WheaEventLogReader.ReadSince(runStart));
            var pcieTask = Task.Run(() => PcieLinkInfoReader.Read());

            try
            {
                foreach (var whea in await wheaTask)
                    session.AppendWheaEvent(whea);
            }
            catch
            {
                // WHEA listing is optional; the run's result never depends on it
            }

            try
            {
                foreach (var link in await pcieTask)
                    session.AppendPcieDevice(link);
            }
            catch
            {
                // PCIe link capability is optional; the run's result never depends on it
            }
        }

        bool completedFullQueue = completed == queue.Count;
        session.FinalizeSession(overallPassed: allPassed && completedFullQueue);
        var sessionDir = session.SessionDirectory;
        session.Dispose();
        _currentSession = null;

        // Stopped unconditionally, including when hwInfoDetected was false (a harmless no-op then), so a
        // multi-pass suite always starts its next pass from a clean state and HWiNFO never lingers as a
        // background process once the run ends.
        _hwInfoProcess.Shutdown();
        _hwInfoLaunchedByRun = false;

        // Generated as soon as the session is written, unless suppressReport (Disable Reports
        // checkbox) is set, in which case reportPath stays null and session data is still written.
        // Best-effort: a report failure shouldn't take down a completed run. reportPath stays null
        // on skip or failure, which is when ResultPopupWindow's View Report button should hide.
        string? reportPath = null;
        if (!suppressReport)
        {
            try
            {
                var record = WinTester.Core.Session.SessionReader.Load(sessionDir);

                // Captures activation status fresh and embeds the genuine/non-genuine badge as a
                // base64 data URI so the report stays a single self-contained HTML file. Best-effort.
                WinTester.Core.Activation.ActivationSnapshot? activation = null;
                string? activationImageDataUri = null;
                try
                {
                    activation = await ActivationInfoService.GetSnapshotAsync();
                    activationImageDataUri = BuildActivationImageDataUri(activation.IsPermanentlyActivated);
                }
                catch
                {
                    // report omits the activation banner if this fails
                }

                // Same System Identity pull Battery Drain/Charge use (Win32_ComputerSystem
                // Manufacturer/Model, Win32_Bios SerialNumber), feeding the report's System Identity
                // subsection. Best-effort.
                WinTester.Tests.Power.SystemIdentityResult? sysIdentity = null;
                try
                {
                    sysIdentity = WinTester.Tests.Power.SystemIdentityReader.Read();
                }
                catch
                {
                    // report simply omits the System Identity subsection if this fails
                }

                // Fixed voltage-axis range for the Battery Percent/Voltage charts. Best-effort;
                // DesignVoltage is current machine state, not something the test run measured.
                WinTester.Tests.Power.BatteryVoltageRangeResult? voltageRange = null;
                try
                {
                    voltageRange = WinTester.Tests.Power.BatteryVoltageRangeReader.Read();
                }
                catch
                {
                    // report simply falls back to auto-scaling those two charts if this fails
                }

                var reportModel = WinTester.Reporting.ReportModel.Build(
                    record, _brand.ReportFooterText, activation, activationImageDataUri,
                    sysIdentity?.Manufacturer, sysIdentity?.Model, sysIdentity?.SerialNumber, voltageRange,
                    _brand.BusinessDetails);

                // The session id is always in the filename, so a report copied out of its session folder still
                // says which session it came from; the run number is added on top for a multi-run loop
                // (runNumber non-null).
                var fileName = WinTester.Reporting.HtmlReportGenerator.ReportFileName(reportModel.SessionId, runNumber);

                // Only the report moves to the chosen folder, session.json/samples.jsonl/events.jsonl/
                // raw logs stay under Sessions\ as SessionReader's source of truth. `?? sessionDir` is
                // a fallback for a caller that skips the folder prompt.
                var reportDir = reportOutputDir ?? sessionDir;
                reportPath = WinTester.Reporting.HtmlReportGenerator.Generate(reportModel, reportDir, fileName);
            }
            catch
            {
                // reportPath stays null; ResultPopupWindow reads that as "hide the View Report button."
                // Session data itself is intact regardless.
            }
        }

        CurrentTestName = "";
        _currentModuleId = null;
        CurrentTestId = null;
        IsRunning = false;
        _runCts = null;

        // Popup display is the caller's decision (RunSelectedAsync/RunStandaloneAsync via
        // ShowResultPopupIfNeeded); this method only reports back what happened.
        return new RunOutcome(wasManualCancel, allPassed && completedFullQueue, stoppedEarlyDueToFailure, resultRows, reportPath);
    }

    // Reads genuine.png or non-genuine.png from vendor/images/ and returns a data:image/png;base64,...
    // URI for embedding in the self-contained HTML report. Returns null if the file isn't present.
    private static string? BuildActivationImageDataUri(bool isPermanentlyActivated)
    {
        var fileName = isPermanentlyActivated ? "genuine.png" : "non-genuine.png";
        var path = Path.Combine(AppContext.BaseDirectory, "vendor", "images", fileName);
        if (!File.Exists(path)) return null;

        var bytes = File.ReadAllBytes(path);
        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }

    private async Task RequestCancelAsync()
    {
        if (!IsRunning) return;

        // _runOverlayWindow is a separate Topmost window above _ownerWindow; owning this dialog by
        // _ownerWindow would let the overlay cover it, so it's owned by the overlay window instead.
        var confirmed = ConfirmDialog.Show(
            (Window?)_runOverlayWindow ?? _ownerWindow,
            "Are you sure you want to cancel the running test suite?");

        if (confirmed)
            _runCts?.Cancel();

        await Task.CompletedTask;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
