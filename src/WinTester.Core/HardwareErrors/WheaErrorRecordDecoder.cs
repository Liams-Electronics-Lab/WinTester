using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace WinTester.Core.HardwareErrors;

// Reads a binary WHEA error record (the "CPER" record format from the UEFI specification's Appendix N,
// "Common Platform Error Record") when one turns up in an event's data section as a hex string. The header,
// the section descriptors, and the body of any section whose layout is known are decoded; everything else
// stays as raw hex in the surrounding data field. Anything that does not start with the "CPER" signature or
// does not fit the layout is ignored, so a data section of any other shape simply yields no decoded fields.
//
// GUID lookups in this file are only ever a display label: the record's own GUID is always kept in a
// separate field alongside the label (e.g. "Notify type" + "Notify type GUID"), so an unrecognised or
// wrongly-labelled GUID never hides the underlying data.
public static class WheaErrorRecordDecoder
{
    private const int HeaderLength = 128;
    private const int SectionDescriptorLength = 72;

    // A record cannot sensibly have more sections than this; bounds a corrupt count.
    private const int MaxSections = 64;

    // "CPER" as hex, checked before converting so ordinary hex values are not decoded needlessly.
    private const string SignatureHex = "43504552";

    // FILETIME ticks (100ns since 1601-01-01 UTC) bracketing a "plausible" calendar date. Both the header's
    // Record ID and a machine-check section's own Timestamp are sometimes not what they claim to be (see
    // TryFileTime), so a decoded value outside this range is treated as not a date at all rather than
    // printed as a nonsense year.
    private static readonly DateTime FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const ulong MinPlausibleFileTime = 125_911_584_000_000_000; // 2000-01-01
    private const ulong MaxPlausibleFileTime = 157_469_184_000_000_000; // 2100-01-01

    // UEFI spec Appendix N, Table N.5 ("Section Type"), plus the two Windows-specific machine-check section
    // types below that Microsoft's own public headers don't document by name.
    private static readonly Dictionary<Guid, string> SectionTypes = new()
    {
        [Guid.Parse("9876CCAD-47B4-4BDB-B65E-16F193C4F3DB")] = "Generic processor error",
        [Guid.Parse("DC3EA0B0-A144-4797-B95B-53FA242B6E1D")] = "IA32 / AMD64 processor error",
        [Guid.Parse("E19E3D16-BC11-11E4-9CAA-C2051D5D46B0")] = "ARM processor error",
        [Guid.Parse("A5BC1114-6F64-4EDE-B863-3E83ED7C83B1")] = "Platform memory error",
        [Guid.Parse("D995E954-BBC1-430F-AD91-B44DCB3C6F35")] = "PCI Express error",
        [Guid.Parse("C5753963-3B84-4095-BF78-EDDAD3F9C9DD")] = "PCI/PCI-X bus error",
        [Guid.Parse("EB5E4685-CA66-4769-B6A2-26068B001326")] = "PCI component error",
        [Guid.Parse("81212A96-09ED-4996-9471-8D729C8E69ED")] = "Firmware error record reference",
        [Guid.Parse("5B51FEF7-C79D-4434-8F1B-AA62DE3E2C64")] = "DMA remapping error (generic)",

        // WHEA_XPF_MCA_SECTION: Microsoft's own extended IA32/X64 machine-check section (distinct from the
        // UEFI-standard "IA32 / AMD64 processor error" section above). Not in any Microsoft Learn page, but
        // named consistently across real-world Windows WHEA-19 records and by the community reference
        // decoder ralish/DecodeWheaRecord. Its body is decoded below (see DecodeXpfMcaSection).
        [Guid.Parse("8A1E1D01-42F9-4557-9C33-565E5CC3F7E8")] = "IA32 / AMD64 machine check (MCA) error",

        // WHEA_ERROR_RECOVERY_INFO_SECTION: undocumented by Microsoft, but attested identically across many
        // real Windows machine-check records (including in Microsoft's own Q&A forum) and named by
        // ralish/DecodeWheaRecord. Its body's layout isn't published anywhere, so only the section
        // descriptor (type, severity, length) is shown; the body itself stays undecoded.
        [Guid.Parse("C34832A1-02C3-4C52-A9F1-9F1D5D7723FC")] = "Error recovery information"
    };

    // UEFI spec Appendix N, section N.2.1.1 ("Notification Type") — every standard type it defines.
    private static readonly Dictionary<Guid, string> NotifyTypes = new()
    {
        [Guid.Parse("2DCE8BB1-BDD7-450E-B9AD-9CF4EBD4F890")] = "Corrected machine check (CMC)",
        [Guid.Parse("4E292F96-D843-4A55-A8C2-D481F27EBEEE")] = "Corrected platform error (CPE)",
        [Guid.Parse("E8F56FFE-919C-4CC5-BA88-65ABE14913BB")] = "Machine check exception (MCE)",
        [Guid.Parse("CF93C01F-1A16-4DFC-B8BC-9C4DAF67C104")] = "PCI Express error (PCIe)",
        [Guid.Parse("CC5263E8-9308-454A-89D0-340BD39BC98E")] = "INIT",
        [Guid.Parse("5BAD89FF-B7E6-42C9-814A-CF2485D6E98A")] = "Non-maskable interrupt (NMI)",
        [Guid.Parse("3D61A466-AB40-409A-A698-F362D464B38F")] = "Boot error",
        [Guid.Parse("667DD791-C6B3-4C27-8A6B-0F8E722DEB41")] = "DMA remapping error",
        [Guid.Parse("9A78788A-BBE8-11E4-809E-67611E5D46B0")] = "Synchronous external abort (SEA)",
        [Guid.Parse("5C284C81-B0AE-4E87-A322-B04C85624323")] = "SError interrupt (SEI)",
        [Guid.Parse("09A9D5AC-5204-4214-96E5-94992E752BCD")] = "Platform error interrupt (PEI)",
        [Guid.Parse("69293BC9-41DF-49A3-B4BD-4FB0DB3041F6")] = "CXL component"

        // Deliberately not listed: 919448B2-3739-4B7F-A8F1-E0062805C2A3, seen on real corrected-machine-check
        // (WHEA-19) events from more than one vendor's hardware, which suggests it is a fixed Windows-internal
        // notify type (a "CMCI" / interrupt-delivered corrected-MC type has been proposed for it online) rather
        // than a per-machine random value — but no Microsoft or UEFI source confirms a name for it, so it is
        // left unrecognised rather than guessed at. It still prints as a raw "Notify type GUID" field.
    };

    // The Creator ID at header offset 64 identifies whoever wrote the record. This is the one and only value
    // seen there across every real Windows-authored record checked (including examples from other machines
    // entirely unrelated to this one), so it reads as a fixed Windows/PSHED constant, not a per-machine or
    // per-vendor value.
    private static readonly Dictionary<Guid, string> CreatorIds = new()
    {
        [Guid.Parse("CF07C4BD-B789-4E18-B3C4-1F732CB57131")] = "Windows"
    };

    // Decodes the first data field that holds a binary error record.
    public static DecodedRecord Decode(IEnumerable<HardwareErrorField> dataFields)
    {
        foreach (var field in dataFields)
        {
            if (ParseHex(field.Value) is not { } bytes) continue;

            var decoded = DecodeRecord(bytes);
            if (decoded.Fields.Count > 0) return decoded;
        }

        return new DecodedRecord(Array.Empty<HardwareErrorField>(), null);
    }

    public static DecodedRecord DecodeRecord(byte[] record)
    {
        if (record.Length < HeaderLength ||
            record[0] != (byte)'C' || record[1] != (byte)'P' || record[2] != (byte)'E' || record[3] != (byte)'R')
            return new DecodedRecord(Array.Empty<HardwareErrorField>(), null);

        var span = record.AsSpan();
        var fields = new List<HardwareErrorField>();

        var revision = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10));
        var severity = SeverityOf(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12)));
        var validation = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20));
        var creatorId = ReadGuid(record, 64);
        var notifyType = ReadGuid(record, 80);
        var recordId = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(96));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(104));

        // Both bytes of the revision are meant to be read as hex digits (the field is BCD, per the UEFI
        // spec), not the minor byte as decimal — 0x0210 is "2.10", not "2.16".
        fields.Add(new HardwareErrorField("Record revision",
            $"{revision >> 8:X}.{revision & 0xFF:X2} (0x{revision:X4})"));
        fields.Add(new HardwareErrorField("Record severity", HardwareErrorText.SeverityLabel(severity)));
        fields.Add(new HardwareErrorField("Record length", $"{length} bytes"));
        fields.Add(new HardwareErrorField("Section count", sectionCount.ToString()));

        // Validation bit 1 says the timestamp is valid. The UEFI spec calls this field BCD, but real Windows
        // records write it as plain binary — a BCD read rejects perfectly good timestamps whose byte values
        // (like 0x1A) aren't valid BCD digits. It's also UTC in practice despite the spec's "local time"
        // wording, confirmed by comparing it against this same event's own TimeCreated; every other time this
        // decoder shows is local, so both readings are shown here rather than picking one silently.
        if ((validation & 0x2) != 0 && TryBinaryTimestamp(record, out var timestampUtc))
            fields.Add(new HardwareErrorField("Record timestamp",
                $"{timestampUtc.ToLocalTime().ToString(HardwareErrorText.TimeFormat)} " +
                $"(UTC {timestampUtc.ToString(HardwareErrorText.TimeFormat)})"));

        // The 16 creator bytes are a GUID for Windows-written records but plain ASCII text for firmware-written
        // ones (e.g. a board ID), so a name-less creator is shown as that text when its bytes read as text.
        var creatorName = NameOf(CreatorIds, creatorId);
        if (creatorName == "Unrecognised" && TryAsciiId(record.AsSpan(64, 16), out var creatorAscii))
            creatorName = $"{creatorAscii} (ASCII text)";
        fields.Add(new HardwareErrorField("Record creator", creatorName));
        fields.Add(new HardwareErrorField("Record creator GUID", creatorId.ToString().ToUpperInvariant()));

        // Validation bit 0: the platform ID (bytes 32-47) is valid. Same GUID-or-ASCII convention as above.
        if ((validation & 0x1) != 0)
        {
            var platformId = ReadGuid(record, 32);
            fields.Add(new HardwareErrorField("Platform ID",
                TryAsciiId(record.AsSpan(32, 16), out var platformAscii)
                    ? $"{platformAscii} (ASCII text)"
                    : "(not text)"));
            fields.Add(new HardwareErrorField("Platform ID GUID", platformId.ToString().ToUpperInvariant()));
        }
        fields.Add(new HardwareErrorField("Notify type", NameOf(NotifyTypes, notifyType)));
        fields.Add(new HardwareErrorField("Notify type GUID", notifyType.ToString().ToUpperInvariant()));

        // Record ID has no defined format beyond "unique alongside the Creator ID" — but Windows' own WHEA
        // implementation happens to generate it from a FILETIME-based counter, so it usually decodes to a
        // real (if not especially meaningful — see the field's own description) date. Shown as hex first
        // since that's what the spec actually promises; the date is a bonus reading, not a claim about when
        // the error happened.
        var recordIdText = $"0x{recordId:X16}";
        if (TryFileTime(recordId, out var recordIdTime))
            recordIdText += $" (as a time: {recordIdTime.ToLocalTime().ToString(HardwareErrorText.TimeFormat)} " +
                             $"(UTC {recordIdTime.ToString(HardwareErrorText.TimeFormat)}))";
        fields.Add(new HardwareErrorField("Record ID", recordIdText));

        fields.Add(new HardwareErrorField("Record flags", $"0x{flags:X8}"));

        var count = Math.Min((int)sectionCount, MaxSections);
        for (var i = 0; i < count; i++)
        {
            var offset = HeaderLength + i * SectionDescriptorLength;
            if (offset + SectionDescriptorLength > record.Length) break;

            var sectionOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset));
            var sectionLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4));
            var validMask = record[offset + 10];
            var typeGuid = ReadGuid(record, offset + 16);
            var sectionSeverity = SeverityOf(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 48)));

            var label = $"Section {i + 1}";
            var typeName = NameOf(SectionTypes, typeGuid);
            var text = $"{typeName}, {HardwareErrorText.SeverityLabel(sectionSeverity)}, " +
                       $"{sectionLength} bytes at offset {sectionOffset}";

            // Mask bit 1: the 20-byte FRU (replaceable part) text is valid.
            if ((validMask & 0x2) != 0 && FruText(record, offset + 52) is { Length: > 0 } fru)
                text += $", FRU: {fru}";

            fields.Add(new HardwareErrorField(label, text));
            fields.Add(new HardwareErrorField($"{label} GUID", typeGuid.ToString().ToUpperInvariant()));

            if (sectionOffset >= 0 && sectionLength >= 0 &&
                sectionOffset + sectionLength <= record.Length)
            {
                var body = record.AsSpan(sectionOffset, sectionLength);

                if (typeGuid == Guid.Parse("DC3EA0B0-A144-4797-B95B-53FA242B6E1D"))
                    DecodeIa32X64ProcessorSection(body, label, fields);
                else if (typeGuid == Guid.Parse("8A1E1D01-42F9-4557-9C33-565E5CC3F7E8"))
                    DecodeXpfMcaSection(body, label, fields);
                else if (!SectionTypes.ContainsKey(typeGuid) && TryUtf16Text(body, out var sectionText))
                    fields.Add(new HardwareErrorField($"{label} text", sectionText));
            }
        }

        return new DecodedRecord(fields, severity);
    }

    // UEFI spec Appendix N, Table N.7 ("IA32/X64 Processor Error Section", type DC3EA0B0-...): only the
    // fixed-size header (APIC ID + CPUID Info) is decoded — the variable-length error/context structures
    // that can follow are not.
    private static void DecodeIa32X64ProcessorSection(ReadOnlySpan<byte> body, string label, List<HardwareErrorField> fields)
    {
        if (body.Length < 64) return;

        var validBits = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(0));
        var apicId = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(8));
        var cpuidEax = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(16));

        if ((validBits & 0x1) != 0)
            fields.Add(new HardwareErrorField($"{label} local APIC ID", $"{apicId} (0x{apicId:X})"));

        if ((validBits & 0x2) != 0)
        {
            // CPUID leaf 1, EAX: family/model/stepping, with the family==6/0xF extended-field folding the
            // x86 architecture manuals define.
            var stepping = cpuidEax & 0xF;
            var model = (cpuidEax >> 4) & 0xF;
            var family = (cpuidEax >> 8) & 0xF;
            var extModel = (cpuidEax >> 16) & 0xF;
            var extFamily = (cpuidEax >> 20) & 0xFF;

            var effectiveFamily = family == 0xF ? family + extFamily : family;
            var effectiveModel = family is 0x6 or 0xF ? model + (extModel << 4) : model;

            fields.Add(new HardwareErrorField($"{label} CPU signature",
                $"family {effectiveFamily}, model 0x{effectiveModel:X} ({effectiveModel}), stepping {stepping} " +
                $"(CPUID 0x{cpuidEax:X8})"));
        }
    }

    // WHEA_XPF_MCA_SECTION (type 8A1E1D01-..., see the SectionTypes comment above for sourcing). Layout
    // confirmed against this event's own raw bytes (offsets are not published by Microsoft): UInt32
    // VersionNumber @0, UInt32 CpuVendor @4, Int64 Timestamp (FILETIME) @8, UInt32 ProcessorNumber @16,
    // UInt64 GlobalStatus @20, UInt64 InstructionPointer @28, UInt32 BankNumber @36, UInt64 Status (the MCi_
    // STATUS MSR) @40, UInt64 Address @48, UInt64 Misc @56, UInt32 ExtendedRegisterCount @64, UInt32 ApicId
    // @68 — all packed with no padding between fields.
    private static void DecodeXpfMcaSection(ReadOnlySpan<byte> body, string label, List<HardwareErrorField> fields)
    {
        if (body.Length < 72) return;

        var cpuVendor = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4));
        var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(8));
        var processorNumber = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(16));
        var bankNumber = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(36));
        var status = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(40));
        var apicId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(68));

        if (TryFileTime(timestamp, out var eventTimeUtc))
            fields.Add(new HardwareErrorField($"{label} event time",
                $"{eventTimeUtc.ToLocalTime().ToString(HardwareErrorText.TimeFormat)} " +
                $"(UTC {eventTimeUtc.ToString(HardwareErrorText.TimeFormat)})"));

        fields.Add(new HardwareErrorField($"{label} CPU vendor", CpuVendorName(cpuVendor)));
        fields.Add(new HardwareErrorField($"{label} logical processor", processorNumber.ToString()));
        fields.Add(new HardwareErrorField($"{label} APIC ID", $"{apicId} (0x{apicId:X})"));
        fields.Add(new HardwareErrorField($"{label} MCA bank", bankNumber.ToString()));
        fields.Add(new HardwareErrorField($"{label} MCi_STATUS", $"0x{status:X16}"));
        fields.Add(new HardwareErrorField($"{label} status flags", MciStatusFlags(status)));

        // IA-32/AMD64 MCi_STATUS layout (Intel SDM Vol. 3B §16, AMD64 APM Vol. 2 §14): bits 15:0 are the MCA
        // error code, bits 31:16 the model-specific error code. Only a handful of the low values are simple,
        // fully-defined codes (e.g. 0x0005 = "internal parity error"); the rest are compound bitfields this
        // decoder doesn't attempt to break down further, so only the two raw sub-fields are shown.
        fields.Add(new HardwareErrorField($"{label} MCA error code",
            $"0x{status & 0xFFFF:X4} (model-specific code 0x{(status >> 16) & 0xFFFF:X4})"));
    }

    private static string CpuVendorName(uint vendor) => vendor switch
    {
        0 => "Unknown",
        1 => "Intel",
        2 => "AMD",
        3 => "Hygon",
        _ => $"Unknown ({vendor})"
    };

    // VAL/OVER/UC/PCC are the top status bits every x86 MCi_STATUS register defines; UC decides
    // corrected-vs-uncorrected rather than the record/section severity, since it's the actual hardware bit.
    private static string MciStatusFlags(ulong status)
    {
        var parts = new List<string>();
        if ((status & (1UL << 63)) != 0) parts.Add("valid");
        parts.Add((status & (1UL << 61)) != 0 ? "uncorrected" : "corrected");
        if ((status & (1UL << 62)) != 0) parts.Add("overflow");
        if ((status & (1UL << 57)) != 0) parts.Add("processor context corrupt");
        return string.Join(", ", parts);
    }

    private static byte[]? ParseHex(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);

        if (text.Length < HeaderLength * 2 || text.Length % 2 != 0 ||
            !text.StartsWith(SignatureHex, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Guid ReadGuid(byte[] record, int offset) => new Guid(record.AsSpan(offset, 16));

    private static string NameOf(Dictionary<Guid, string> names, Guid guid) =>
        names.TryGetValue(guid, out var name) ? name : "Unrecognised";

    private static HardwareErrorSeverity SeverityOf(uint severity) => severity switch
    {
        0 => HardwareErrorSeverity.Recoverable,
        1 => HardwareErrorSeverity.Fatal,
        2 => HardwareErrorSeverity.Corrected,
        3 => HardwareErrorSeverity.Informational,
        _ => HardwareErrorSeverity.Other
    };

    // Header offset 24, 8 bytes, read as plain binary (see the "Record timestamp" field comment above for
    // why this isn't BCD despite the spec).
    private static bool TryBinaryTimestamp(byte[] record, out DateTime utc)
    {
        utc = default;

        int second = record[24], minute = record[25], hour = record[26];
        int day = record[28], month = record[29], year = record[30], century = record[31];

        if (second > 59 || minute > 59 || hour > 23 || month is < 1 or > 12) return false;

        try
        {
            utc = new DateTime(century * 100 + year, month, day, hour, minute, second, DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // Interprets a raw 64-bit value as a Windows FILETIME (100ns ticks since 1601-01-01 UTC), only when it
    // lands in a plausible calendar range — used for both Record ID and a machine-check section's Timestamp,
    // neither of which is guaranteed by any spec to actually be a FILETIME, so a wildly out-of-range value is
    // reported as "not a date" rather than printed as a nonsense year.
    private static bool TryFileTime(ulong value, out DateTime utc)
    {
        utc = default;
        if (value < MinPlausibleFileTime || value > MaxPlausibleFileTime) return false;

        utc = FileTimeEpoch.AddTicks((long)value);
        return true;
    }

    // True when the bytes are printable ASCII up to a run of NULs (at least 4 characters), as firmware
    // writes an ID string into a GUID-sized field.
    private static bool TryAsciiId(ReadOnlySpan<byte> bytes, out string text)
    {
        text = string.Empty;

        var length = 0;
        while (length < bytes.Length && bytes[length] != 0) length++;
        if (length < 4) return false;

        for (var i = 0; i < length; i++)
            if (bytes[i] < 0x20 || bytes[i] > 0x7E) return false;

        for (var i = length; i < bytes.Length; i++)
            if (bytes[i] != 0) return false;

        text = Encoding.ASCII.GetString(bytes.Slice(0, length));
        return true;
    }

    // The first run of at least 6 printable ASCII characters stored as UTF-16LE (each followed by a zero
    // byte) in a section body, capped at 256 characters. Firmware-defined sections such as a device path
    // carry their meaning as a string like this; an unrecognised section with none of it yields nothing.
    private static bool TryUtf16Text(ReadOnlySpan<byte> body, out string text)
    {
        text = string.Empty;

        var i = 0;
        while (i + 1 < body.Length)
        {
            var builder = new StringBuilder();
            var j = i;
            while (j + 1 < body.Length && body[j + 1] == 0 && body[j] >= 0x20 && body[j] <= 0x7E &&
                   builder.Length < 256)
            {
                builder.Append((char)body[j]);
                j += 2;
            }

            if (builder.Length >= 6)
            {
                text = builder.ToString();
                return true;
            }

            i++;
        }

        return false;
    }

    // Up to the first NUL, with anything unprintable shown as '?'.
    private static string FruText(byte[] record, int offset)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            var b = record[offset + i];
            if (b == 0) break;
            builder.Append(b >= 0x20 && b <= 0x7E ? (char)b : '?');
        }

        return builder.ToString().Trim();
    }
}

// A decoded record's fields plus the severity from its own header, so a caller (the scanner) can fall back
// to it when the event's description doesn't classify the error itself.
public readonly record struct DecodedRecord(IReadOnlyList<HardwareErrorField> Fields, HardwareErrorSeverity? Severity);
