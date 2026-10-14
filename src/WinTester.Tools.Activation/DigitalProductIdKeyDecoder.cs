using System.Text;

namespace WinTester.Tools.Activation;

// Decodes the 25-character product key encoded inside HKLM\...\CurrentVersion\DigitalProductId, a
// direct port of backup_key.ps1's Convert-DigitalProductIdToKey (Method 5), reimplemented in C# since
// the .ps1 can't run unsigned on the operator's machine. Same byte offset (52), base-24 character set,
// 5-char grouping. Only reads the local machine's own already-installed key back out of its own
// registry: no network calls, no other machine's data.
public static class DigitalProductIdKeyDecoder
{
    private const string KeyChars = "BCDFGHJKMPQRTVWXY2346789";
    private const int Offset = 52;
    private const int KeyLength = 25;

    // Returns null if digitalProductId is missing or too short for a key at the expected offset
    // (52 + 15 bytes): same silent-fail posture as the ps1 script's try/catch.
    public static string? Decode(byte[]? digitalProductId)
    {
        if (digitalProductId is null || digitalProductId.Length < Offset + 15)
            return null;

        // Work on a copy: the ps1 mutates the array in place during the base-24 division, which would
        // corrupt the caller's buffer if it's reused elsewhere.
        var buffer = (byte[])digitalProductId.Clone();
        var key = new char[KeyLength];

        for (var i = KeyLength - 1; i >= 0; i--)
        {
            var r = 0;
            for (var j = 14; j >= 0; j--)
            {
                r = (r * 256) ^ buffer[Offset + j];
                buffer[Offset + j] = (byte)(r / 24);
                r %= 24;
            }
            key[i] = KeyChars[r];
        }

        var sb = new StringBuilder(KeyLength + 4);
        for (var i = 0; i < key.Length; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(key[i]);
        }
        return sb.ToString();
    }
}
