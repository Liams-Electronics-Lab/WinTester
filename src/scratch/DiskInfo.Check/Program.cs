using System.Globalization;
using WinTester.Tools.DiskInfo;

// Logic checks for the pure parts of WinTester.Tools.DiskInfo.
// No WMI, IOCTL or process calls are made. Exit code 0 means all checks passed.

var total = 0;
var failures = 0;

void Eq<T>(string name, T expected, T actual)
{
    total++;
    if (EqualityComparer<T>.Default.Equals(expected, actual)) return;
    failures++;
    Console.WriteLine($"FAIL  {name}: expected {expected}, got {actual}");
}

void Check(string name, bool ok) => Eq(name, true, ok);

const ulong Gb = 1024UL * 1024 * 1024;
const ulong Mb = 1024UL * 1024;

// ---------- Block builders ----------

// ATA data block: 2 byte revision, then 12 byte entries (id, flags lo, flags hi, current, worst, raw x6, reserved).
byte[] AtaBlock(params (byte Id, ushort Flags, byte Cur, byte Worst, ulong Raw)[] entries)
{
    var block = new byte[512];
    for (var i = 0; i < entries.Length; i++)
    {
        var o = 2 + i * 12;
        var e = entries[i];
        block[o] = e.Id;
        block[o + 1] = (byte)(e.Flags & 0xFF);
        block[o + 2] = (byte)(e.Flags >> 8);
        block[o + 3] = e.Cur;
        block[o + 4] = e.Worst;
        for (var b = 0; b < 6; b++) block[o + 5 + b] = (byte)((e.Raw >> (8 * b)) & 0xFF);
    }
    return block;
}

byte[] ThresholdBlock(params (byte Id, byte Threshold)[] entries)
{
    var block = new byte[512];
    for (var i = 0; i < entries.Length; i++)
    {
        var o = 2 + i * 12;
        block[o] = entries[i].Id;
        block[o + 1] = entries[i].Threshold;
    }
    return block;
}

AtaSmartData Ata(bool? predicted, (byte, byte)[] thresholds, params (byte Id, ushort Flags, byte Cur, byte Worst, ulong Raw)[] entries) =>
    AtaSmartParser.Parse(AtaBlock(entries), ThresholdBlock(thresholds), predicted);

DiskReadings Readings(DiskMedia media, AtaSmartData? ata = null, NvmeHealth? nvme = null, WindowsReliability? win = null,
    string model = "Test Disk", int? winHealth = null, ulong? free = null, ulong? totalBytes = null, bool? trim = null) =>
    new(media, model, ata, nvme, win, winHealth, free, totalBytes, trim);

SmartRow? Row(EvaluationResult r, string key) => r.Rows.FirstOrDefault(x => x.Key == key);

byte[] NvmeLog(byte warning = 0, int kelvin = 313, byte spare = 100, byte spareThreshold = 10, byte used = 3,
    ulong read = 1000, ulong written = 2000, ulong cycles = 50, ulong hours = 1234, ulong unsafeShutdowns = 7,
    ulong mediaErrors = 0, ulong errorEntries = 0, uint warnMin = 0, uint critMin = 0)
{
    var log = new byte[512];
    log[0] = warning;
    log[1] = (byte)(kelvin & 0xFF);
    log[2] = (byte)(kelvin >> 8);
    log[3] = spare;
    log[4] = spareThreshold;
    log[5] = used;
    void Put64(int offset, ulong v) => BitConverter.TryWriteBytes(log.AsSpan(offset, 8), v);
    Put64(32, read);
    Put64(48, written);
    Put64(112, cycles);
    Put64(128, hours);
    Put64(144, unsafeShutdowns);
    Put64(160, mediaErrors);
    Put64(176, errorEntries);
    BitConverter.TryWriteBytes(log.AsSpan(192, 4), warnMin);
    BitConverter.TryWriteBytes(log.AsSpan(196, 4), critMin);
    return log;
}

// ---------- SizeFormat ----------
Eq("size 512110190592", "476.94 GB", SizeFormat.Format(512_110_190_592UL));
Eq("size 100 MB", "100.00 MB", SizeFormat.Format(100 * Mb));
Eq("size 2 TB", "2.00 TB", SizeFormat.Format(2048 * Gb));
Eq("size 500 bytes", "500 bytes", SizeFormat.Format(500));

// ---------- AtaSmartParser ----------
{
    var data = Ata(false, new (byte, byte)[] { (5, 36), (197, 0) },
        (5, 0x33, 100, 100, 12), (9, 0x32, 90, 90, 12345), (197, 0x32, 99, 99, 3));
    Eq("ata attribute count", 3, data.Attributes.Count);
    Eq("ata raw 5", 12UL, data.Find(5)!.Raw);
    Eq("ata raw 9", 12345UL, data.Find(9)!.Raw);
    Check("ata pre-failure flag", data.Find(5)!.IsPreFailure);
    Check("ata non pre-failure flag", !data.Find(9)!.IsPreFailure);
    Eq("ata threshold 5", (byte)36, data.ThresholdFor(5));
    Eq("ata threshold missing", (byte)0, data.ThresholdFor(99));
    Check("ata find missing", data.Find(250) is null);

    var wide = Ata(null, Array.Empty<(byte, byte)>(), (188, 0x32, 100, 100, 0x0000_0001_0000_0002UL));
    Eq("ata 48-bit raw", 0x0000_0001_0000_0002UL, wide.Find(188)!.Raw);

    Eq("ata null block", 0, AtaSmartParser.ParseAttributes(null).Count);
    Eq("ata short block", 0, AtaSmartParser.ParseAttributes(new byte[5]).Count);
    Eq("ata empty block", 0, AtaSmartParser.ParseAttributes(new byte[512]).Count);
}

// ---------- NvmeHealthParser ----------
{
    var n = NvmeHealthParser.Parse(NvmeLog())!;
    Eq("nvme temp", 40, n.TemperatureCelsius);
    Eq("nvme spare", (byte)100, n.AvailableSparePercent);
    Eq("nvme threshold", (byte)10, n.AvailableSpareThreshold);
    Eq("nvme used", (byte)3, n.PercentageUsed);
    Eq("nvme hours", 1234UL, n.PowerOnHours);
    Eq("nvme cycles", 50UL, n.PowerCycles);
    Eq("nvme unsafe", 7UL, n.UnsafeShutdowns);
    Eq("nvme warning text none", "None", n.CriticalWarningText);
    Check("nvme tb written", Math.Abs(n.TerabytesWritten - 2000 * 512_000.0 / 1e12) < 1e-12);

    Eq("nvme warning text", "Available spare below threshold, Read only mode", NvmeHealthParser.Parse(NvmeLog(warning: 0x09))!.CriticalWarningText);
    Check("nvme unknown warning bit", NvmeHealthParser.Parse(NvmeLog(warning: 0x80))!.CriticalWarningText.Contains("0x80"));
    Check("nvme zero kelvin", NvmeHealthParser.Parse(NvmeLog(kelvin: 0))!.TemperatureCelsius is null);
    Check("nvme absurd kelvin", NvmeHealthParser.Parse(NvmeLog(kelvin: 900))!.TemperatureCelsius is null);
    Check("nvme null log", NvmeHealthParser.Parse(null) is null);
    Check("nvme short log", NvmeHealthParser.Parse(new byte[100]) is null);

    var big = NvmeLog();
    big[32 + 9] = 1;
    Eq("nvme 128-bit saturates", ulong.MaxValue, NvmeHealthParser.Parse(big)!.DataUnitsRead);
}

// ---------- NVMe request and reply ----------
{
    var req = NvmeSmartReader.BuildHealthLogRequest();
    Eq("nvme request length", 8 + 40 + 512, req.Length);
    Eq("nvme request property id", 50, BitConverter.ToInt32(req, 0));
    Eq("nvme request query type", 0, BitConverter.ToInt32(req, 4));
    Eq("nvme request protocol", 3, BitConverter.ToInt32(req, 8));
    Eq("nvme request data type", 2, BitConverter.ToInt32(req, 12));
    Eq("nvme request log page", 2, BitConverter.ToInt32(req, 16));
    Eq("nvme request sub value", 0, BitConverter.ToInt32(req, 20));
    Eq("nvme request data offset", 40, BitConverter.ToInt32(req, 24));
    Eq("nvme request data length", 512, BitConverter.ToInt32(req, 28));
    Eq("nvme global namespace request", 0xFFFFFFFFu, BitConverter.ToUInt32(NvmeSmartReader.BuildHealthLogRequest(NvmeSmartReader.GlobalNamespace), 20));

    // Reply: 8 byte descriptor header, then the protocol specific structure, then the log at ProtocolDataOffset.
    var reply = new byte[8 + 40 + 512];
    BitConverter.TryWriteBytes(reply.AsSpan(8, 4), 3);
    BitConverter.TryWriteBytes(reply.AsSpan(8 + 16, 4), 40);
    BitConverter.TryWriteBytes(reply.AsSpan(8 + 20, 4), 512);
    var log = NvmeLog(hours: 4321);
    Array.Copy(log, 0, reply, 8 + 40, 512);
    var extracted = NvmeSmartReader.ExtractHealthLog(reply, reply.Length);
    Check("nvme extract not null", extracted is not null);
    Eq("nvme extract hours", 4321UL, NvmeHealthParser.Parse(extracted)!.PowerOnHours);
    Check("nvme extract truncated reply", NvmeSmartReader.ExtractHealthLog(reply, 100) is null);
    Check("nvme extract short buffer", NvmeSmartReader.ExtractHealthLog(new byte[20], 20) is null);

    var wrongProtocol = (byte[])reply.Clone();
    BitConverter.TryWriteBytes(wrongProtocol.AsSpan(8, 4), 1);
    Check("nvme extract wrong protocol", NvmeSmartReader.ExtractHealthLog(wrongProtocol, wrongProtocol.Length) is null);

    var shortData = (byte[])reply.Clone();
    BitConverter.TryWriteBytes(shortData.AsSpan(8 + 20, 4), 100);
    Check("nvme extract short data length", NvmeSmartReader.ExtractHealthLog(shortData, shortData.Length) is null);
}

// ---------- AtaSmartReader.InstanceMatches ----------
{
    const string pnp = @"SCSI\Disk&Ven_Samsung&Prod_SSD_860\4&1b2c3d&0&000000";
    Check("instance match with suffix", AtaSmartReader.InstanceMatches(pnp + "_0", pnp));
    Check("instance match no suffix", AtaSmartReader.InstanceMatches(pnp, pnp));
    Check("instance match case", AtaSmartReader.InstanceMatches(pnp.ToLowerInvariant() + "_0", pnp));
    Check("instance other disk", !AtaSmartReader.InstanceMatches(@"SCSI\Disk&Ven_Samsung&Prod_SSD_860\4&1b2c3d&0&000100_0", pnp));
    Check("instance longer id", !AtaSmartReader.InstanceMatches(pnp + "1_0", pnp));
    Check("instance blank", !AtaSmartReader.InstanceMatches("", pnp));
    Check("pnp blank", !AtaSmartReader.InstanceMatches(pnp + "_0", ""));
}

// ---------- TrimReader.Parse ----------
Eq("trim ntfs enabled", true, TrimReader.Parse("NTFS DisableDeleteNotify = 0  (Allows TRIM operations to be sent to the storage device)\r\nReFS DisableDeleteNotify = 1"));
Eq("trim ntfs disabled", false, TrimReader.Parse("NTFS DisableDeleteNotify = 1\r\nReFS DisableDeleteNotify = 0"));
Eq("trim old single line enabled", true, TrimReader.Parse("DisableDeleteNotify = 0"));
Eq("trim old single line disabled", false, TrimReader.Parse("DisableDeleteNotify = 1"));
Eq("trim not set", true, TrimReader.Parse("NTFS DisableDeleteNotify is not currently set\r\nReFS DisableDeleteNotify is not currently set"));
Check("trim empty", TrimReader.Parse("") is null);
Check("trim null", TrimReader.Parse(null) is null);
Check("trim garbage", TrimReader.Parse("The system cannot find the file specified.") is null);

// ---------- DiskEnumerator ----------
Eq("classify nvme bus", DiskMedia.Nvme, DiskEnumerator.Classify(17, 4, "X"));
Eq("classify ssd media", DiskMedia.Ssd, DiskEnumerator.Classify(11, 4, "X"));
Eq("classify hdd media", DiskMedia.Hdd, DiskEnumerator.Classify(11, 3, "X"));
Eq("classify model nvme", DiskMedia.Nvme, DiskEnumerator.Classify(null, null, "Generic NVMe Disk"));
Eq("classify model ssd", DiskMedia.Ssd, DiskEnumerator.Classify(null, null, "Crucial SSD"));
Eq("classify unknown", DiskMedia.Unknown, DiskEnumerator.Classify(null, null, "WDC WD10EZEX"));
Eq("classify unspecified media", DiskMedia.Unknown, DiskEnumerator.Classify(11, 0, "WDC WD10EZEX"));
Eq("bus name sata", "SATA", DiskEnumerator.BusName(11));
Eq("bus name unknown", "Unknown", DiskEnumerator.BusName(99));
Eq("bus name null", "Unknown", DiskEnumerator.BusName(null));
{
    var parts = new List<(int, char?)> { (0, null), (0, 'C'), (1, 'd'), (1, null) };
    Eq("windows disk by letter", 0, DiskEnumerator.DiskOfLetter(parts, 'c'));
    Eq("windows disk lower case partition", 1, DiskEnumerator.DiskOfLetter(parts, 'D'));
    Check("windows disk not found", DiskEnumerator.DiskOfLetter(parts, 'Z') is null);
}

// ---------- PhysicalDiskItem ----------
{
    var item = new PhysicalDiskItem(2, " Samsung SSD 970 ", "S1", "FW1", "PNP", 512_110_190_592UL, DiskMedia.Nvme, "NVMe", 0, true);
    Eq("display name", "Disk 2: Samsung SSD 970 (476.94 GB) - Windows", item.DisplayName);
    Eq("media text nvme", "NVMe SSD", item.MediaText);
    var plain = item with { Model = "", IsWindowsDisk = false, Media = DiskMedia.Unknown };
    Eq("display name blank model", "Disk 2: Unknown disk (476.94 GB)", plain.DisplayName);
    Eq("media text unknown", "Disk", plain.MediaText);
}

// ---------- SmartEvaluator: no data ----------
{
    var r = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd));
    Eq("none source", SmartSource.None, r.Source);
    Eq("none level", HealthLevel.Unknown, r.Health.Level);
    Eq("none status text", "Unknown", r.Health.StatusText);
    Check("none life", r.Health.LifePercent is null);
    Check("none health row not flagged", Row(r, SmartRowKeys.Health) is { Flagged: false });
}

// ---------- SmartEvaluator: healthy HDD ----------
{
    var ata = Ata(false, new (byte, byte)[] { (5, 36), (197, 0), (198, 0) },
        (1, 0x2F, 100, 100, 0), (5, 0x33, 100, 100, 0), (9, 0x32, 95, 95, 20_000), (10, 0x33, 100, 100, 0),
        (12, 0x32, 99, 99, 1500), (187, 0x32, 100, 100, 0), (188, 0x32, 100, 100, 0), (191, 0x32, 100, 100, 0),
        (194, 0x22, 35, 40, 35), (197, 0x32, 100, 100, 0), (198, 0x30, 100, 100, 0), (199, 0x3E, 200, 200, 0));
    var r = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, ata, free: 200 * Gb, totalBytes: 500 * Gb));
    Eq("hdd source", SmartSource.Ata, r.Source);
    Eq("hdd level", HealthLevel.Good, r.Health.Level);
    Check("hdd no life percent", r.Health.LifePercent is null);
    Eq("hdd smart ok", "OK", Row(r, SmartRowKeys.SmartStatus)?.Value);
    Eq("hdd temperature", "35 °C", Row(r, SmartRowKeys.Temperature)?.Value);
    Eq("hdd hours", "20,000 hours", Row(r, SmartRowKeys.PowerOnHours)?.Value);
    Eq("hdd hours detail", "833 days", Row(r, SmartRowKeys.PowerOnHours)?.Detail);
    Eq("hdd power cycles", "1,500", Row(r, SmartRowKeys.PowerCycles)?.Value);
    Check("hdd spin retry shown", Row(r, SmartRowKeys.SpinRetry) is not null);
    Check("hdd gsense shown", Row(r, SmartRowKeys.GSense) is not null);
    Check("hdd no ssd life row", Row(r, SmartRowKeys.SsdLife) is null);
    Check("hdd no trim row", Row(r, SmartRowKeys.Trim) is null);
    Check("hdd free space shown", Row(r, SmartRowKeys.FreeSpace) is { Flagged: false });
    Check("hdd no flagged rows", r.Rows.All(x => !x.Flagged));
    Check("hdd row for absent attribute hidden", Row(r, SmartRowKeys.SeekErrorRate) is null);
}

// ---------- SmartEvaluator: HDD with counters ----------
{
    var ata = Ata(false, new (byte, byte)[] { (5, 36), (197, 0) },
        (5, 0x33, 100, 100, 8), (194, 0x22, 35, 40, 35), (197, 0x32, 100, 100, 2), (199, 0x3E, 200, 200, 5));
    var r = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, ata));
    Eq("hdd counters level", HealthLevel.Caution, r.Health.Level);
    Check("reallocated flagged", Row(r, SmartRowKeys.ReallocatedSectors) is { Flagged: true, Value: "8" });
    Check("pending flagged", Row(r, SmartRowKeys.PendingSectors) is { Flagged: true, Value: "2" });
    Check("crc flagged", Row(r, SmartRowKeys.UdmaCrc) is { Flagged: true });
    Check("health row flagged on caution", Row(r, SmartRowKeys.Health) is { Flagged: true });
    Check("temperature not flagged", Row(r, SmartRowKeys.Temperature) is { Flagged: false });
}

// ---------- SmartEvaluator: Bad rules ----------
{
    var predicted = Ata(true, Array.Empty<(byte, byte)>(), (9, 0x32, 90, 90, 100));
    var r = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, predicted));
    Eq("failure predicted level", HealthLevel.Bad, r.Health.Level);
    Check("failure predicted row", Row(r, SmartRowKeys.SmartStatus) is { Flagged: true, Value: "Failure predicted" });

    var limit = Ata(false, new (byte, byte)[] { (5, 36) }, (5, 0x33, 30, 30, 0), (9, 0x32, 90, 90, 100));
    var r2 = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, limit));
    Eq("threshold breach level", HealthLevel.Bad, r2.Health.Level);
    Check("threshold breach row flagged", Row(r2, SmartRowKeys.ReallocatedSectors) is { Flagged: true });

    var notPre = Ata(false, new (byte, byte)[] { (9, 100) }, (9, 0x32, 50, 50, 100));
    Eq("non pre-failure below threshold is not bad", HealthLevel.Good, SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, notPre)).Health.Level);

    var zeroThreshold = Ata(false, new (byte, byte)[] { (5, 0) }, (5, 0x33, 1, 1, 0));
    Eq("zero threshold is not bad", HealthLevel.Good, SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, zeroThreshold)).Health.Level);

    var vendor = Ata(false, new (byte, byte)[] { (170, 10) }, (170, 0x33, 5, 5, 0));
    var r3 = SmartEvaluator.Evaluate(Readings(DiskMedia.Ssd, vendor));
    Eq("unlisted attribute at limit is bad", HealthLevel.Bad, r3.Health.Level);
    Check("unlisted attribute row shown", r3.Rows.Any(x => x.Key == SmartRowKeys.VendorAttribute && x.Flagged));
}

// ---------- SmartEvaluator: temperature ----------
{
    HealthLevel LevelAt(DiskMedia media, int celsius) =>
        SmartEvaluator.Evaluate(Readings(media, Ata(false, Array.Empty<(byte, byte)>(), (194, 0x22, 40, 40, (ulong)celsius)))).Health.Level;

    Eq("hdd 49 good", HealthLevel.Good, LevelAt(DiskMedia.Hdd, 49));
    Eq("hdd 50 caution", HealthLevel.Caution, LevelAt(DiskMedia.Hdd, 50));
    Eq("hdd 60 bad", HealthLevel.Bad, LevelAt(DiskMedia.Hdd, 60));
    Eq("ssd 69 good", HealthLevel.Good, LevelAt(DiskMedia.Ssd, 69));
    Eq("ssd 70 caution", HealthLevel.Caution, LevelAt(DiskMedia.Ssd, 70));
    Eq("ssd 80 bad", HealthLevel.Bad, LevelAt(DiskMedia.Ssd, 80));

    // The Windows temperature attribute packs min and max into the high bytes; only the low byte is the reading.
    var packed = Ata(false, Array.Empty<(byte, byte)>(), (194, 0x22, 40, 40, 0x0014_0028_0025UL));
    Eq("packed temperature", "37 °C", Row(SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, packed)), SmartRowKeys.Temperature)?.Value);

    var airflow = Ata(false, Array.Empty<(byte, byte)>(), (190, 0x22, 60, 60, 40));
    Eq("airflow temperature used", "40 °C", Row(SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, airflow)), SmartRowKeys.Temperature)?.Value);

    var nonsense = Ata(false, Array.Empty<(byte, byte)>(), (194, 0x22, 40, 40, 0));
    Check("zero temperature hidden", Row(SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, nonsense)), SmartRowKeys.Temperature) is null);
}

// ---------- SmartEvaluator: power on hours ----------
{
    // Some makes keep extra fields in the high bytes of the raw value; only the low 32 bits are hours.
    var odd = Ata(false, Array.Empty<(byte, byte)>(), (9, 0x32, 90, 90, 0x0000_0001_0000_0000UL | 5000UL));
    var r = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, odd));
    Eq("hours low 32 bits", "5,000 hours", Row(r, SmartRowKeys.PowerOnHours)?.Value);
}

// ---------- SmartEvaluator: Seagate command timeout ----------
{
    var ata = Ata(false, Array.Empty<(byte, byte)>(), (188, 0x32, 100, 100, 0x0000_0002_0000_0000UL));
    var seagate = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, ata, model: "ST1000DM010-2EP102"));
    Check("seagate high bytes ignored", Row(seagate, SmartRowKeys.CommandTimeout) is { Value: "0", Flagged: false });
    var other = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, ata, model: "WDC WD10EZEX"));
    Check("other make uses low 32 bits", Row(other, SmartRowKeys.CommandTimeout) is { Value: "0", Flagged: false });
    var low = Ata(false, Array.Empty<(byte, byte)>(), (188, 0x32, 100, 100, 4));
    Check("command timeout counted", Row(SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, low, model: "WDC")), SmartRowKeys.CommandTimeout) is { Value: "4", Flagged: true });
}

// ---------- SmartEvaluator: SATA SSD ----------
{
    var ata = Ata(false, Array.Empty<(byte, byte)>(),
        (9, 0x32, 99, 99, 500), (12, 0x32, 99, 99, 40), (177, 0x13, 97, 97, 12), (194, 0x22, 30, 30, 30), (241, 0x32, 100, 100, 5000));
    var r = SmartEvaluator.Evaluate(Readings(DiskMedia.Ssd, ata, trim: true));
    Eq("ssd life percent", 97, r.Health.LifePercent);
    Eq("ssd life row", "97%", Row(r, SmartRowKeys.SsdLife)?.Value);
    Eq("ssd trim enabled", "Enabled", Row(r, SmartRowKeys.Trim)?.Value);
    Check("ssd trim not flagged", Row(r, SmartRowKeys.Trim) is { Flagged: false });
    Check("ssd no spin retry", Row(r, SmartRowKeys.SpinRetry) is null);
    Check("ssd no gsense", Row(r, SmartRowKeys.GSense) is null);

    var off = SmartEvaluator.Evaluate(Readings(DiskMedia.Ssd, ata, trim: false));
    Check("ssd trim disabled flagged", Row(off, SmartRowKeys.Trim) is { Value: "Disabled", Flagged: true });
    Eq("trim disabled does not change health", HealthLevel.Good, off.Health.Level);

    var hddWithTrim = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, ata, trim: true));
    Check("hdd never shows trim", Row(hddWithTrim, SmartRowKeys.Trim) is null);

    Eq("life 20 caution", HealthLevel.Caution, SmartEvaluator.Evaluate(Readings(DiskMedia.Ssd, Ata(false, Array.Empty<(byte, byte)>(), (231, 0x13, 20, 20, 0)))).Health.Level);
    Eq("life 21 good", HealthLevel.Good, SmartEvaluator.Evaluate(Readings(DiskMedia.Ssd, Ata(false, Array.Empty<(byte, byte)>(), (231, 0x13, 21, 21, 0)))).Health.Level);
    Eq("life 5 bad", HealthLevel.Bad, SmartEvaluator.Evaluate(Readings(DiskMedia.Ssd, Ata(false, Array.Empty<(byte, byte)>(), (231, 0x13, 5, 5, 0)))).Health.Level);
    Check("life attribute over 100 ignored", SmartEvaluator.Evaluate(Readings(DiskMedia.Ssd, Ata(false, Array.Empty<(byte, byte)>(), (231, 0x13, 200, 200, 0)))).Health.LifePercent is null);

    Check("hdd life attribute not used", SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, Ata(false, Array.Empty<(byte, byte)>(), (231, 0x13, 10, 10, 0)))).Health.Level == HealthLevel.Good);
}

// ---------- SmartEvaluator: NVMe ----------
{
    var good = SmartEvaluator.Evaluate(Readings(DiskMedia.Nvme, nvme: NvmeHealthParser.Parse(NvmeLog()), trim: true));
    Eq("nvme source", SmartSource.Nvme, good.Source);
    Eq("nvme good level", HealthLevel.Good, good.Health.Level);
    Eq("nvme life", 97, good.Health.LifePercent);
    Eq("nvme temp row", "40 °C", Row(good, SmartRowKeys.Temperature)?.Value);
    Eq("nvme hours row", "1,234 hours", Row(good, SmartRowKeys.PowerOnHours)?.Value);
    Eq("nvme spare row", "100%", Row(good, SmartRowKeys.AvailableSpare)?.Value);
    Eq("nvme spare detail", "threshold 10%", Row(good, SmartRowKeys.AvailableSpare)?.Detail);
    Eq("nvme warning row", "None", Row(good, SmartRowKeys.CriticalWarning)?.Value);
    Eq("nvme data written", "0.00 TB", Row(good, SmartRowKeys.DataWritten)?.Value);
    Check("nvme no smart status row", Row(good, SmartRowKeys.SmartStatus) is null);
    Check("nvme trim shown", Row(good, SmartRowKeys.Trim) is not null);
    Check("nvme nothing flagged", good.Rows.All(x => !x.Flagged));

    EvaluationResult Eval(NvmeHealth n) => SmartEvaluator.Evaluate(Readings(DiskMedia.Nvme, nvme: n));

    Eq("nvme critical warning bad", HealthLevel.Bad, Eval(NvmeHealthParser.Parse(NvmeLog(warning: 0x04))!).Health.Level);
    Check("nvme critical warning row flagged", Row(Eval(NvmeHealthParser.Parse(NvmeLog(warning: 0x04))!), SmartRowKeys.CriticalWarning) is { Flagged: true });
    Eq("nvme spare below threshold bad", HealthLevel.Bad, Eval(NvmeHealthParser.Parse(NvmeLog(spare: 5, spareThreshold: 10))!).Health.Level);
    Eq("nvme spare at threshold caution", HealthLevel.Caution, Eval(NvmeHealthParser.Parse(NvmeLog(spare: 10, spareThreshold: 10))!).Health.Level);
    Eq("nvme used 80 caution", HealthLevel.Caution, Eval(NvmeHealthParser.Parse(NvmeLog(used: 80))!).Health.Level);
    Check("nvme used 80 row flagged", Row(Eval(NvmeHealthParser.Parse(NvmeLog(used: 80))!), SmartRowKeys.PercentageUsed) is { Flagged: true });
    Eq("nvme used 95 bad", HealthLevel.Bad, Eval(NvmeHealthParser.Parse(NvmeLog(used: 95))!).Health.Level);
    Eq("nvme used 100 life zero", 0, Eval(NvmeHealthParser.Parse(NvmeLog(used: 100))!).Health.LifePercent);
    Eq("nvme used over 100 life zero", 0, Eval(NvmeHealthParser.Parse(NvmeLog(used: 130))!).Health.LifePercent);
    Eq("nvme media errors caution", HealthLevel.Caution, Eval(NvmeHealthParser.Parse(NvmeLog(mediaErrors: 3))!).Health.Level);
    Check("nvme media errors row flagged", Row(Eval(NvmeHealthParser.Parse(NvmeLog(mediaErrors: 3))!), SmartRowKeys.MediaErrors) is { Flagged: true, Value: "3" });
    Eq("nvme hot 70 caution", HealthLevel.Caution, Eval(NvmeHealthParser.Parse(NvmeLog(kelvin: 343))!).Health.Level);
    Eq("nvme hot 80 bad", HealthLevel.Bad, Eval(NvmeHealthParser.Parse(NvmeLog(kelvin: 353))!).Health.Level);
    Eq("nvme warning temp time caution", HealthLevel.Caution, Eval(NvmeHealthParser.Parse(NvmeLog(warnMin: 12))!).Health.Level);
    Check("nvme error log entries never flagged", Row(Eval(NvmeHealthParser.Parse(NvmeLog(errorEntries: 500))!), SmartRowKeys.ErrorLogEntries) is { Flagged: false });
    Check("nvme unsafe shutdowns never flagged", Row(Eval(NvmeHealthParser.Parse(NvmeLog(unsafeShutdowns: 900))!), SmartRowKeys.UnexpectedPowerLoss) is { Flagged: false });
}

// ---------- SmartEvaluator: Windows counters ----------
{
    var w = new WindowsReliability(38, 12, 800, 0, 0, 250);
    var ssd = SmartEvaluator.Evaluate(Readings(DiskMedia.Ssd, win: w));
    Eq("windows source", SmartSource.WindowsCounters, ssd.Source);
    Eq("windows ssd life", 88, ssd.Health.LifePercent);
    Eq("windows temp", "38 °C", Row(ssd, SmartRowKeys.Temperature)?.Value);
    Check("windows ssd no load cycles", Row(ssd, SmartRowKeys.LoadCycles) is null);

    var hdd = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, win: w));
    Check("windows hdd no life", hdd.Health.LifePercent is null && Row(hdd, SmartRowKeys.SsdLife) is null);
    Check("windows hdd load cycles", Row(hdd, SmartRowKeys.LoadCycles) is { Value: "250" });

    var errors = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, win: new WindowsReliability(null, null, null, 4, 0, null)));
    Eq("windows read errors caution", HealthLevel.Caution, errors.Health.Level);
    Check("windows read errors flagged", Row(errors, SmartRowKeys.ReadErrorsUncorrected) is { Flagged: true, Value: "4" });

    var sparse = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, win: new WindowsReliability(null, null, 100, null, null, null)));
    Check("windows missing fields hidden", Row(sparse, SmartRowKeys.Temperature) is null && Row(sparse, SmartRowKeys.ReadErrorsUncorrected) is null);
}

// ---------- SmartEvaluator: Windows health status and free space ----------
{
    var good = Ata(false, Array.Empty<(byte, byte)>(), (9, 0x32, 90, 90, 100));
    Eq("windows unhealthy bad", HealthLevel.Bad, SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, good, winHealth: 2)).Health.Level);
    Eq("windows warning caution", HealthLevel.Caution, SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, good, winHealth: 1)).Health.Level);
    Eq("windows healthy good", HealthLevel.Good, SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, good, winHealth: 0)).Health.Level);
    Eq("windows unhealthy with no smart is bad", HealthLevel.Bad, SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, winHealth: 2)).Health.Level);

    var low = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, good, free: 40 * Gb, totalBytes: 500 * Gb));
    Check("low free space flagged", Row(low, SmartRowKeys.FreeSpace) is { Flagged: true, Detail: "8% free" });
    Eq("low free space does not change health", HealthLevel.Good, low.Health.Level);
    Check("free space hidden without sizes", Row(SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, good)), SmartRowKeys.FreeSpace) is null);
    Check("free space hidden for zero total", Row(SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, good, free: 0, totalBytes: 0)), SmartRowKeys.FreeSpace) is null);
}

// ---------- SmartEvaluator: row order ----------
{
    var ata = Ata(false, new (byte, byte)[] { (5, 36) },
        (5, 0x33, 100, 100, 0), (9, 0x32, 95, 95, 100), (194, 0x22, 30, 30, 30));
    var r = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, ata, free: 100 * Gb, totalBytes: 200 * Gb));
    var keys = r.Rows.Select(x => x.Key).ToList();
    Eq("first row is health", SmartRowKeys.Health, keys[0]);
    Eq("second row is smart status", SmartRowKeys.SmartStatus, keys[1]);
    Check("temperature before free space", keys.IndexOf(SmartRowKeys.Temperature) < keys.IndexOf(SmartRowKeys.FreeSpace));
    Check("free space before counters", keys.IndexOf(SmartRowKeys.FreeSpace) < keys.IndexOf(SmartRowKeys.ReallocatedSectors));
    Eq("row keys unique", keys.Count, keys.Distinct().Count());
}

// ---------- Catalog ----------
{
    Check("applicable hdd only on hdd", SmartAttributeCatalog.Applicable(AttributeApplies.HddOnly, DiskMedia.Hdd));
    Check("applicable hdd only on unknown", SmartAttributeCatalog.Applicable(AttributeApplies.HddOnly, DiskMedia.Unknown));
    Check("hdd only hidden on ssd", !SmartAttributeCatalog.Applicable(AttributeApplies.HddOnly, DiskMedia.Ssd));
    Check("hdd only hidden on nvme", !SmartAttributeCatalog.Applicable(AttributeApplies.HddOnly, DiskMedia.Nvme));
    Check("ssd only hidden on hdd", !SmartAttributeCatalog.Applicable(AttributeApplies.SsdOnly, DiskMedia.Hdd));
    Check("ssd only shown on unknown", SmartAttributeCatalog.Applicable(AttributeApplies.SsdOnly, DiskMedia.Unknown));
    Check("both shown everywhere", SmartAttributeCatalog.Applicable(AttributeApplies.Both, DiskMedia.Hdd) && SmartAttributeCatalog.Applicable(AttributeApplies.Both, DiskMedia.Nvme));
    Eq("catalog id unique per id", SmartAttributeCatalog.Rows.Count, SmartAttributeCatalog.Rows.Select(d => d.Id).Distinct().Count());
    Eq("display name known", "Wear Leveling Count (attribute 177)", SmartAttributeCatalog.DisplayNameFor(177));
    Eq("display name unknown", "Attribute 250 (0xFA)", SmartAttributeCatalog.DisplayNameFor(250));

    // A second id for the same quantity must not create a second row.
    var both = Ata(false, Array.Empty<(byte, byte)>(), (171, 0x32, 100, 100, 1), (181, 0x32, 100, 100, 2));
    var r = SmartEvaluator.Evaluate(Readings(DiskMedia.Ssd, both));
    Eq("program fail one row", 1, r.Rows.Count(x => x.Key == SmartRowKeys.ProgramFail));
    Eq("program fail first id wins", "1", Row(r, SmartRowKeys.ProgramFail)?.Value);
}

// ---------- Explanations ----------
{
    var keys = typeof(SmartRowKeys).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
    Check("row keys found", keys.Count > 30);
    foreach (var key in keys)
    {
        var e = SmartExplanations.Find(key);
        Check($"explanation exists for {key}", e is not null);
        if (e is null) continue;
        Check($"explanation title for {key}", !string.IsNullOrWhiteSpace(e.Title));
        Check($"explanation body for {key}", e.Body.Length > 80);
    }
    Eq("explanation keys unique", SmartExplanations.All.Count, SmartExplanations.All.Select(e => e.Key).Distinct().Count());
    Check("unknown key returns null", SmartExplanations.Find("nope") is null);

    foreach (var e in SmartExplanations.All)
    {
        Check($"no em dash in {e.Key}", !e.Body.Contains('\u2014') && !e.Title.Contains('\u2014'));
        Check($"no en dash in {e.Key}", !e.Body.Contains('\u2013'));
        Check($"ascii only in {e.Key}", e.Body.All(c => c < 128) && e.Title.All(c => c < 128));
    }

    // Row titles come from the explanation titles, so the table and the popup agree.
    var r = SmartEvaluator.Evaluate(Readings(DiskMedia.Hdd, Ata(false, Array.Empty<(byte, byte)>(), (197, 0x32, 100, 100, 0))));
    Eq("row title from explanation", SmartExplanations.Find(SmartRowKeys.PendingSectors)!.Title, Row(r, SmartRowKeys.PendingSectors)?.Name);

    Eq("search query", "NVMe SSD Percentage Used 3%", SmartExplanations.BuildSearchQuery("NVMe SSD", "Percentage Used", "3%"));
    Eq("search query blanks", "HDD Spin Retry Count", SmartExplanations.BuildSearchQuery("HDD", "Spin Retry Count", "  "));
    Eq("search query whitespace", "HDD Spin Retry Count 0", SmartExplanations.BuildSearchQuery(" HDD ", "Spin  Retry Count", "0"));
    Eq("search url", "https://www.google.com/search?q=SSD%20Temperature%2041%20%C2%B0C", SmartExplanations.BuildSearchUrl("SSD Temperature 41 \u00B0C"));
}

// ---------- PartitionLayoutBuilder ----------
{
    const int Gpt = 2, Mbr = 1;
    const string Efi = "{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}";
    const string Msr = "{E3C9E316-0B5C-4DB8-817D-F92DF00215AE}";
    const string Basic = "{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}";
    const string Recovery = "{DE94BBA4-06D1-4D40-A16A-BFD50179D6AC}";
    const string LdmData = "{AF9B60A0-1431-4F62-BC68-3311714A69AD}";

    RawPartition P(int n, ulong offset, ulong size, int? mbr = null, string gpt = "", char? letter = null,
        bool active = false, bool boot = false, bool system = false, string[]? paths = null) =>
        new(n, offset, size, mbr, gpt, letter, active, boot, system, paths ?? Array.Empty<string>());

    var disk = new RawDisk(0, Gpt, false, false, 476 * Gb);
    var parts = new List<RawPartition>
    {
        P(3, 1 * Gb + 16 * Mb, 474 * Gb - 16 * Mb, gpt: Basic, letter: 'C', boot: true),
        P(1, 1 * Mb, 100 * Mb, gpt: Efi, system: true, paths: new[] { @"\\?\Volume{efi}\" }),
        P(2, 101 * Mb, 16 * Mb, gpt: Msr),
        P(4, 475 * Gb + 512 * Mb, 400 * Mb, gpt: Recovery),
    };
    var vols = new List<RawVolume>
    {
        new(@"\\?\Volume{c}\", 'C', "Windows", "NTFS", 474 * Gb, 100 * Gb, 0),
        new(@"\\?\Volume{efi}\", null, "", "FAT32", 99 * Mb, 70 * Mb, 0),
    };

    var result = PartitionLayoutBuilder.Build(disk, parts, vols);
    var layout = result.Layout!;
    Check("layout built", layout is not null && result.ErrorMessage is null);
    Eq("layout type", "Basic", layout.TypeText);
    Eq("layout style", "GPT", layout.StyleText);
    Eq("layout status", "Online", layout.StatusText);
    Eq("layout name", "Disk 0", layout.Name);

    // Sorted by offset: efi, msr, gap, C, gap, recovery, gap. The 1 MB gap before the first partition is padding and
    // is not shown.
    Eq("layout block count", 7, layout.Partitions.Count);
    Eq("layout gaps", 3, layout.Partitions.Count(p => p.IsUnallocated));
    Eq("layout first is efi", "EFI System Partition", layout.Partitions[0].Title);
    Check("layout efi healthy system", layout.Partitions[0].StatusText.Contains("System") && layout.Partitions[0].StatusText.StartsWith("Healthy"));
    Eq("layout efi size text", "100.00 MB FAT32", layout.Partitions[0].FileSystemText);
    Eq("layout c title", "Windows (C:)", layout.Partitions.First(p => p.Title.Contains("C:")).Title);
    Check("layout c boot", layout.Partitions.First(p => p.Title.Contains("C:")).StatusText.Contains("Boot"));
    Check("layout recovery listed", layout.Partitions.Any(p => p.Title == "Recovery Partition"));
    Check("layout has no tiny gaps", layout.Partitions.Where(p => p.IsUnallocated).All(p => p.SizeBytes > PartitionLayoutBuilder.MinimumGapBytes));
    Eq("layout free space", 100 * Gb + 70 * Mb, result.FreeBytes ?? 0);
    Eq("layout total space", 474 * Gb + 99 * Mb, result.TotalBytes ?? 0);
    Check("layout ordered by offset", layout.Partitions.Zip(layout.Partitions.Skip(1), (a, b) => a.OffsetBytes <= b.OffsetBytes).All(x => x));

    // Unallocated space in the middle and at the end.
    var gaps = PartitionLayoutBuilder.Build(new RawDisk(1, Mbr, false, false, 100 * Gb), new List<RawPartition>
    {
        P(1, 1 * Mb, 20 * Gb, mbr: 7, letter: 'D', active: true),
        P(2, 50 * Gb, 10 * Gb, mbr: 7, letter: 'E'),
    }, new List<RawVolume>()).Layout!;
    Eq("gaps count", 4, gaps.Partitions.Count);
    Eq("gap middle", true, gaps.Partitions[1].IsUnallocated);
    Eq("gap middle size", 30 * Gb - 1 * Mb, gaps.Partitions[1].SizeBytes);
    Eq("gap end", true, gaps.Partitions[3].IsUnallocated);
    Eq("gap end size", 40 * Gb, gaps.Partitions[3].SizeBytes);
    Eq("gap title", "Unallocated", gaps.Partitions[1].Title);
    Eq("mbr style", "MBR", gaps.StyleText);
    Check("mbr primary active", gaps.Partitions[0].StatusText.Contains("Active") && gaps.Partitions[0].StatusText.Contains("Primary Partition"));
    Eq("no volume title uses letter", "(D:)", gaps.Partitions[0].Title);
    Eq("no volume size text", "20.00 GB", gaps.Partitions[0].FileSystemText);
    Check("no volume gives no free space", PartitionLayoutBuilder.Build(new RawDisk(1, Mbr, false, false, 100 * Gb), new List<RawPartition>(), new List<RawVolume>()).FreeBytes is null);

    // Empty and uninitialized disks.
    var empty = PartitionLayoutBuilder.Build(new RawDisk(2, 0, false, false, 50 * Gb), new List<RawPartition>(), new List<RawVolume>()).Layout!;
    Eq("uninitialized status", "Not Initialized", empty.StatusText);
    Eq("uninitialized type", "Unknown", empty.TypeText);
    Eq("uninitialized one block", 1, empty.Partitions.Count);
    Check("uninitialized block unallocated", empty.Partitions[0].IsUnallocated);

    // Offline and read-only.
    var offline = PartitionLayoutBuilder.Build(new RawDisk(3, Gpt, true, true, 50 * Gb), new List<RawPartition>(), new List<RawVolume>()).Layout!;
    Eq("offline status", "Offline", offline.StatusText);
    Check("offline flags", offline.IsOffline && offline.IsReadOnly);

    // Dynamic disks.
    var dyn = PartitionLayoutBuilder.Build(new RawDisk(4, Gpt, false, false, 50 * Gb), new List<RawPartition> { P(1, 1 * Mb, 49 * Gb, gpt: LdmData) }, new List<RawVolume>()).Layout!;
    Eq("dynamic gpt", "Dynamic", dyn.TypeText);
    var dynMbr = PartitionLayoutBuilder.Build(new RawDisk(5, Mbr, false, false, 50 * Gb), new List<RawPartition> { P(1, 1 * Mb, 49 * Gb, mbr: 0x42) }, new List<RawVolume>()).Layout!;
    Eq("dynamic mbr", "Dynamic", dynMbr.TypeText);
    Check("basic is not dynamic", !PartitionLayoutBuilder.IsDynamicPartition(P(1, 0, 1, gpt: Basic)));

    // A volume found by access path when the partition has no letter; unhealthy volume shows its health.
    var byPath = PartitionLayoutBuilder.Build(new RawDisk(6, Gpt, false, false, 10 * Gb),
        new List<RawPartition> { P(1, 1 * Mb, 9 * Gb, gpt: Basic, paths: new[] { @"\\?\Volume{x}\" }) },
        new List<RawVolume> { new(@"\\?\Volume{x}\", null, "Data", "NTFS", 9 * Gb, 1 * Gb, 2) });
    Eq("volume by path title", "Data", byPath.Layout!.Partitions[0].Title);
    Check("unhealthy volume status", byPath.Layout.Partitions[0].StatusText.StartsWith("Unhealthy"));

    // Letters from WMI.
    Eq("letter char", (char?)'C', PartitionLayoutBuilder.LetterOf('C'));
    Eq("letter ushort", (char?)'D', PartitionLayoutBuilder.LetterOf((ushort)68));
    Eq("letter int", (char?)'E', PartitionLayoutBuilder.LetterOf(69));
    Eq("letter string", (char?)'F', PartitionLayoutBuilder.LetterOf(" F: "));
    Check("letter zero", PartitionLayoutBuilder.LetterOf('\0') is null);
    Check("letter blank", PartitionLayoutBuilder.LetterOf("  ") is null);
    Check("letter null", PartitionLayoutBuilder.LetterOf(null) is null);

    // Logical drive in an extended partition.
    var logical = PartitionLayoutBuilder.Build(new RawDisk(7, Mbr, false, false, 10 * Gb),
        new List<RawPartition> { P(5, 1 * Mb, 5 * Gb, mbr: 7, letter: 'G') }, new List<RawVolume>()).Layout!;
    Check("logical drive", logical.Partitions[0].StatusText.Contains("Logical Drive"));
}

// ---------- Service on a non-Windows host ----------
if (!OperatingSystem.IsWindows())
{
    var disk = new PhysicalDiskItem(0, "Test", "", "", "", 100 * Gb, DiskMedia.Ssd, "SATA", null, true);
    var report = DiskInfoService.BuildReport(disk);
    Eq("unsupported health", HealthLevel.Unknown, report.Health.Level);
    Check("unsupported note", report.Notes.Count == 1);
    Check("unsupported no layout", report.Layout is null);
    var list = DiskEnumerator.List();
    Check("unsupported list empty", list.Disks.Count == 0 && list.ErrorMessage is not null);
}

// ---------- Launcher ----------
Check("open url rejects file scheme", !DiskInfoLauncher.OpenUrl("file:///c:/windows/system32/cmd.exe").Success);
Check("open url rejects junk", !DiskInfoLauncher.OpenUrl("not a url").Success);

Console.WriteLine(failures == 0 ? $"All {total} checks passed." : $"{failures} of {total} checks FAILED.");
return failures == 0 ? 0 : 1;
