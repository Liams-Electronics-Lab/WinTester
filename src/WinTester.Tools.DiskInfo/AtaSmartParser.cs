namespace WinTester.Tools.DiskInfo;

// One entry of the ATA S.M.A.R.T. attribute table. Raw is the 48-bit raw value, little endian.
public sealed record AtaAttribute(byte Id, ushort Flags, byte Current, byte Worst, ulong Raw)
{
    // Bit 0 of the status flags marks a pre-failure attribute: reaching its threshold means the drive expects to fail.
    public bool IsPreFailure => (Flags & 0x0001) != 0;
}

public sealed record AtaSmartData(
    IReadOnlyList<AtaAttribute> Attributes,
    IReadOnlyDictionary<byte, byte> Thresholds,
    bool? FailurePredicted)
{
    public AtaAttribute? Find(byte id)
    {
        foreach (var attribute in Attributes)
            if (attribute.Id == id) return attribute;
        return null;
    }

    public byte ThresholdFor(byte id) => Thresholds.TryGetValue(id, out var t) ? t : (byte)0;
}

// Parses the 512-byte ATA S.M.A.R.T. data block and the matching threshold block. Pure: no I/O.
//
// Both blocks start with a 2 byte revision number followed by 30 entries of 12 bytes. A data entry is: id, 2 status
// flag bytes, current value, worst value, 6 raw bytes, 1 reserved byte. A threshold entry is: id, threshold, 10
// reserved bytes. An id of 0 marks an unused slot.
public static class AtaSmartParser
{
    public const int BlockLength = 512;
    private const int HeaderLength = 2;
    private const int EntryLength = 12;
    private const int EntryCount = 30;

    public static IReadOnlyList<AtaAttribute> ParseAttributes(byte[]? block)
    {
        var list = new List<AtaAttribute>();
        if (block is null || block.Length < HeaderLength + EntryLength) return list;

        for (var i = 0; i < EntryCount; i++)
        {
            var offset = HeaderLength + i * EntryLength;
            if (offset + EntryLength > block.Length) break;

            var id = block[offset];
            if (id == 0) continue;

            var flags = (ushort)(block[offset + 1] | (block[offset + 2] << 8));
            ulong raw = 0;
            for (var b = 5; b >= 0; b--)
                raw = (raw << 8) | block[offset + 5 + b];

            list.Add(new AtaAttribute(id, flags, block[offset + 3], block[offset + 4], raw));
        }

        return list;
    }

    public static IReadOnlyDictionary<byte, byte> ParseThresholds(byte[]? block)
    {
        var map = new Dictionary<byte, byte>();
        if (block is null || block.Length < HeaderLength + EntryLength) return map;

        for (var i = 0; i < EntryCount; i++)
        {
            var offset = HeaderLength + i * EntryLength;
            if (offset + EntryLength > block.Length) break;

            var id = block[offset];
            if (id == 0) continue;
            map[id] = block[offset + 1];
        }

        return map;
    }

    public static AtaSmartData Parse(byte[]? dataBlock, byte[]? thresholdBlock, bool? failurePredicted) =>
        new(ParseAttributes(dataBlock), ParseThresholds(thresholdBlock), failurePredicted);
}
