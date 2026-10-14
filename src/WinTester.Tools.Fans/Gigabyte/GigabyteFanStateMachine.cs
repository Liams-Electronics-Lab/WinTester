using System;

namespace WinTester.Tools.Fans.Gigabyte;

// The ACPI hardware state machine for Gigabyte laptop ECs.
//
// The key finding behind why this backend is tier-shaped rather than slider-shaped: live testing on a
// GAMING A16 CVH (BIOS FB05) established that this EC ignores every proportional-speed register.
// SetFixedFanSpeed, the SetFanIndexValue table, and SetFanSpeed all either read back correctly or error,
// but none of them changes fan behaviour. The firmware only implements discrete modes, so a commanded
// duty is quantized onto a four-tier ladder:
//
//   duty <  10   -> WhisperEco  (Auto=1 + Whisper=1,       ~1600 RPM)
//   10..79       -> StepCurve   (Auto=0, Fixed=0, Step=1,  ~52% duty curve)
//   80..97       -> FixedBoost  (Auto=0, Fixed=1, Step=1,  ~87% duty)
//   >= 98        -> MaxTurbo    (FixedBoost + TPP + Turbo, 100%, ~7500 RPM)
//
// The speed registers are still written on the boost tiers so this degrades gracefully on other
// Gigabyte/AORUS models whose EC may honour them.
//
// Two things about the call sequences are load-bearing and must not be "tidied": manual fixed control
// requires BOTH SetFixedFanStatus=1 and SetStepFanStatus=1 (with step cleared, the EC silently ignores
// SetFixedFanSpeed), and the order of writes in each sequence mirrors Gigabyte Control Center's own call
// order. Neither is reasoned from a spec — both come from hardware testing.
//
// SetGPUFanDuty routes every commanded percentage through GigabyteDutyTable.ToRegisterSpeed, same as
// SetFixedFanSpeed (CPU): writing an unconverted percentage straight into a register whose real range is
// the table's 57-229 bytes lands at roughly the 40-45% step regardless of what was requested, which
// matched a real report of the GPU fan running far quieter than commanded during MaxTurbo. GCC's own
// ComData.dll defines exactly one duty table (DutyValue/DutyPercent), keyed generically across
// Fan1Duty..Fan4Duty rather than a CPU-specific field, consistent with the same raw-register requirement
// applying to the GPU channel. The literal 0 writes in EnterAutoMode/WriteWhisperSequence are
// deliberately not run through the table — those are an off/reset sentinel outside the table's 25-100%
// range; only a genuinely commanded duty (FixedBoost, MaxTurbo) goes through ToRegisterSpeed.
internal sealed class GigabyteFanStateMachine
{
    // Hysteresis bands stop the mode thrashing while a slider drag walks the commanded duty across a
    // tier boundary (e.g. 100 -> 98 -> 95 -> 92 -> 100).
    private const byte TurboEnterAt = 98;
    private const byte TurboExitBelow = 93;
    private const byte BoostEnterAt = 80;
    private const byte BoostExitBelow = 75;
    private const byte WhisperEnterBelow = 10;
    private const byte WhisperExitAt = 15;

    // Overdrive kick-cycling cadence — see OverdriveTick.
    private const int OverdriveDropMs = 750;
    private const int OverdriveBoostMs = 2500;

    private readonly GigabyteWmiDriver _driver;
    private readonly Func<DateTime> _clock;

    private bool _autoApplied;
    private bool _overdriveBoostPhase = true;
    private DateTime _overdrivePhaseStart = DateTime.MinValue;

    public GigabyteFanMode CurrentMode { get; private set; } = GigabyteFanMode.Auto;
    public byte AppliedCpuDuty { get; private set; } = GigabyteFanTarget.Unset;
    public byte AppliedGpuDuty { get; private set; } = GigabyteFanTarget.Unset;

    // Set when a tier button is pressed rather than a slider moved — tiers are explicit controls, not
    // merely a consequence of slider position. While this is non-null the duty-driven ladder above is
    // bypassed entirely; moving any slider clears it (GigabyteLaptopFanBackend.SetDuty), handing control
    // back to the ladder.
    public GigabyteFanMode? ForcedMode { get; set; }

    // Gates MaxTurbo's Overdrive kick-cycle (see OverdriveTick). Set once by
    // GigabyteLaptopFanBackend.TryActivate from the same board check that gates RPM display trust — see
    // that class's ConfirmedBoardModelSubstring. Defaults false: an unconfirmed board gets a flat,
    // steady MaxTurbo (single write, periodic re-assert like every other tier) rather than a cycle only
    // ever measured on one specific board's EC.
    public bool IsConfirmedBoard { get; set; }

    public GigabyteFanStateMachine(GigabyteWmiDriver driver, Func<DateTime>? clock = null)
    {
        _driver = driver;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    // Idempotent: the worker calls this every tick while no target is set, so it must not re-issue the
    // full write sequence each time.
    public void ResetToAuto()
    {
        ForcedMode = null;
        if (CurrentMode == GigabyteFanMode.Auto && _autoApplied) return;
        EnterAutoMode();
    }

    public void ProcessTarget(GigabyteFanTarget target)
    {
        if (ForcedMode is null &&
            (target.IsResetRequested ||
             (target.CpuDuty == GigabyteFanTarget.Unset && target.GpuDuty == GigabyteFanTarget.Unset)))
        {
            ResetToAuto();
            return;
        }

        byte effectiveCpu = target.EffectiveCpuDuty;
        byte effectiveGpu = target.EffectiveGpuDuty;

        GigabyteFanMode desired = ForcedMode ?? SelectTier(Math.Max(effectiveCpu, effectiveGpu));

        if (desired != CurrentMode)
        {
            switch (desired)
            {
                case GigabyteFanMode.MaxTurbo:   EnterMaxTurbo();   break;
                case GigabyteFanMode.FixedBoost: EnterFixedBoost(); break;
                case GigabyteFanMode.StepCurve:  EnterStepCurve();  break;
                case GigabyteFanMode.WhisperEco: EnterWhisperEco(); break;
                case GigabyteFanMode.Auto:       EnterAutoMode();   break;
            }
        }
        else if (CurrentMode == GigabyteFanMode.MaxTurbo && IsConfirmedBoard)
        {
            OverdriveTick();
        }

        if (CurrentMode == GigabyteFanMode.FixedBoost &&
            (effectiveCpu != AppliedCpuDuty || effectiveGpu != AppliedGpuDuty))
        {
            ApplyBoostSpeeds(effectiveCpu, effectiveGpu);
        }
        else if (CurrentMode == GigabyteFanMode.StepCurve)
        {
            AppliedCpuDuty = effectiveCpu;
            AppliedGpuDuty = effectiveGpu;
        }
    }

    // Quantizes a duty percentage onto the discrete ladder, with hysteresis relative to the mode the
    // hardware is already in at every boundary.
    private GigabyteFanMode SelectTier(byte duty)
    {
        byte turboThreshold = CurrentMode == GigabyteFanMode.MaxTurbo ? TurboExitBelow : TurboEnterAt;
        if (duty >= turboThreshold) return GigabyteFanMode.MaxTurbo;

        byte whisperThreshold = CurrentMode == GigabyteFanMode.WhisperEco ? WhisperExitAt : WhisperEnterBelow;
        if (duty < whisperThreshold) return GigabyteFanMode.WhisperEco;

        byte boostThreshold = CurrentMode is GigabyteFanMode.FixedBoost or GigabyteFanMode.MaxTurbo
            ? BoostExitBelow
            : BoostEnterAt;

        return duty >= boostThreshold ? GigabyteFanMode.FixedBoost : GigabyteFanMode.StepCurve;
    }

    // Periodically re-asserts the current non-auto mode's full sequence. The EC loses its status bits on
    // hibernate-resume; Gigabyte's own Control Center re-applies the fan mode on resume for the same
    // reason.
    public void Heartbeat()
    {
        switch (CurrentMode)
        {
            case GigabyteFanMode.MaxTurbo:
                if (IsConfirmedBoard)
                {
                    // Overdrive already re-writes its mode bits every <=3s from OverdriveTick;
                    // re-asserting the full sequence here would cut a droop phase short.
                }
                else
                {
                    // No droop cycle on this board — re-assert steady, same as every other tier.
                    WriteTurboSequence();
                }
                break;

            case GigabyteFanMode.FixedBoost:
                WriteFixedBoostSequence();
                if (AppliedCpuDuty != GigabyteFanTarget.Unset && AppliedGpuDuty != GigabyteFanTarget.Unset)
                {
                    _driver.SetMethod("SetFixedFanSpeed", GigabyteDutyTable.ToRegisterSpeed(AppliedCpuDuty));
                    _driver.SetMethod("SetGPUFanDuty", GigabyteDutyTable.ToRegisterSpeed(AppliedGpuDuty));
                }
                break;

            case GigabyteFanMode.StepCurve:
                WriteStepCurveSequence();
                break;

            case GigabyteFanMode.WhisperEco:
                WriteWhisperSequence();
                break;
        }
    }

    private void EnterAutoMode()
    {
        _driver.SetMethod("SetTurboMode", 0);
        _driver.SetMethod("SetTppStatus", 0);
        _driver.SetMethod("SetWhisperMode", 0);
        _driver.SetMethod("SetFixedFanStatus", 0);
        _driver.SetMethod("SetStepFanStatus", 0);
        _driver.SetMethod("SetAutoFanStatus", 1);
        _driver.SetMethod("SetFixedFanSpeed", 0);
        _driver.SetMethod("SetGPUFanDuty", 0);

        CurrentMode = GigabyteFanMode.Auto;
        _autoApplied = true;
        AppliedCpuDuty = GigabyteFanTarget.Unset;
        AppliedGpuDuty = GigabyteFanTarget.Unset;
    }

    private void EnterWhisperEco()
    {
        WriteWhisperSequence();
        CurrentMode = GigabyteFanMode.WhisperEco;
        _autoApplied = false;
        AppliedCpuDuty = 0;
        AppliedGpuDuty = 0;
    }

    private void WriteWhisperSequence()
    {
        _driver.SetMethod("SetTurboMode", 0);
        _driver.SetMethod("SetTppStatus", 0);
        _driver.SetMethod("SetFixedFanStatus", 0);
        _driver.SetMethod("SetStepFanStatus", 0);
        _driver.SetMethod("SetAutoFanStatus", 1);
        _driver.SetMethod("SetFixedFanSpeed", 0);
        _driver.SetMethod("SetGPUFanDuty", 0);
        _driver.SetMethod("SetWhisperMode", 1);
    }

    // GCC's applyFANprofile custom branch: the EC's own moderate step curve (~52% duty at 76C on the
    // confirmed board, measured below stock auto at the same temperature).
    private void EnterStepCurve()
    {
        WriteStepCurveSequence();
        CurrentMode = GigabyteFanMode.StepCurve;
        _autoApplied = false;
    }

    private void WriteStepCurveSequence()
    {
        _driver.SetMethod("SetTurboMode", 0);
        _driver.SetMethod("SetTppStatus", 0);
        _driver.SetMethod("SetWhisperMode", 0);
        _driver.SetMethod("SetFixedFanStatus", 0);
        _driver.SetMethod("SetStepFanStatus", 1);
        _driver.SetMethod("SetAutoFanStatus", 0);
    }

    // Fixed+Step engaged together: the EC runs its aggressive ~87% profile regardless of the speed
    // registers (still written, for other-model compatibility).
    private void EnterFixedBoost()
    {
        WriteFixedBoostSequence();
        CurrentMode = GigabyteFanMode.FixedBoost;
        _autoApplied = false;
        AppliedCpuDuty = GigabyteFanTarget.Unset;
        AppliedGpuDuty = GigabyteFanTarget.Unset;
    }

    private void WriteFixedBoostSequence()
    {
        _driver.SetMethod("SetTurboMode", 0);
        _driver.SetMethod("SetTppStatus", 0);
        _driver.SetMethod("SetWhisperMode", 0);
        _driver.SetMethod("SetFixedFanStatus", 1);
        _driver.SetMethod("SetStepFanStatus", 1);
        _driver.SetMethod("SetAutoFanStatus", 0);
    }

    private void ApplyBoostSpeeds(byte cpuDuty, byte gpuDuty)
    {
        _driver.SetMethod("SetFixedFanSpeed", GigabyteDutyTable.ToRegisterSpeed(cpuDuty));
        _driver.SetMethod("SetGPUFanDuty", GigabyteDutyTable.ToRegisterSpeed(gpuDuty));
        AppliedCpuDuty = cpuDuty;
        AppliedGpuDuty = gpuDuty;
    }

    // Mirrors GCC's ucNotebook FanControl.SetFanMax call order, plus SetTurboMode.
    private void EnterMaxTurbo()
    {
        WriteTurboSequence();
        CurrentMode = GigabyteFanMode.MaxTurbo;
        _autoApplied = false;
        AppliedCpuDuty = 100;
        AppliedGpuDuty = 100;

        if (IsConfirmedBoard)
        {
            _overdriveBoostPhase = true;
            _overdrivePhaseStart = _clock();
        }
    }

    private void WriteTurboSequence()
    {
        _driver.SetMethod("SetAutoFanStatus", 0);
        _driver.SetMethod("SetWhisperMode", 0);
        _driver.SetMethod("SetFixedFanSpeed", 229);
        _driver.SetMethod("SetGPUFanDuty", 229);
        _driver.SetMethod("SetStepFanStatus", 1);
        _driver.SetMethod("SetFixedFanStatus", 1);
        _driver.SetMethod("SetTppStatus", 1);
        _driver.SetMethod("SetTurboMode", 1);
    }

    // Overdrive: rides the EC's own spin-up kick to beat the firmware's 84% clamp — measured only on
    // GIGABYTE GAMING A16 CVH (see IsConfirmedBoard). The droop phase drops ADJF (SetFixedFanStatus=0)
    // so the EC falls back to the step curve for ~0.75s, and the boost phase re-asserts ADJF, which
    // re-fires the EC's 100%-duty kick (~7500 RPM) for ~2.5s. Net on the confirmed board: ~7.2k RPM
    // average, 6521 RPM floor.
    //
    // Only called when IsConfirmedBoard is true — see ProcessTarget and EnterMaxTurbo. An unconfirmed
    // AORUS board (GIGABYTE AORUS 15 XE5) reported MaxTurbo running quieter than a flat 100% under this
    // cycle, the opposite of the intended effect, so the cycle is gated to the one confirmed board.
    private void OverdriveTick()
    {
        DateTime now = _clock();
        double inPhaseMs = (now - _overdrivePhaseStart).TotalMilliseconds;

        if (_overdriveBoostPhase && inPhaseMs >= OverdriveBoostMs)
        {
            _driver.SetMethod("SetFixedFanStatus", 0);
            _overdriveBoostPhase = false;
            _overdrivePhaseStart = now;
        }
        else if (!_overdriveBoostPhase && inPhaseMs >= OverdriveDropMs)
        {
            _driver.SetMethod("SetStepFanStatus", 1);
            _driver.SetMethod("SetAutoFanStatus", 0);
            _driver.SetMethod("SetFixedFanStatus", 1);
            _overdriveBoostPhase = true;
            _overdrivePhaseStart = now;
        }
    }
}

// Desired CPU/GPU duty pair. Unset (255) means "no manual target for this fan" and is distinguishable
// from 0% (a real, deliberate request for the lowest tier).
internal readonly struct GigabyteFanTarget
{
    public const byte Unset = 255;

    public byte CpuDuty { get; }
    public byte GpuDuty { get; }
    public bool IsResetRequested { get; }

    public GigabyteFanTarget(byte cpuDuty, byte gpuDuty, bool isResetRequested = false)
    {
        CpuDuty = cpuDuty;
        GpuDuty = gpuDuty;
        IsResetRequested = isResetRequested;
    }

    // The EC has one shared mode for both fans, so a target set on only one fan still has to produce a
    // duty for the other — it borrows the set one rather than defaulting to 0, which would otherwise
    // drag the shared mode down to Whisper the moment a single slider was touched.
    public byte EffectiveCpuDuty => CpuDuty != Unset ? CpuDuty : (GpuDuty != Unset ? GpuDuty : (byte)0);
    public byte EffectiveGpuDuty => GpuDuty != Unset ? GpuDuty : (CpuDuty != Unset ? CpuDuty : (byte)0);
}
