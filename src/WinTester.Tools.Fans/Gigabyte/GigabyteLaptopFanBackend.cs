using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using WinTester.Core.Fans;
using WinTester.Tools.Fans.Gigabyte;

namespace WinTester.Tools.Fans;

// Gigabyte laptop fan backend (GB_WMIACPI_Set/Get, root\WMI).
// Method names, "Data", 16-step duty table, tier thresholds, and write order are load-bearing;
// see GigabyteFanStateMachine.
//
// This is a real write path. FanControlWindow close-path ResetAll() has thermal stakes: it stops/joins
// the worker, then writes auto last.
//
// Exposes tiers (IFanTierControl) and sliders. The confirmed board's EC honors only tiers, not
// proportional speed; sliders remain wired for ECs that honor speed registers.
//
// Verified only on GIGABYTE GAMING A16 CVH, BIOS FB05. Other Gigabyte/AORUS models are unverified:
// the WMI class may exist while the EC layout differs. The probe confirms only that the interface exists.
//
// On AORUS 15 XE5 (BIOS FB09, ITE IT5571), duty control works but RPM readback is physically impossible
// and self-drifting. No tool can derive this EC's real RPM. RPM display is gated to the confirmed model;
// duty control is not gated.
[SupportedOSPlatform("windows")]
public sealed class GigabyteLaptopFanBackend : IFanControlService, IFanBackendProbe, IFanTierControl, IDisposable
{
// Only board with confirmed tier-specific behavior (not just RPM).
// Matched by substring against Win32_ComputerSystem.Model: "GIGABYTE GAMING A16 CVH"
// (OEM model strings may include revision/SKU suffixes).
//
// Gates RPM display and MaxTurbo's Overdrive kick-cycle
// (GigabyteFanStateMachine.IsConfirmedBoard). 
// GigabyteRpmByteSwap's family conversion is unconfirmed. If hardware confirms either half, add a
// family check (mirroring GigabyteRpmByteSwap.NoSwapFamilies); do not assume AORUS inherits GAMING behavior.
    private const string ConfirmedBoardModelSubstring = "A16 CVH";

    // The EC exposes exactly two tachometers and one shared mode. These Ids are this backend's stable
    // FanControlInfo.Id values; they're never shown to the user.
    private const string CpuFanId = "GbAcpiFan:Cpu";
    private const string GpuFanId = "GbAcpiFan:Gpu";

    internal const string TierAuto = "gb.auto";
    internal const string TierWhisper = "gb.whisper";
    internal const string TierStep = "gb.step";
    internal const string TierBoost = "gb.boost";
    internal const string TierTurbo = "gb.turbo";

    private readonly GigabyteWmiDriver _driver = new();
    private readonly GigabyteFanStateMachine _stateMachine;
    private readonly GigabyteFanWorker _worker;

    private bool _activated;
    private bool _disposed;
    private bool _isConfirmedBoard;

    public GigabyteLaptopFanBackend()
    {
        _stateMachine = new GigabyteFanStateMachine(_driver);
        _worker = new GigabyteFanWorker(_driver, _stateMachine);
    }

    // The manufacturer string alone doesn't prove GB_WMIACPI is present, so this confirms it directly
    // (equivalent to Get-WmiObject -Namespace root\WMI -Class GB_WMIACPI_Set).
    //
    // Deliberately confirms a read before declaring the backend usable: connecting proves the classes
    // exist, but a board that exposes the classes without a working tachometer would otherwise pass the
    // probe and then present two permanently-blank fans as though that were normal. Nothing here writes.
    public bool TryActivate()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            if (!_driver.EnsureConnected()) return false;

            // A board with the interface present answers this; a board without it returns 0 from the
            // driver's own failure path. Either fan reporting a tachometer is enough.
            uint cpuRpm = _driver.GetMethod("getRpm1");
            uint gpuRpm = _driver.GetMethod("getRpm2");
            if (cpuRpm == 0 && gpuRpm == 0) return false;

            // The getRpm1/getRpm2 numbers themselves, and MaxTurbo's Overdrive kick-cycle, are only
            // known-good on the one board they were verified against. Everywhere else, trust that the
            // interface answers (needed for the probe above and for duty control) without trusting
            // board-specific behaviors layered on top of it.
            _isConfirmedBoard = IsConfirmedBoard();
            _stateMachine.IsConfirmedBoard = _isConfirmedBoard;

            _worker.Start();
            _activated = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Best-effort model check against Win32_ComputerSystem (root\cimv2 — unrelated to the root\WMI/
    // GB_WMIACPI connection above). Any failure here — WMI unavailable, query throws, string comes back
    // empty meaning "not the confirmed board": an unknown board doesn't get the benefit of the doubt on
    // a behavior demonstrated wrong on other hardware.
    private static bool IsConfirmedBoard()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Model FROM Win32_ComputerSystem");

            foreach (System.Management.ManagementBaseObject result in searcher.Get())
            {
                if (result["Model"] is string model &&
                    model.Contains(ConfirmedBoardModelSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to "not verified."
        }

        return false;
    }

    public IReadOnlyList<FanControlInfo> GetFans()
    {
        if (!_activated || _disposed) return Array.Empty<FanControlInfo>();

        GigabyteTelemetry telemetry = _worker.Telemetry;
        GigabyteFanMode mode = _worker.CurrentMode;

        return new[]
        {
            new FanControlInfo(
                Id: CpuFanId,
                Name: "CPU Fan",
                Rpm: (_isConfirmedBoard && telemetry.HasRead) ? telemetry.CpuRpm : null,
                DutyPercent: DutyForDisplay(_stateMachine.AppliedCpuDuty, mode),
                IsControllable: true),

            new FanControlInfo(
                Id: GpuFanId,
                Name: "GPU Fan",
                Rpm: (_isConfirmedBoard && telemetry.HasRead) ? telemetry.GpuRpm : null,
                DutyPercent: DutyForDisplay(_stateMachine.AppliedGpuDuty, mode),
                IsControllable: true)
        };
    }

    // The EC has no "read back the current duty" method, so the reported duty is what this backend last
    // commanded, not what the hardware measured. In Auto (nothing commanded) that's null rather than 0 
    // showing 0% for a firmware-controlled fan spinning at 2000 RPM would be actively misleading.
    private static double? DutyForDisplay(byte appliedDuty, GigabyteFanMode mode)
    {
        if (mode == GigabyteFanMode.Auto) return null;
        return appliedDuty == GigabyteFanTarget.Unset ? null : appliedDuty;
    }

    public void SetDuty(string id, double percent)
    {
        if (!_activated || _disposed) return;

        bool isGpu = id == GpuFanId;
        if (!isGpu && id != CpuFanId) return;

        // Moving a slider hands control back to the duty-driven tier ladder. Without this, a previously
        // selected tier would pin the hardware and the slider would appear inert.
        _stateMachine.ForcedMode = null;
        _worker.SetTargetDuty(Math.Clamp(percent, 0, 100), isGpu);
    }

    // The EC's mode is shared across both fans, so there is no meaningful per-fan reset .Resetting
    // either one returns the whole EC to firmware control. Documented here rather than silently
    // treating Reset(cpu) as a no-op, since a caller could reasonably expect otherwise.
    public void Reset(string id)
    {
        if (!_activated || _disposed) return;
        if (id != CpuFanId && id != GpuFanId) return;

        ResetAll();
    }

    // The close-path guarantee. Order matters: stop and join the worker first so no queued manual write
    // can land after the reset, then issue the auto sequence synchronously on the calling thread. Safe
    // to call repeatedly and safe to call when nothing was ever set.ResetToAuto is idempotent.
    public void ResetAll()
    {
        if (_disposed) return;

        try
        {
            _worker.RequestResetToAuto();
            _worker.Stop();
            _stateMachine.ResetToAuto();
        }
        catch
        {
            // Best-effort: throwing here would propagate into FanControlWindow.OnClosed.
        }
        finally
        {
            // The window may still be open (the manual "Reset All to Auto" button also lands here), so
            // bring the worker back up for continued polling. Start() is a no-op if it's already running.
            if (_activated && !_disposed) _worker.Start();
        }
    }

    // ---- IFanTierControl -------------------------------------------------------------------------
    //
    // Tiers appear as their own buttons, alongside the sliders, only on backends that have them.
    // StepCurve is marked implicit, the hardware genuinely lands there from mid-range slider positions,
    // so the UI needs to be able to show it, but offering it as a button next to Auto would present two
    // firmware-curve options with no meaningful difference.

    public IReadOnlyList<FanTier> GetTiers() => new[]
    {
        new FanTier(TierAuto, "Auto",
            "Automatic fan control (firmware default)."),

        new FanTier(TierWhisper, "Whisper",
            "Quiet mode, not recommended under load."),

        new FanTier(TierStep, "Step Curve",
            "Moderate.",
            IsImplicit: true),

        new FanTier(TierBoost, "Boost",
            "Aggressive."),

        new FanTier(TierTurbo, "Turbo",
            _isConfirmedBoard
                ? "Maximum cooling."
                : "Maximum cooling.")
    };

    public string? ActiveTierId => _activated
        ? _worker.CurrentMode switch
        {
            GigabyteFanMode.Auto => TierAuto,
            GigabyteFanMode.WhisperEco => TierWhisper,
            GigabyteFanMode.StepCurve => TierStep,
            GigabyteFanMode.FixedBoost => TierBoost,
            GigabyteFanMode.MaxTurbo => TierTurbo,
            _ => null
        }
        : null;

    public void ApplyTier(string tierId)
    {
        if (!_activated || _disposed) return;

        if (tierId == TierAuto)
        {
            ResetAll();
            return;
        }

        GigabyteFanMode? mode = tierId switch
        {
            TierWhisper => GigabyteFanMode.WhisperEco,
            TierStep => GigabyteFanMode.StepCurve,
            TierBoost => GigabyteFanMode.FixedBoost,
            TierTurbo => GigabyteFanMode.MaxTurbo,
            _ => null
        };

        if (mode is null) return;

        _worker.Start(); // no-op if already running; covers a tier pressed straight after a reset.
        _stateMachine.ForcedMode = mode;

        // Nudge the worker so the tier applies on its next tick rather than waiting out the poll interval.
        _worker.SetTargetDuty(mode switch
        {
            GigabyteFanMode.WhisperEco => 0,
            GigabyteFanMode.StepCurve => 50,
            GigabyteFanMode.FixedBoost => 85,
            _ => 100
        }, isGpu: false);
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Reset before tearing down, never after, or the EC is left pinned in a manual mode with no
        // connection left to undo it.
        try { ResetAll(); } catch { }

        _disposed = true;
        _worker.Stop();
        _driver.Dispose();
    }
}
