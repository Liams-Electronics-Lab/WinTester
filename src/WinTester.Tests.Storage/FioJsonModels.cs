using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WinTester.Tests.Storage;

// Minimal subset of fio's --output-format=json schema; the final result object and each --status-interval snapshot share the same shape (jobs[] with read/write sub-objects), so one model serves both. Unneeded fields are omitted [System.Text.Json ignores unmapped properties, keeping this forward-compatible. Not verified against a real fio binary (no fio/Windows host in this dev environment); shape follows fio's documented JSON format. No longer the live-speed source] status-interval snapshots don't reliably reach stdout, so live speed now polls the fio process's own OS-level I/O counters (see FioRunner); this model still parses fio's one guaranteed final result per phase.
internal sealed class FioJsonResult
{
    [JsonPropertyName("jobs")]
    public List<FioJob>? Jobs { get; set; }
}

internal sealed class FioJob
{
    [JsonPropertyName("jobname")]
    public string? JobName { get; set; }

    // Non-zero means fio itself recorded an I/O error on this job; treated as an immediate storage-test failure.
    [JsonPropertyName("error")]
    public int Error { get; set; }

    [JsonPropertyName("read")]
    public FioIoStats? Read { get; set; }

    [JsonPropertyName("write")]
    public FioIoStats? Write { get; set; }
}

internal sealed class FioIoStats
{
    // Bandwidth in bytes/sec, converted to MB/s by FioRunner. fio's KiB/s bw field is deliberately unused; bw_bytes avoids assuming what "bw" is scaled to.
    [JsonPropertyName("bw_bytes")]
    public long BwBytes { get; set; }

    [JsonPropertyName("iops")]
    public double Iops { get; set; }

    [JsonPropertyName("runtime")]
    public long RuntimeMs { get; set; }
}
