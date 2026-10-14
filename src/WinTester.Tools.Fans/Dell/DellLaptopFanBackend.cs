using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using WinTester.Core.Fans;
using WinTester.Tools.Fans.Dell;

namespace WinTester.Tools.Fans;

// Dell laptop (and some tower) fan backend, via Dell's SMBIOS/SMM fan interface reached through the
// bzh_dell_smm_io_x64.sys kernel driver.
//
// Like Gigabyte and unlike ASUS-laptop, this is a tier backend: Dell's firmware exposes three discrete
// fan levels (Off / Medium / High), not a PWM duty. The sliders still appear and quantize onto the
// three levels, but the tier buttons are the honest control here.
//
// Important asymmetry versus every other backend: manual control requires first telling the EC to stop
// managing the fans at all (DisableEcFanControl). A machine left in that state with the tool gone has NO
// automatic thermal management — not a fan stuck at a fixed speed, but a fan nobody is managing. That
// makes the reset-on-close contract more load-bearing here than anywhere else, and it's why ResetAll
// re-enables EC control even when no fan level was ever set.
[SupportedOSPlatform("windows")]
public sealed class DellLaptopFanBackend : IFanControlService, IFanBackendProbe, IFanTierControl, IDisposable
{
    private const string IdPrefix = "DellSmm:";

    internal const string TierAuto = "dell.auto";
    internal const string TierOff = "dell.off";
    internal const string TierMedium = "dell.medium";
    internal const string TierHigh = "dell.high";

    // Dell's interface has no "how many fans" query, so probe each index until one stops answering.
    // Seven is upstream's own ceiling (BzhFanIndex.Fan1..Fan7).
    private const int MaxFanIndices = 7;

    private readonly DellSmmDriver _driver = new();
    private readonly List<uint> _fanIndices = new();
    private readonly Dictionary<uint, DellFanLevel> _commandedLevel = new();

    private bool _ecControlDisabled;
    private bool _usedAlternateEcControl;
    private bool _disposed;

    public bool TryActivate()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            if (!_driver.Initialize()) return false;

            // Confirm we're actually talking to a Dell SMI implementation, not just relying on the
            // manufacturer string.
            if (_driver.ExecuteCommand(DellSmbiosCommand.CheckSmbiosInterface) is null)
            {
                _driver.Dispose();
                return false;
            }

            DiscoverFans();

            if (_fanIndices.Count == 0)
            {
                _driver.Dispose();
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Read-only discovery: GetFanRpm on each index, stopping at the first that doesn't answer. A fan
    // that reports 0 RPM still counts (it may simply be parked at idle) — only a null, meaning the SMI
    // call itself was refused, ends the scan.
    private void DiscoverFans()
    {
        _fanIndices.Clear();

        for (uint i = 0; i < MaxFanIndices; i++)
        {
            if (_driver.ExecuteCommand(DellSmbiosCommand.GetFanRpm, i) is null) break;
            _fanIndices.Add(i);
        }
    }

    public IReadOnlyList<FanControlInfo> GetFans()
    {
        if (_disposed || !_driver.IsInitialized) return Array.Empty<FanControlInfo>();

        try
        {
            var result = new List<FanControlInfo>(_fanIndices.Count);

            foreach (uint index in _fanIndices)
            {
                uint? rpm = _driver.ExecuteCommand(DellSmbiosCommand.GetFanRpm, index);

                result.Add(new FanControlInfo(
                    Id: IdPrefix + index,
                    Name: _fanIndices.Count == 1 ? "Fan" : $"Fan {index + 1}",
                    Rpm: rpm,
                    DutyPercent: _commandedLevel.TryGetValue(index, out var level) ? LevelToPercent(level) : null,
                    IsControllable: true));
            }

            return result;
        }
        catch
        {
            return Array.Empty<FanControlInfo>();
        }
    }

    // Slider <-> level mapping. The thresholds are chosen so each level owns a roughly equal slice of
    // slider travel and 0% lands on Off; there's no firmware meaning to the exact numbers, since the
    // hardware only ever sees one of three values.
    private static DellFanLevel PercentToLevel(double percent) => percent switch
    {
        < 33 => DellFanLevel.Off,
        < 67 => DellFanLevel.Medium,
        _ => DellFanLevel.High
    };

    // Displayed back on the slider after a set. Midpoints rather than the boundaries, so the thumb sits
    // in the middle of the band it's actually in instead of on the edge of the next one.
    private static double LevelToPercent(DellFanLevel level) => level switch
    {
        DellFanLevel.Off => 0,
        DellFanLevel.Medium => 50,
        _ => 100
    };

    public void SetDuty(string id, double percent)
    {
        if (_disposed || !_driver.IsInitialized) return;
        if (!TryParseIndex(id, out uint index)) return;

        ApplyLevel(index, PercentToLevel(Math.Clamp(percent, 0, 100)));
    }

    private void ApplyLevel(uint index, DellFanLevel level)
    {
        try
        {
            // EC control has to be off before any manual level is honoured. Done once and tracked so
            // dragging a slider doesn't re-issue the SMI call every tick.
            //
            // Dell's SMI exposes two independent disable/enable pairs — CTL1 (0x30A3/0x31A3) and CTL2
            // (0x34A3/0x35A3) — for models where one family doesn't respond. Whichever family succeeds
            // is tracked so the matching enable call is used later.
            if (!_ecControlDisabled)
            {
                if (_driver.ExecuteCommand(DellSmbiosCommand.DisableEcFanControl) is not null)
                {
                    _usedAlternateEcControl = false;
                }
                else if (_driver.ExecuteCommand(DellSmbiosCommand.DisableEcFanControlAlternate) is not null)
                {
                    _usedAlternateEcControl = true;
                }
                else
                {
                    // Neither family responded: don't set a level, since the EC would keep overriding it.
                    return;
                }

                _ecControlDisabled = true;
            }

            uint parameter = ((uint)level << 8) | index;
            if (_driver.ExecuteCommand(DellSmbiosCommand.SetFanLevel, parameter) is null) return;

            _commandedLevel[index] = level;
        }
        catch
        {
        }
    }

    public void Reset(string id)
    {
        if (_disposed) return;
        if (!TryParseIndex(id, out _)) return;

        // EC control is machine-wide, not per-fan — handing one fan back means handing them all back.
        ResetAll();
    }

    // Tries both enable opcode families unconditionally, rather than only the one tracked as used: if
    // that tracking is ever wrong (a crash mid-sequence, or a previous run that never recorded which
    // family it used), sending only one opcode could leave EC control off. A harmless SMI call to the
    // unused family costs nothing.
    public void ResetAll()
    {
        if (_disposed || !_driver.IsInitialized) return;

        try
        {
            _driver.ExecuteCommand(DellSmbiosCommand.EnableEcFanControl);
            _driver.ExecuteCommand(DellSmbiosCommand.EnableEcFanControlAlternate);

            _ecControlDisabled = false;
            _usedAlternateEcControl = false;
            _commandedLevel.Clear();
        }
        catch
        {
        }
    }

    // ---- IFanTierControl -------------------------------------------------------------------------

    public IReadOnlyList<FanTier> GetTiers() => new[]
    {
        new FanTier(TierAuto, "Auto",
            "Returns the fans to Dell's embedded-controller thermal management. This is the state the tool restores when the window closes."),

        new FanTier(TierOff, "Off",
            "Stops the fans. EC thermal management is disabled while this is active — do not leave a machine here under load."),

        new FanTier(TierMedium, "Medium",
            "Dell's mid fan level. EC thermal management stays disabled while a manual level is set."),

        new FanTier(TierHigh, "High",
            "Dell's maximum fan level — the usual choice for confirming a fan spins up properly under test.")
    };

    public string? ActiveTierId
    {
        get
        {
            if (_disposed || !_ecControlDisabled) return TierAuto;
            if (_commandedLevel.Count == 0) return TierAuto;

            // All fans are normally at the same level; if they've diverged, report the highest.
            DellFanLevel highest = DellFanLevel.Off;
            foreach (var level in _commandedLevel.Values)
                if (level > highest) highest = level;

            return highest switch
            {
                DellFanLevel.Off => TierOff,
                DellFanLevel.Medium => TierMedium,
                _ => TierHigh
            };
        }
    }

    public void ApplyTier(string tierId)
    {
        if (_disposed || !_driver.IsInitialized) return;

        if (tierId == TierAuto)
        {
            ResetAll();
            return;
        }

        DellFanLevel? level = tierId switch
        {
            TierOff => DellFanLevel.Off,
            TierMedium => DellFanLevel.Medium,
            TierHigh => DellFanLevel.High,
            _ => null
        };

        if (level is null) return;

        // Tier buttons act on every fan, unlike the per-row sliders.
        foreach (uint index in _fanIndices)
            ApplyLevel(index, level.Value);
    }

    private static bool TryParseIndex(string id, out uint index)
    {
        index = 0;
        return id.StartsWith(IdPrefix, StringComparison.Ordinal) &&
               uint.TryParse(id.AsSpan(IdPrefix.Length), out index);
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Restore EC control before the driver goes away, or the machine is left with fan management
        // disabled and no interface left to re-enable it.
        try { ResetAll(); } catch { }

        _disposed = true;
        _driver.Dispose();
    }
}
