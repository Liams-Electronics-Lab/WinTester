using System;
using System.Text.RegularExpressions;

namespace WinTester.Core.HardwareErrors;

// Reads the human-readable text of a WHEA-Logger event. The message is a headline sentence followed by
// "Label: value" lines, for example:
//
//   A fatal hardware error has occurred.
//   Reported by component: Processor Core
//   Error Source: Machine Check Exception
//   Error Type: Cache Hierarchy Error
//   Processor APIC ID: 0
//
// PCI Express events carry a "Primary Device Name" line holding the device's PCI hardware ID instead. The
// wording comes from Windows' own message templates, so the parser only picks out labels it recognises and
// ignores the rest; an unrecognised message still yields a headline and severity.
public static class WheaMessageParser
{
    // Horizontal whitespace only around the colon: a value that is empty on its own line must not pick up
    // the following line as its value.
    private static readonly Regex FieldLine = new(
        @"^[ \t]*(?<key>[A-Za-z][A-Za-z0-9 ]{1,40}?)[ \t]*:[ \t]*(?<value>\S[^\r\n]*?)[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Vendor and device ID pair as it appears in a PCI hardware ID (VEN_8086&DEV_A340), with or without the
    // "PCI\" prefix. Subsystem and revision are deliberately not captured: they narrow a search too far.
    private static readonly Regex PciId = new(
        @"(?:PCI\\)?VEN_(?<ven>[0-9A-Fa-f]{4})&DEV_(?<dev>[0-9A-Fa-f]{4})",
        RegexOptions.Compiled);

    public static WheaMessageDetails Parse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return new WheaMessageDetails();

        string? component = null, source = null, type = null, apic = null;

        foreach (Match match in FieldLine.Matches(description))
        {
            var value = match.Groups["value"].Value;
            switch (match.Groups["key"].Value.Trim().ToLowerInvariant())
            {
                case "reported by component":
                case "component":
                    component ??= value;
                    break;
                case "error source":
                    source ??= value;
                    break;
                case "error type":
                    type ??= value;
                    break;
                case "processor apic id":
                    apic ??= value;
                    break;
            }
        }

        var headline = FirstLine(description);

        return new WheaMessageDetails
        {
            Headline = headline,
            Component = component,
            ErrorSource = source,
            ErrorType = type,
            PciDeviceId = FindPciId(description),
            ProcessorApicId = apic,
            Severity = SeverityOf(headline)
        };
    }

    private static string? FindPciId(string description)
    {
        var match = PciId.Match(description);
        return match.Success
            ? $"PCI\\VEN_{match.Groups["ven"].Value.ToUpperInvariant()}&DEV_{match.Groups["dev"].Value.ToUpperInvariant()}"
            : null;
    }

    private static string FirstLine(string description)
    {
        foreach (var line in description.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return string.Empty;
    }

    private static HardwareErrorSeverity SeverityOf(string headline)
    {
        if (headline.Contains("fatal", StringComparison.OrdinalIgnoreCase))
            return HardwareErrorSeverity.Fatal;

        if (headline.Contains("corrected", StringComparison.OrdinalIgnoreCase))
            return HardwareErrorSeverity.Corrected;

        return HardwareErrorSeverity.Other;
    }
}
