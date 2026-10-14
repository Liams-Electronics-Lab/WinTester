using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using WinTester.Core.Fans;
using WinTester.Tools.Fans.Asus;

namespace WinTester.Tools.Fans;

// ASUS laptop fan backend (ROG / TUF / Zenbook / Vivobook) via AsusWinIO64.dll, the user-mode shim that
// ships with the ASUS System Control Interface.
//
// Unlike Gigabyte's EC, this interface is genuinely proportional: duty is a 0-255 PWM value the fan
// actually tracks, so this backend exposes sliders only and does not implement IFanTierControl.
//
// Two hard requirements, both handled by TryActivate rather than assumed:
//  * AsusWinIO64.dll must be resolvable. It's bundled alongside the app, but a DllNotFoundException or
//    EntryPointNotFoundException (older vendor DLL missing an export) is still possible, so every entry
//    into native code here is guarded.
//  * The process must be elevated, and on most models the ASUS System Analysis/Control Interface service
//    must be installed. When absent, the DLL typically loads but reports zero fans rather than failing
//    loudly — the probe below treats "zero fans" as failure and lets FanBackendSelector fall through to
//    the generic LHM path.
//
// The DLL is stateful and not thread-safe: SetFanIndex selects the fan that the next FanRPM or
// SetFanPwmDuty call applies to, so every read/write pair must be atomic with respect to other callers,
// hence _sync.
[SupportedOSPlatform("windows")]
public sealed class AsusLaptopFanBackend : IFanControlService, IFanBackendProbe, IDisposable
{
    private const string IdPrefix = "AsusWinIO:";

    private readonly object _sync = new();

    // Fan indices this backend has put into manual (test) mode. ResetAll walks exactly this set, so a
    // fan never touched is never written to.
    private readonly HashSet<byte> _manualFans = new();

    private int _fanCount;
    private bool _initialized;
    private bool _disposed;

    // Last duty this backend commanded, per fan index, as a percentage. The DLL has no read-back for
    // commanded duty, so this is the only source for the slider's displayed value.
    private readonly Dictionary<byte, double> _commandedDuty = new();

    public bool TryActivate()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            lock (_sync)
            {
                AsusWinIO64.InitializeWinIo();
                _initialized = true;

                _fanCount = AsusWinIO64.HealthyTable_FanCounts();

                // Zero fans is the normal shape of "DLL loaded but the ASUS service isn't present, not
                // elevated, or model unsupported." Shut the DLL back down before discarding this backend.
                if (_fanCount <= 0)
                {
                    ShutdownQuietly();
                    return false;
                }

                return true;
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // An older AsusWinIO64.dll without one of the HealthyTable_* exports.
            return false;
        }
        catch
        {
            ShutdownQuietly();
            return false;
        }
    }

    public IReadOnlyList<FanControlInfo> GetFans()
    {
        if (!_initialized || _disposed || _fanCount <= 0) return Array.Empty<FanControlInfo>();

        try
        {
            lock (_sync)
            {
                var result = new List<FanControlInfo>(_fanCount);

                for (byte i = 0; i < _fanCount; i++)
                {
                    // Index-then-read; nothing else allowed between the two calls.
                    AsusWinIO64.HealthyTable_SetFanIndex(i);
                    int rpm = AsusWinIO64.HealthyTable_FanRPM();

                    result.Add(new FanControlInfo(
                        Id: IdPrefix + i,
                        Name: _fanCount == 1 ? "Fan" : $"Fan {i + 1}",
                        Rpm: rpm >= 0 ? rpm : null,
                        DutyPercent: _commandedDuty.TryGetValue(i, out var duty) ? duty : null,
                        IsControllable: true));
                }

                return result;
            }
        }
        catch
        {
            return Array.Empty<FanControlInfo>();
        }
    }

    public void SetDuty(string id, double percent)
    {
        if (!_initialized || _disposed) return;
        if (!TryParseIndex(id, out byte index)) return;

        double clamped = Math.Clamp(percent, 0, 100);

        try
        {
            lock (_sync)
            {
                AsusWinIO64.HealthyTable_SetFanIndex(index);

                // The vendor wrapper treats duty 0 as "leave test mode," not "spin down to nothing" — a
                // 0% slider hands the fan back to the EC rather than stopping it outright.
                if (clamped <= 0)
                {
                    AsusWinIO64.HealthyTable_SetFanTestMode((char)0x00);
                    AsusWinIO64.HealthyTable_SetFanPwmDuty(0);
                    _manualFans.Remove(index);
                    _commandedDuty[index] = 0;
                    return;
                }

                AsusWinIO64.HealthyTable_SetFanTestMode((char)0x01);
                AsusWinIO64.HealthyTable_SetFanPwmDuty(ToPwm(clamped));

                _manualFans.Add(index);
                _commandedDuty[index] = clamped;
            }
        }
        catch
        {
            // A failed write leaves the fan wherever the EC last had it, the safe direction to fail in.
        }
    }

    // The DLL takes 0-255, not a percentage. Rounding keeps 100% landing on a true 255 instead of 254.
    private static short ToPwm(double percent) =>
        (short)Math.Clamp(Math.Round(percent / 100.0 * 255.0), 0, 255);

    public void Reset(string id)
    {
        if (!_initialized || _disposed) return;
        if (!TryParseIndex(id, out byte index)) return;

        try
        {
            lock (_sync)
            {
                ResetFanLocked(index);
            }
        }
        catch
        {
        }
    }

    // Close-path guarantee. Walks only the fans actually put into manual mode and clears the set as it
    // goes, so a repeat call (manual button, then OnClosed, then ProcessExit) is a genuine no-op.
    public void ResetAll()
    {
        if (!_initialized || _disposed) return;

        try
        {
            lock (_sync)
            {
                foreach (byte index in new List<byte>(_manualFans))
                {
                    // One fan failing to reset must not abandon the rest.
                    try { ResetFanLocked(index); } catch { }
                }

                _manualFans.Clear();
            }
        }
        catch
        {
        }
    }

    private void ResetFanLocked(byte index)
    {
        AsusWinIO64.HealthyTable_SetFanIndex(index);
        AsusWinIO64.HealthyTable_SetFanTestMode((char)0x00);
        _manualFans.Remove(index);
        _commandedDuty.Remove(index);
    }

    private static bool TryParseIndex(string id, out byte index)
    {
        index = 0;
        return id.StartsWith(IdPrefix, StringComparison.Ordinal) &&
               byte.TryParse(id.AsSpan(IdPrefix.Length), out index);
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Reset before shutting the DLL down; doing it after would leave a manual-mode fan stuck with
        // no interface left to undo it.
        try { ResetAll(); } catch { }

        _disposed = true;
        ShutdownQuietly();
    }

    private void ShutdownQuietly()
    {
        if (!_initialized) return;

        try { AsusWinIO64.ShutdownWinIo(); } catch { }
        _initialized = false;
    }
}
