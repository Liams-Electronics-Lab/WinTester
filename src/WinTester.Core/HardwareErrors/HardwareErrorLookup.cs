using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTester.Core.HardwareErrors;

// Builds the web-search link for a hardware error and collapses repeats into groups. Pure logic with no
// Windows dependency.
public static class HardwareErrorLookup
{
    private const string SearchBase = "https://www.google.com/search?q=";

    // A PCI device ID means a specific piece of hardware, so the useful search is for its driver. Without
    // one (processor and memory errors) the event ID plus whatever the message named is the best handle.
    public static string BuildUrl(HardwareErrorEvent error)
    {
        var details = error.Details;

        string query;
        if (details.PciDeviceId is { Length: > 0 } pciId)
        {
            query = $"{pciId} driver download windows";
        }
        else
        {
            var parts = new List<string> { $"WHEA-Logger Event ID {error.EventId}" };
            if (details.Component is { Length: > 0 }) parts.Add(details.Component);
            if (details.ErrorType is { Length: > 0 }) parts.Add(details.ErrorType);
            parts.Add("fix");
            query = string.Join(' ', parts);
        }

        return SearchBase + Uri.EscapeDataString(query);
    }

    // Newest group first. Two errors are the same when their event ID, level, wording and named
    // component/source/type/device/core all match; the APIC ID is part of the key so a single failing core
    // is not averaged in with healthy ones.
    public static IReadOnlyList<HardwareErrorGroup> Group(IEnumerable<HardwareErrorEvent> events)
    {
        return events
            .GroupBy(KeyOf)
            .Select(group =>
            {
                var ordered = group.OrderBy(e => e.Timestamp).ToList();
                return new HardwareErrorGroup
                {
                    Count = ordered.Count,
                    First = ordered[0].Timestamp,
                    Last = ordered[^1].Timestamp,
                    Occurrences = ordered.Select(e => e.Timestamp).OrderByDescending(t => t).ToList(),
                    Latest = ordered[^1]
                };
            })
            .OrderByDescending(g => g.Last)
            .ToList();
    }

    private static string KeyOf(HardwareErrorEvent e)
    {
        var d = e.Details;
        var key = string.Join('|', e.EventId, e.Level, d.Headline, d.Component, d.ErrorSource, d.ErrorType,
            d.PciDeviceId, d.ProcessorApicId);

        // When the message names nothing (the generic "the details are in the data section" events), the
        // data section is the only thing telling two records apart, so it joins the key: records with
        // different data stay separate rows instead of being counted as repeats of one error.
        if (!NamesSomething(d))
            key += "|" + string.Join(';', e.EventData.DataFields.Select(f => f.Name + "=" + f.Value));

        return key;
    }

    private static bool NamesSomething(WheaMessageDetails d) =>
        !string.IsNullOrEmpty(d.Component) || !string.IsNullOrEmpty(d.ErrorSource) ||
        !string.IsNullOrEmpty(d.ErrorType) || !string.IsNullOrEmpty(d.PciDeviceId) ||
        !string.IsNullOrEmpty(d.ProcessorApicId);
}
