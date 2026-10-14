using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinTester.App.ViewModels;

// One row in the Fan tool window. Backend-agnostic: the window owns polling and control calls, this just
// holds display/binding state for one fan across ticks so the slider and RPM text update in place
// instead of the row (and any in-progress drag) being rebuilt on every poll.
public sealed class FanRowViewModel : INotifyPropertyChanged
{
    // Backend-defined Id (FanControlInfo.Id), not shown to the user.
    public string Id { get; }

    public string Name { get; }

    public bool IsControllable { get; }

    public FanRowViewModel(string id, string name, bool isControllable)
    {
        Id = id;
        Name = name;
        IsControllable = isControllable;
    }

    private string _rpmText = "-- RPM";
    public string RpmText
    {
        get => _rpmText;
        set { if (_rpmText == value) return; _rpmText = value; OnPropertyChanged(); }
    }

    // Backing value for the row's slider. Set from a poll (ApplyPoll) and from the user dragging the
    // slider (two-way bound); the window distinguishes the two via IsPollUpdateInProgress rather than by
    // comparing values, so a user drag landing on the same percent the backend just reported isn't
    // silently dropped.
    private double _dutyPercent;
    public double DutyPercent
    {
        get => _dutyPercent;
        set { if (Math.Abs(_dutyPercent - value) < 0.01) return; _dutyPercent = value; OnPropertyChanged(); }
    }

    // True only while a poll-read value is being applied to DutyPercent; checked before treating a
    // DutyPercent change as a user-initiated slider drag.
    public bool IsPollUpdateInProgress { get; set; }

    public void ApplyPoll(double? rpm, double? dutyPercent)
    {
        RpmText = rpm is { } r ? $"{r:0} RPM" : "-- RPM";

        if (!IsControllable || dutyPercent is not { } d) return;

        IsPollUpdateInProgress = true;
        DutyPercent = d;
        IsPollUpdateInProgress = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
