using System;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AForge.Video;
using AForge.Video.DirectShow;

namespace WinTester.Tools.WebcamMic;

// Wraps one AForge VideoCaptureDevice and republishes each frame as a frozen WPF BitmapSource on the
// caller's dispatcher, so WebcamMicTesterWindow's code-behind only ever has to set Image.Source — no
// System.Drawing.Bitmap or DirectShow type crosses into WinTester.App. NewFrame fires on AForge's own
// capture thread; FrameReady is always re-marshalled onto _dispatcher before it's raised.
public sealed class CameraCaptureSession : IDisposable
{
    // Preview box is 480px wide (WebcamMicTesterWindow.xaml); picking the capability closest to 640-wide
    // rather than the device's native default keeps a modest, consistent capture load regardless of
    // whether the camera's default happens to be VGA or 4K.
    private const int PreferredWidth = 640;

    private readonly Dispatcher _dispatcher;
    private VideoCaptureDevice? _device;
    private volatile bool _disposed;

    public event Action<BitmapSource>? FrameReady;

    public CameraCaptureSession(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Start(string monikerString)
    {
        Stop();

        var device = new VideoCaptureDevice(monikerString);
        if (device.VideoCapabilities is { Length: > 0 } caps)
        {
            var best = caps[0];
            foreach (var cap in caps)
            {
                if (Math.Abs(cap.FrameSize.Width - PreferredWidth) < Math.Abs(best.FrameSize.Width - PreferredWidth))
                    best = cap;
            }
            device.VideoResolution = best;
        }

        device.NewFrame += Device_NewFrame;
        _device = device;
        device.Start();
    }

    public void Stop()
    {
        if (_device is null) return;

        var device = _device;
        _device = null;
        device.NewFrame -= Device_NewFrame;
        if (device.IsRunning)
        {
            device.SignalToStop();
            device.WaitForStop();
        }
    }

    private void Device_NewFrame(object sender, NewFrameEventArgs eventArgs)
    {
        if (_disposed) return;

        // Clone before crossing threads — eventArgs.Frame is reused/disposed by AForge as soon as this
        // handler returns.
        using var frame = (Bitmap)eventArgs.Frame.Clone();
        var bitmapSource = ToBitmapSource(frame);

        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) FrameReady?.Invoke(bitmapSource);
        });
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
