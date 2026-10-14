using System.Collections.Generic;
using System.Windows.Media;
using WinTester.Core.Devices;

namespace WinTester.App.ViewModels;

// One row in the Devices window's tree. Three node kinds share this single type (category → class →
// device) so the TreeView needs exactly one HierarchicalDataTemplate: an extra type per level would buy
// nothing but three near-identical templates, since the rows differ only in which fields are populated.
// Built once per scan and never mutated, so no change notification is needed.
public sealed class DeviceTreeNodeViewModel
{
    // Category and class rows: the heading text. Device rows: the device's friendly name.
    public required string Label { get; init; }

    // Trailing count on group rows ("4 devices"); empty on device rows.
    public string CountText { get; init; } = string.Empty;

    // Device rows only: "Code 43 — Stopped, reported problems".
    public string ErrorText { get; init; } = string.Empty;

    // Device rows only: the long explanation, plus manufacturer when WMI reported one.
    public string DetailText { get; init; } = string.Empty;

    // Severity colour for the error line and the group-row accent bar. Amber for driver problems, red for
    // device errors, grey for the deliberately-disabled.
    public Brush AccentBrush { get; init; } = Brushes.Gray;

    // Null on group rows. Its presence is what makes a row double-clickable — the window checks this
    // rather than a node-kind enum, since opening properties is the only behaviour that differs.
    public ProblemDevice? Device { get; init; }

    public bool IsDevice => Device is not null;

    // Groups start open: the whole point of the window is seeing every problem at a glance, and a tech
    // shouldn't have to expand three categories to find out whether the machine is clean.
    public bool IsExpanded { get; init; } = true;

    public IReadOnlyList<DeviceTreeNodeViewModel> Children { get; init; } = [];
}
