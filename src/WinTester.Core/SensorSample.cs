using System;
using System.Collections.Generic;

namespace WinTester.Core;

// One timestamped snapshot of free-form hardware telemetry.
public sealed class SensorSample
{
    public DateTime Timestamp { get; init; }
    public Dictionary<string, double> Values { get; init; } = new();
}
