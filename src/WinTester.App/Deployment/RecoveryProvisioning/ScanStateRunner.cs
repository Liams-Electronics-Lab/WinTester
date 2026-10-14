using System.Diagnostics;
using System.IO;

namespace WinTester.App.Deployment.RecoveryProvisioning;

public sealed record ScanStateRunResult(int ExitCode, bool Cancelled, TimeSpan Elapsed, IReadOnlyList<string> OutputLines);

// Starts scanstate.exe, streams its output, follows the /progress file and can kill the whole process tree
// (task 3.1). Callbacks come from background threads; the caller marshals to the UI (IProgress<T> does that).
public static class ScanStateRunner
{
    private const int MaxKeptOutputLines = 5000;
    private const int PollMs = 700;
    private const int QuietNoticeMs = 20000;

    public static async Task<ScanStateRunResult> RunAsync(ScanStateCommand cmd, Action<string> onLine,
        Action<int> onPercent, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cmd.ExePath)
        {
            WorkingDirectory = cmd.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in cmd.Arguments) psi.ArgumentList.Add(a);

        var lines = new List<string>();
        var gate = new object();
        var lastOutputTick = new long[] { Environment.TickCount64 };

        void Handle(string? text)
        {
            // ScanState writes UTF-16 to a redirected stdout; read as ANSI it arrives as "S\0C\0A\0..." (C22).
            if (text is not null)
            {
                if (text.IndexOf('\0') >= 0) text = text.Replace("\0", "");
                if (text.StartsWith("\u00FF\u00FE", StringComparison.Ordinal)) text = text.Substring(2); // UTF-16 BOM read as ANSI
            }
            if (string.IsNullOrWhiteSpace(text)) return;
            Interlocked.Exchange(ref lastOutputTick[0], Environment.TickCount64);
            lock (gate)
            {
                if (lines.Count < MaxKeptOutputLines) lines.Add(text);
            }
            onLine(text);
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data is null ? null : "[stderr] " + e.Data);

        var watch = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException("scanstate.exe could not be started.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var cancelled = false;
        using var registration = ct.Register(() =>
        {
            cancelled = true;
            try { if (!process.HasExited) process.Kill(true); } catch { /* already gone */ }
        });

        using var helperCts = new CancellationTokenSource();
        var tail = Task.Run(() => FollowProgressAsync(cmd.ProgressFile, onPercent, helperCts.Token));
        var quiet = Task.Run(() => QuietNoticesAsync(lastOutputTick, watch, onLine, helperCts.Token));

        // Waits for the process and for the redirected streams to drain.
        await Task.Run(() => process.WaitForExit());

        helperCts.Cancel();
        try { await Task.WhenAll(tail, quiet); } catch (OperationCanceledException) { }
        watch.Stop();

        // One last read so a final percentage written just before exit is not missed.
        try
        {
            if (File.Exists(cmd.ProgressFile) && ProgressParser.Latest(ProgressParser.ReadLinesShared(cmd.ProgressFile)) is int p)
                onPercent(p);
        }
        catch { }

        List<string> kept;
        lock (gate) kept = new List<string>(lines);
        return new ScanStateRunResult(process.ExitCode, cancelled, watch.Elapsed, kept);
    }

    private static async Task FollowProgressAsync(string file, Action<int> onPercent, CancellationToken token)
    {
        var reported = -1;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(file))
                {
                    var latest = ProgressParser.Latest(ProgressParser.ReadLinesShared(file));
                    if (latest is int p && p != reported)
                    {
                        reported = p;
                        onPercent(p);
                    }
                }
            }
            catch { /* file briefly locked or half-written: try again next poll */ }

            try { await Task.Delay(PollMs, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ScanState can be silent for minutes; say so now and then so the wizard does not look frozen.
    private static async Task QuietNoticesAsync(long[] lastOutputTick, Stopwatch watch, Action<string> onLine,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(QuietNoticeMs, token); }
            catch (OperationCanceledException) { return; }

            if (Environment.TickCount64 - Interlocked.Read(ref lastOutputTick[0]) >= QuietNoticeMs - 1000)
                onLine($"Still running... {(int)watch.Elapsed.TotalMinutes}:{watch.Elapsed.Seconds:00} elapsed");
        }
    }
}
