using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace WinTester.App.ViewModels;

// Backs one tile in the USB Validator grid. Mutable + INotifyPropertyChanged, same reasoning as
// SensorTreeNodeViewModel: the grid is built once per run (or per rescan) and then has its state updated
// in place on every poll tick, rather than being rebuilt wholesale rebuilding would fight any hover/
// selection state and is unnecessary churn for something that ticks twice a second.
public sealed class UsbPortTileViewModel : INotifyPropertyChanged
{
    private static readonly Brush EmptyFill = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40));
    private static readonly Brush ConnectedFill = new SolidColorBrush(Color.FromRgb(0x35, 0xB8, 0x5C));
    private static readonly Brush PassedFill = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40));

    public string PortKey { get; }
    public int PortNumber { get; }
    public string HubPath { get; }

    public string HeaderText => $"Port {PortNumber}";

    private string _hubLabel;
    private string? _deviceLabel;
    private bool _passed;
    private bool _connected;

    public UsbPortTileViewModel(string portKey, int portNumber, string hubPath, string hubLabel)
    {
        PortKey = portKey;
        PortNumber = portNumber;
        HubPath = hubPath;
        _hubLabel = hubLabel;
    }

    // Secondary line: the hub identity while idle/never-connected, the last-seen VID/PID once a device has
    // been on this port, kept showing that VID/PID through the Passed state too (matches the ported
    // prototype: SetDetail is only called while a device is actually connected, so the text simply
    // outlives the device being unplugged).
    public string SubText => _deviceLabel ?? _hubLabel;

    public Brush Fill => _connected ? ConnectedFill : (_passed ? PassedFill : EmptyFill);

    public Visibility PassVisibility => _passed && !_connected ? Visibility.Visible : Visibility.Collapsed;

    public void SetConnected(ushort vendorId, ushort productId)
    {
        _connected = true;
        _deviceLabel = $"VID_{vendorId:X4} PID_{productId:X4}";
        RaiseAll();
    }

    public void SetPassed()
    {
        _connected = false;
        _passed = true;
        RaiseAll();
    }

    public void SetEmpty()
    {
        _connected = false;
        _passed = false;
        RaiseAll();
    }

    // Restores the base "waiting" look for a fresh run — drops any remembered VID/PID from a prior run on
    // this same port, same as the ported prototype's ResetDisplay.
    public void ResetForNewRun()
    {
        _connected = false;
        _passed = false;
        _deviceLabel = null;
        RaiseAll();
    }

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fill)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PassVisibility)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
