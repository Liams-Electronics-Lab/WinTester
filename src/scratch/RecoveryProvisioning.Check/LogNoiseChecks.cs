using System.IO;
using WinTester.App.Deployment.RecoveryProvisioning;

// S127: the pnpmig error lines every healthy capture has contained are known noise (C25). They are counted apart, listed in the
// verifier's details, and no longer turn an exit-0 run amber. Called from Program.cs.
public static class LogNoiseChecks
{
    public static void Run(Action<bool, string> check, string tempRoot)
    {
        var log = Path.Combine(tempRoot, "noise.log");
        File.WriteAllLines(log, new[]
        {
            "2026-10-01 12:04:50, Error                 [0x0808fe] Plugin {ff9c714f-0000-0000-0000-000000000000} (pnpmig.dll): Unable to open policy key to migrate values. Error = 0x00000005",
            "2026-10-01 12:04:51, Error                 [0x0808fe] pnpmig.dll AllowDenyLayered failed 0x32",
            "2026-10-01 12:04:52, Warning               [0x0808b0] something mild"
        });
        var an = ScanStateLogAnalyzer.Analyze(log);
        check(an.ErrorCount == 0 && an.KnownNoiseCount == 2 && an.WarningCount == 1 && an.FirstErrors.Count == 0
              && an.KnownNoiseLines is { Count: 2 }, "noise: pnpmig error lines are counted apart, not as errors");

        // S146: the Setup\Upgrade delete failure is known noise too, and only that exact key.
        var upg = Path.Combine(tempRoot, "noise-upgrade.log");
        File.WriteAllLines(upg, new[]
        {
            @"2026-10-07 20:28:50, Error                 [0x000000] Failed to delete reg tree HKLM\SYSTEM\Setup\Upgrade[gle=0x00000005]",
            "2026-10-07 20:28:40, Error                 [0x0808fe] Plugin {ff9c714f-b864-4f43-ae39-ec07d7385abe}: Policy value 'AllowDenyLayered' blocks policy migration. Error = 0x00000032"
        });
        var anU = ScanStateLogAnalyzer.Analyze(upg);
        check(anU.ErrorCount == 0 && anU.KnownNoiseCount == 2, "noise: the Setup\\Upgrade delete failure is counted apart");
        check(CaptureVerifier.Verify(0, false, 1_000_000, anU).Kind == CaptureOutcomeKind.Success, "noise: exit 0 with only the known lines is a clean success");
        check(!ScanStateLogAnalyzer.IsKnownNoise(@"Failed to delete reg tree HKLM\SYSTEM\Setup\Other[gle=0x5]"), "noise: another failed reg tree delete still counts");

        File.AppendAllLines(log, new[] { "2026-10-01 12:05:00, Error                 [0x000001] something else broke" });
        var an2 = ScanStateLogAnalyzer.Analyze(log);
        check(an2.ErrorCount == 1 && an2.KnownNoiseCount == 2 && an2.FirstErrors.Count == 1, "noise: any other error line still counts");

        check(ScanStateLogAnalyzer.IsKnownNoise("x pnpmig.dll y") && ScanStateLogAnalyzer.IsKnownNoise("{FF9C714F-1}")
              && !ScanStateLogAnalyzer.IsKnownNoise("Plugin x failed") && !ScanStateLogAnalyzer.IsKnownNoise(""), "noise: matcher");

        var v1 = CaptureVerifier.Verify(0, false, 1_000_000, an);
        check(v1.Kind == CaptureOutcomeKind.Success && v1.Details.Any(d => d.StartsWith("Not counted: 2")), "noise: exit 0 with only pnpmig errors is a clean success and says what was not counted");
        var v2 = CaptureVerifier.Verify(0, false, 1_000_000, an2);
        check(v2.Kind == CaptureOutcomeKind.SuccessWithWarnings, "noise: a real error next to the noise still gives warnings");
        var v3 = CaptureVerifier.Verify(72, false, 1000, an);
        check(v3.Kind == CaptureOutcomeKind.Failed, "noise: a failing run is still a failure");
    }
}
