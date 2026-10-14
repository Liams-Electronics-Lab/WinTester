namespace WinTester.Core.Fans;

// One physical fan as reported by a fan-control backend. Id is a stable, backend-defined string used to
// address this fan in SetDuty/Reset calls; never shown to the user and not guaranteed meaningful outside
// the backend that produced it. Rpm is null when the backend can't read a tachometer for this fan;
// DutyPercent is null when no writable control is associated with it. IsControllable gates whether
// SetDuty/Reset are meaningful for this Id at all.
public sealed record FanControlInfo(
    string Id,
    string Name,
    double? Rpm,
    double? DutyPercent,
    bool IsControllable);
