using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTester.Branding;

// One label/value row of the business-details block (e.g. "Phone" / "(555) 010-2030"). Labels are free
// text, so a shop can print ABN instead of EIN, or Mobile instead of Fax, with no code change.
public sealed class BrandRow
{
    public string? Label { get; set; } = "";
    public string? Value { get; set; } = "";
}

// Optional business-details block printed at the top of every report. Read from
// brand.json's "businessDetails" object. Every member is optional: an old brand.json without the object,
// or one with every field blank, produces no block anywhere.
public sealed class BusinessDetails
{
    // Independent switches: the normal HTML report and the 80 mm thermal view can each show the block or not.
    public bool PrintOnFullReport { get; set; } = true;
    public bool PrintOnThermal { get; set; } = true;

    // "left" (default) or "right"; the full report only. The thermal layout is fixed (centred).
    public string? FullReportAlign { get; set; } = "left";

    public string? Name { get; set; } = "";
    public List<string?>? AddressLines { get; set; } = new();
    public List<BrandRow?>? Rows { get; set; } = new();

    public bool IsRightAligned =>
        string.Equals(FullReportAlign?.Trim(), "right", StringComparison.OrdinalIgnoreCase);

    // Trimmed, blank-free views. A blank name, address line, label or value is dropped entirely so nothing
    // leaves an empty line behind; a row needs both a label and a value to be shown.
    public string CleanName => (Name ?? "").Trim();

    public IReadOnlyList<string> CleanAddressLines =>
        (AddressLines ?? new List<string?>())
            .Select(a => (a ?? "").Trim())
            .Where(a => a.Length > 0)
            .ToList();

    public IReadOnlyList<BrandRow> CleanRows =>
        (Rows ?? new List<BrandRow?>())
            .Where(r => r is not null)
            .Select(r => new BrandRow { Label = (r!.Label ?? "").Trim(), Value = (r.Value ?? "").Trim() })
            .Where(r => r.Label!.Length > 0 && r.Value!.Length > 0)
            .ToList();

    public bool HasContent => CleanName.Length > 0 || CleanAddressLines.Count > 0 || CleanRows.Count > 0;
}
