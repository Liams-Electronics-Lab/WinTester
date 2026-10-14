using System.Globalization;

namespace WinTester.Tools.DiskInfo;

// Counters Windows keeps in MSFT_StorageReliabilityCounter. Used when the drive's own S.M.A.R.T. data cannot be read,
// and to say so honestly: every field is optional because Windows fills only what the driver provides.
public sealed record WindowsReliability(
    int? TemperatureCelsius,
    int? WearPercent,
    ulong? PowerOnHours,
    ulong? ReadErrorsUncorrected,
    ulong? WriteErrorsUncorrected,
    ulong? LoadUnloadCycles);

// Everything the evaluator needs for one disk. Nothing here is read from the machine by the evaluator itself.
public sealed record DiskReadings(
    DiskMedia Media,
    string Model,
    AtaSmartData? Ata,
    NvmeHealth? Nvme,
    WindowsReliability? Windows,
    int? WindowsHealthStatus,
    ulong? FreeBytes,
    ulong? TotalBytes,
    bool? TrimEnabled);

public sealed record EvaluationResult(SmartSource Source, HealthSummary Health, IReadOnlyList<SmartRow> Rows);

// Turns raw readings into table rows, abnormal flags and the overall health. Pure: no I/O, so every rule below is
// covered by logic checks. The limits are this tool's own, chosen to be cautious rather than alarming, and are quoted
// in SmartExplanations: change both together.
public static class SmartEvaluator
{
    public const int HddCautionCelsius = 50;
    public const int HddBadCelsius = 60;
    public const int SsdCautionCelsius = 70;
    public const int SsdBadCelsius = 80;
    public const int LifeCautionPercent = 20;
    public const int LifeBadPercent = 5;
    public const int PercentageUsedFlag = 80;
    public const int LowFreeSpacePercent = 10;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static EvaluationResult Evaluate(DiskReadings r)
    {
        var ssdLike = r.Media is DiskMedia.Ssd or DiskMedia.Nvme;
        var source = r.Ata is { Attributes.Count: > 0 } ? SmartSource.Ata
            : r.Nvme is not null ? SmartSource.Nvme
            : r.Windows is not null ? SmartSource.WindowsCounters
            : SmartSource.None;

        var head = new List<SmartRow>();   // temperature, hours, cycles
        var body = new List<SmartRow>();   // counters
        var level = HealthLevel.Good;
        var reasons = new List<string>();
        int? life = null;
        bool? smartOk = null;

        void Raise(HealthLevel to, string reason)
        {
            if (to > level) level = to;
            if (!reasons.Contains(reason)) reasons.Add(reason);
        }

        switch (source)
        {
            case SmartSource.Ata:
                smartOk = EvaluateAta(r, ssdLike, head, body, Raise, out life);
                break;
            case SmartSource.Nvme:
                EvaluateNvme(r.Nvme!, ssdLike, head, body, Raise, out life);
                break;
            case SmartSource.WindowsCounters:
                EvaluateWindows(r, ssdLike, head, body, Raise, out life);
                break;
        }

        var middle = new List<SmartRow>();
        AddFreeSpace(r, middle);
        if (ssdLike && r.TrimEnabled is { } trim)
            middle.Add(new SmartRow(SmartRowKeys.Trim, Title(SmartRowKeys.Trim), trim ? "Enabled" : "Disabled", !trim, "Windows setting"));

        switch (r.WindowsHealthStatus)
        {
            case 2: Raise(HealthLevel.Bad, "Windows reports this disk as Unhealthy."); break;
            case 1: Raise(HealthLevel.Caution, "Windows reports this disk as Warning."); break;
        }

        HealthSummary health;
        if (source == SmartSource.None && level == HealthLevel.Good)
        {
            health = new HealthSummary(HealthLevel.Unknown, "Unknown", null,
                new[] { "No S.M.A.R.T. data could be read for this disk." });
        }
        else
        {
            health = new HealthSummary(level, level.ToString(), ssdLike ? life : null, reasons);
        }

        var rows = new List<SmartRow>
        {
            new(SmartRowKeys.Health, Title(SmartRowKeys.Health), health.StatusText,
                health.Level is HealthLevel.Caution or HealthLevel.Bad,
                health.LifePercent is { } p ? $"{p}% life remaining" : null),
        };
        if (smartOk is { } ok)
            rows.Add(new SmartRow(SmartRowKeys.SmartStatus, Title(SmartRowKeys.SmartStatus), ok ? "OK" : "Failure predicted", !ok));
        rows.AddRange(head);
        rows.AddRange(middle);
        rows.AddRange(body);

        return new EvaluationResult(source, health, rows);
    }

    // ---- ATA ---------------------------------------------------------------------------------------------------

    // Returns the drive's own failure-predicted verdict (true means OK), or null when it was not read.
    private static bool? EvaluateAta(DiskReadings r, bool ssdLike, List<SmartRow> head, List<SmartRow> body,
        Action<HealthLevel, string> raise, out int? life)
    {
        var data = r.Ata!;
        life = null;

        // Pre-failure attributes at or below their threshold: the drive's own definition of "about to fail".
        var breached = new HashSet<byte>();
        foreach (var a in data.Attributes)
        {
            var threshold = data.ThresholdFor(a.Id);
            if (a.IsPreFailure && threshold > 0 && a.Current <= threshold)
            {
                breached.Add(a.Id);
                raise(HealthLevel.Bad, $"{SmartAttributeCatalog.DisplayNameFor(a.Id)} has reached its limit.");
            }
        }
        var shown = new HashSet<byte>();

        if (data.FailurePredicted == true)
            raise(HealthLevel.Bad, "The drive reports that a failure is predicted.");

        // Temperature.
        var tempAttr = data.Find(SmartAttributeCatalog.TemperatureId) ?? data.Find(SmartAttributeCatalog.AirflowTemperatureId);
        if (tempAttr is not null)
        {
            var celsius = (int)(tempAttr.Raw & 0xFF);
            if (celsius is >= 1 and <= 120)
            {
                shown.Add(tempAttr.Id);
                head.Add(TemperatureRow(celsius, ssdLike, r.Media, raise, breached.Contains(tempAttr.Id)));
            }
        }

        // Power-on hours and power cycles.
        if (data.Find(SmartAttributeCatalog.PowerOnHoursId) is { } poh)
        {
            var hours = poh.Raw & 0xFFFFFFFF;
            if (hours > 1_000_000) hours = poh.Raw & 0xFFFFFF;
            shown.Add(poh.Id);
            head.Add(HoursRow(hours, breached.Contains(poh.Id)));
        }
        if (data.Find(SmartAttributeCatalog.PowerCyclesId) is { } cycles)
        {
            shown.Add(cycles.Id);
            head.Add(new SmartRow(SmartRowKeys.PowerCycles, Title(SmartRowKeys.PowerCycles),
                Count(cycles.Raw & 0xFFFFFFFF), breached.Contains(cycles.Id)));
        }

        // SSD life remaining, from whichever attribute the drive uses for it.
        if (r.Media != DiskMedia.Hdd)
        {
            foreach (var id in SmartAttributeCatalog.LifeRemainingIds)
            {
                if (data.Find(id) is not { } attr || attr.Current > 100) continue;
                life = attr.Current;
                shown.Add(id);
                body.Add(LifeRow(attr.Current, raise, breached.Contains(id)));
                break;
            }
        }

        // Counter and score rows in table order.
        var emitted = new HashSet<string>();
        foreach (var def in SmartAttributeCatalog.Rows)
        {
            if (!SmartAttributeCatalog.Applicable(def.Applies, r.Media)) continue;
            if (emitted.Contains(def.RowKey)) continue;
            if (data.Find(def.Id) is not { } attr) continue;

            emitted.Add(def.RowKey);
            shown.Add(def.Id);
            var threshold = data.ThresholdFor(def.Id);
            var atLimit = breached.Contains(def.Id);

            switch (def.Style)
            {
                case AttributeStyle.Counter:
                {
                    var count = CounterValue(def.Id, attr.Raw, r.Model);
                    var flagged = count > 0 || atLimit;
                    if (count > 0)
                        raise(HealthLevel.Caution, $"{Title(def.RowKey)} is {Count(count)}.");
                    body.Add(new SmartRow(def.RowKey, Title(def.RowKey), Count(count), flagged));
                    break;
                }
                case AttributeStyle.Score:
                {
                    // The raw count of these is maker specific. G-Sense shows its count; the rate rows show the score.
                    var scoreText = $"score {attr.Current}, worst {attr.Worst}, limit {threshold}";
                    if (def.RowKey == SmartRowKeys.GSense)
                        body.Add(new SmartRow(def.RowKey, Title(def.RowKey), Count(attr.Raw & 0xFFFFFFFF), atLimit, scoreText));
                    else
                        body.Add(new SmartRow(def.RowKey, Title(def.RowKey), $"Score {attr.Current}", atLimit, $"worst {attr.Worst}, limit {threshold}"));
                    break;
                }
                default:
                    body.Add(new SmartRow(def.RowKey, Title(def.RowKey), Count(attr.Raw & 0xFFFFFFFF), atLimit));
                    break;
            }
        }

        // Attributes at their limit that have no row of their own.
        foreach (var attr in data.Attributes)
        {
            if (!breached.Contains(attr.Id) || shown.Contains(attr.Id)) continue;
            body.Add(new SmartRow(SmartRowKeys.VendorAttribute, SmartAttributeCatalog.DisplayNameFor(attr.Id),
                $"Score {attr.Current}", true, $"limit {data.ThresholdFor(attr.Id)}"));
        }

        return data.FailurePredicted is { } predicted ? !predicted : null;
    }

    // Counters are judged on the raw count. A few makes pack extra fields into the high bytes, so only the low 32
    // bits are used. Seagate drives pack the total timeout count into the low 16 bits of the Command Timeout raw value.
    internal static ulong CounterValue(byte id, ulong raw, string model)
    {
        if (id == 188 && IsSeagate(model)) return raw & 0xFFFF;
        return raw & 0xFFFFFFFF;
    }

    private static bool IsSeagate(string model) =>
        model.TrimStart().StartsWith("ST", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("Seagate", StringComparison.OrdinalIgnoreCase);

    // ---- NVMe --------------------------------------------------------------------------------------------------

    private static void EvaluateNvme(NvmeHealth n, bool ssdLike, List<SmartRow> head, List<SmartRow> body,
        Action<HealthLevel, string> raise, out int? life)
    {
        if (n.TemperatureCelsius is { } t)
            head.Add(TemperatureRow(t, true, DiskMedia.Nvme, raise, false));
        head.Add(HoursRow(n.PowerOnHours, false));
        head.Add(new SmartRow(SmartRowKeys.PowerCycles, Title(SmartRowKeys.PowerCycles), Count(n.PowerCycles), false));

        if (n.CriticalWarning != 0)
            raise(HealthLevel.Bad, $"The drive raised a critical warning: {n.CriticalWarningText}.");
        body.Add(new SmartRow(SmartRowKeys.CriticalWarning, Title(SmartRowKeys.CriticalWarning),
            n.CriticalWarningText, n.CriticalWarning != 0));

        var spareLow = n.AvailableSparePercent <= n.AvailableSpareThreshold;
        if (n.AvailableSparePercent < n.AvailableSpareThreshold)
            raise(HealthLevel.Bad, "Available spare is below the drive's threshold.");
        else if (spareLow)
            raise(HealthLevel.Caution, "Available spare has reached the drive's threshold.");
        body.Add(new SmartRow(SmartRowKeys.AvailableSpare, Title(SmartRowKeys.AvailableSpare),
            $"{n.AvailableSparePercent}%", spareLow, $"threshold {n.AvailableSpareThreshold}%"));

        life = Math.Max(0, 100 - n.PercentageUsed);
        if (life <= LifeBadPercent)
            raise(HealthLevel.Bad, $"Only {life}% of the rated life remains.");
        else if (life <= LifeCautionPercent)
            raise(HealthLevel.Caution, $"Only {life}% of the rated life remains.");
        body.Add(new SmartRow(SmartRowKeys.PercentageUsed, Title(SmartRowKeys.PercentageUsed),
            $"{n.PercentageUsed}%", n.PercentageUsed >= PercentageUsedFlag));

        if (n.MediaErrors > 0)
            raise(HealthLevel.Caution, $"Media and data integrity errors: {Count(n.MediaErrors)}.");
        body.Add(new SmartRow(SmartRowKeys.MediaErrors, Title(SmartRowKeys.MediaErrors), Count(n.MediaErrors), n.MediaErrors > 0));
        body.Add(new SmartRow(SmartRowKeys.ErrorLogEntries, Title(SmartRowKeys.ErrorLogEntries), Count(n.ErrorLogEntries), false));
        body.Add(new SmartRow(SmartRowKeys.UnexpectedPowerLoss, Title(SmartRowKeys.UnexpectedPowerLoss), Count(n.UnsafeShutdowns), false));
        body.Add(new SmartRow(SmartRowKeys.DataWritten, Title(SmartRowKeys.DataWritten), Terabytes(n.TerabytesWritten), false));
        body.Add(new SmartRow(SmartRowKeys.DataRead, Title(SmartRowKeys.DataRead), Terabytes(n.TerabytesRead), false));

        if (n.WarningTempMinutes > 0)
            raise(HealthLevel.Caution, $"The drive has spent {Count(n.WarningTempMinutes)} minutes above its warning temperature.");
        body.Add(new SmartRow(SmartRowKeys.WarningTempTime, Title(SmartRowKeys.WarningTempTime),
            $"{Count(n.WarningTempMinutes)} min", n.WarningTempMinutes > 0));
        if (n.CriticalTempMinutes > 0)
            raise(HealthLevel.Caution, $"The drive has spent {Count(n.CriticalTempMinutes)} minutes above its critical temperature.");
        body.Add(new SmartRow(SmartRowKeys.CriticalTempTime, Title(SmartRowKeys.CriticalTempTime),
            $"{Count(n.CriticalTempMinutes)} min", n.CriticalTempMinutes > 0));
    }

    // ---- Windows counters ---------------------------------------------------------------------------------------

    private static void EvaluateWindows(DiskReadings r, bool ssdLike, List<SmartRow> head, List<SmartRow> body,
        Action<HealthLevel, string> raise, out int? life)
    {
        var w = r.Windows!;
        life = null;

        if (w.TemperatureCelsius is >= 1 and <= 120)
            head.Add(TemperatureRow(w.TemperatureCelsius.Value, ssdLike, r.Media, raise, false));
        if (w.PowerOnHours is { } hours)
            head.Add(HoursRow(hours, false));

        if (r.Media != DiskMedia.Hdd && w.WearPercent is >= 0 and <= 100)
        {
            life = 100 - w.WearPercent.Value;
            body.Add(LifeRow(life.Value, raise, false));
        }

        if (r.Media is not (DiskMedia.Ssd or DiskMedia.Nvme) && w.LoadUnloadCycles is { } load)
            body.Add(new SmartRow(SmartRowKeys.LoadCycles, Title(SmartRowKeys.LoadCycles), Count(load), false));

        if (w.ReadErrorsUncorrected is { } read)
        {
            if (read > 0) raise(HealthLevel.Caution, $"Uncorrected read errors: {Count(read)}.");
            body.Add(new SmartRow(SmartRowKeys.ReadErrorsUncorrected, Title(SmartRowKeys.ReadErrorsUncorrected), Count(read), read > 0));
        }
        if (w.WriteErrorsUncorrected is { } write)
        {
            if (write > 0) raise(HealthLevel.Caution, $"Uncorrected write errors: {Count(write)}.");
            body.Add(new SmartRow(SmartRowKeys.WriteErrorsUncorrected, Title(SmartRowKeys.WriteErrorsUncorrected), Count(write), write > 0));
        }
    }

    // ---- shared row builders -----------------------------------------------------------------------------------

    private static SmartRow TemperatureRow(int celsius, bool ssdLike, DiskMedia media, Action<HealthLevel, string> raise, bool atLimit)
    {
        var bad = ssdLike ? SsdBadCelsius : HddBadCelsius;
        var caution = ssdLike ? SsdCautionCelsius : HddCautionCelsius;
        if (celsius >= bad) raise(HealthLevel.Bad, $"Temperature is {celsius} °C.");
        else if (celsius >= caution) raise(HealthLevel.Caution, $"Temperature is {celsius} °C.");
        return new SmartRow(SmartRowKeys.Temperature, Title(SmartRowKeys.Temperature), $"{celsius} °C", celsius >= caution || atLimit);
    }

    private static SmartRow HoursRow(ulong hours, bool atLimit)
    {
        var days = hours / 24;
        return new SmartRow(SmartRowKeys.PowerOnHours, Title(SmartRowKeys.PowerOnHours),
            $"{Count(hours)} hours", atLimit, days > 0 ? $"{Count(days)} days" : null);
    }

    private static SmartRow LifeRow(int percent, Action<HealthLevel, string> raise, bool atLimit)
    {
        if (percent <= LifeBadPercent) raise(HealthLevel.Bad, $"Only {percent}% of the rated life remains.");
        else if (percent <= LifeCautionPercent) raise(HealthLevel.Caution, $"Only {percent}% of the rated life remains.");
        return new SmartRow(SmartRowKeys.SsdLife, Title(SmartRowKeys.SsdLife), $"{percent}%", percent <= LifeCautionPercent || atLimit);
    }

    private static void AddFreeSpace(DiskReadings r, List<SmartRow> rows)
    {
        if (r.FreeBytes is not { } free || r.TotalBytes is not { } total || total == 0) return;
        var percent = (int)Math.Round(free * 100.0 / total);
        rows.Add(new SmartRow(SmartRowKeys.FreeSpace, Title(SmartRowKeys.FreeSpace),
            $"{SizeFormat.Format(free)} free of {SizeFormat.Format(total)}", percent < LowFreeSpacePercent, $"{percent}% free"));
    }

    private static string Title(string key) => SmartExplanations.Find(key)?.Title ?? key;

    internal static string Count(ulong value) => value.ToString("N0", Inv);

    internal static string Terabytes(double tb) => tb < 10 ? $"{tb.ToString("0.00", Inv)} TB" : $"{tb.ToString("0.0", Inv)} TB";
}
