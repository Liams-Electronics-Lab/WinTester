using System;
using System.Collections.Generic;

namespace WinTester.Core.Devices;

// How a problem device is grouped in the Devices tool's tree. Derived entirely from the device's
// Configuration Manager error code (see DeviceErrorCodes), not from anything the UI decides.
public enum DeviceProblemCategory
{
    // Codes where the fix is a driver: none installed, the wrong one installed, one that failed to load,
    // or one blocked by signing/compatibility policy.
    DriverProblem,

    // Codes where the driver isn't the obvious culprit: the device won't start, has no resources, is
    // reporting a hardware fault, or was stopped by Windows.
    DeviceError,

    // Code 22 only. Kept apart from DeviceError because a disabled device is usually somebody's deliberate
    // choice rather than a fault, and a refurb tech reading the list shouldn't have to mentally filter it
    // out of the genuine failures.
    Disabled
}

// One device reported by Windows as not working properly.
public sealed record ProblemDevice
{
    public required string Name { get; init; }

    // Raw WMI PNPClass ("Net", "Display", "HIDClass", ...). Empty for devices Windows hasn't classified,
    // which are the ones Device Manager files under "Other devices".
    public required string PnpClass { get; init; }

    // PnpClass resolved to the wording Device Manager uses ("Network adapters"), for the tree's group rows.
    public required string ClassDisplayName { get; init; }

    // Full PnP instance ID (e.g. "PCI\VEN_8086&DEV_1234\3&11583659&0&C8"). This is what identifies the
    // device to the properties dialog, so it has to survive intact to the UI layer.
    public required string DeviceId { get; init; }

    public string? Manufacturer { get; init; }

    public required uint ErrorCode { get; init; }

    // Short label for the badge next to the device name.
    public required string ErrorSummary { get; init; }

    // The longer explanation shown under the device name.
    public required string ErrorDetail { get; init; }

    public required DeviceProblemCategory Category { get; init; }

    // Raw WMI Status ("OK", "Error", "Degraded", "Unknown"). Informational only; the error code is what
    // everything else keys off.
    public string? Status { get; init; }
}

// Result of one scan. A failed scan returns an empty device list plus ErrorMessage rather than throwing,
// so the window always has something to render.
public sealed record DeviceProblemSnapshot
{
    public required IReadOnlyList<ProblemDevice> Devices { get; init; }

    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    // Total devices Windows enumerated, problem or not. Gives the operator a sanity check that the scan
    // actually saw the machine rather than silently returning nothing.
    public int TotalDevicesScanned { get; init; }

    public string? ErrorMessage { get; init; }

    public bool HasProblems => Devices.Count > 0;

    public static DeviceProblemSnapshot Failed(string message) => new()
    {
        Devices = Array.Empty<ProblemDevice>(),
        ErrorMessage = message
    };
}
