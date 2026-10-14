using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinTester.Core.Fans;

namespace WinTester.App.ViewModels;

// One tier button in the Fan tool window's vendor-tier row. The row only exists when the active backend
// implements IFanTierControl, so this view model is never populated on the generic LibreHardwareMonitor
// or ASUS-desktop paths.
//
// Like FanRowViewModel, holds display state across polls rather than being rebuilt each tick, so the
// active-tier highlight updates in place instead of the button row flickering.
public sealed class FanTierViewModel : INotifyPropertyChanged
{
    // Backend-defined tier Id, passed straight back to IFanTierControl.ApplyTier. Not shown.
    public string Id { get; }

    public string Name { get; }

    // Shown as the button's tooltip: what the tier physically does.
    public string Description { get; }

    // An implicit tier is a state the hardware can land in from a slider position but can't be selected
    // directly, so it renders as a disabled state indicator rather than a live button.
    public bool IsImplicit { get; }
    public bool IsSelectable => !IsImplicit;

    public FanTierViewModel(FanTier tier)
    {
        Id = tier.Id;
        Name = tier.Name;
        Description = tier.Description;
        IsImplicit = tier.IsImplicit;
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
