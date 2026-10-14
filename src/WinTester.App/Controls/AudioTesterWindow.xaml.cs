using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinTester.App.Audio;

namespace WinTester.App.Controls;

// Standalone Tab 2 (HID) tool window. Plays vendor\Sounds\Title.mp3 through NAudio (AudioFileReader +
// WaveOutEvent) rather than WPF's own MediaPlayer, specifically because the visualizer needs sample-level
// access to what's playing — MediaPlayer never exposes that. VisualizingSampleProvider taps the samples
// as NAudio's playback thread reads them; this window's own render timer (UI thread) snapshots that tap
// ~30 times a second and draws either an FFT bar spectrum or a raw waveform trace, loosely inspired by
// https://github.com/SilenZcience/CAudioVisualizer — that project is a whole separate OpenGL/ImGui
// application (NAudio + OpenTK + ImGui.NET + MathNet.Numerics) meant to run standalone over the desktop,
// not something embeddable as-is into a WPF popup, so this is a from-scratch WPF-native equivalent
// (Canvas + Rectangles/Polyline) rather than a literal port. No brand/report: nothing here is ever exported.
//
// Playback runs through WasapiOut rather than WaveOutEvent so the channel-sweep toggle can address the
// default device's real channel layout directly: ChannelSweepSampleProvider (WinTester.App/Audio) sits
// after VisualizingSampleProvider, downmixes to mono, and re-expands across the device's own channel
// count, panning around the ring SpeakerLayout builds for that count.
public partial class AudioTesterWindow : Window
{
    private const int FftSize = 1024; // must stay a power of two (SimpleFft.Transform requires it)
    private const int RingSize = 4096;
    private const int BarCount = 48;

    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _visualizerTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _pillIdleTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private readonly float[] _sampleBuffer = new float[FftSize];
    private readonly float[] _fftReal = new float[FftSize];
    private readonly float[] _fftImag = new float[FftSize];
    private readonly float[] _barHeights = new float[BarCount];
    private readonly Rectangle[] _barRects = new Rectangle[BarCount];
    private Polyline? _waveformLine;

    private AudioFileReader? _reader;
    private VisualizingSampleProvider? _visualizingProvider;
    private ChannelSweepSampleProvider? _sweepProvider;
    private WasapiOut? _waveOut;

    // Which pre-made diagram set (§ SpeakerLayout.GetDiagramKey) the detected device's channel count maps
    // to — "2.0"/"4.0"/"5.1"/"7.1", or "UNIVERSAL" for anything else. Set once in LoadAudio.
    private string _speakerLayoutKey = "UNIVERSAL";

    // File-name stem (without ".png") of whatever's currently loaded into SpeakerOverlayImage, so the
    // ~33ms visualizer tick doesn't re-decode the same bitmap every frame — most ticks land mid-dwell,
    // where nothing about the overlay has actually changed.
    private string? _loadedSpeakerImage;

    private Rect _windowedBounds;
    private bool _isDraggingSeek;
    private bool _isPlaying;
    private bool _fileAvailable;
    private bool _isFullscreen;
    private bool _visualizerIsBars = true;

    private AudioTesterWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;

        BuildVisualizerVisuals();

        // Subscribed here rather than in XAML: the same NullReferenceException-on-startup hazard as the
        // earlier player-controls window (VolumeSlider's XAML-declared Value="0.7" would fire ValueChanged
        // during InitializeComponent itself, before _reader exists). Subscribing after InitializeComponent
        // sidesteps it entirely.
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;

        _visualizerTimer.Tick += (_, _) => RenderVisualizerFrame();
        _positionTimer.Tick += (_, _) => UpdatePositionDisplay();
        _pillIdleTimer.Tick += (_, _) =>
        {
            _pillIdleTimer.Stop();
            FadeOutPill();
        };

        Loaded += Window_Loaded;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Captured post-Loaded, not in the constructor: WindowStartupLocation="CenterOwner" hasn't
        // positioned the window yet at construction time, so Left/Top would still read as unset.
        _windowedBounds = new Rect(Left, Top, Width, Height);

        LoadAudio();
        _visualizerTimer.Start();
        ResetPillIdleTimer();
    }

    // ===================== Audio loading =====================

    private void LoadAudio()
    {
        var soundPath = System.IO.Path.Combine(AppContext.BaseDirectory, "vendor", "Sounds", "Title.mp3");
        if (!File.Exists(soundPath))
        {
            ShowMissingFile("Audio file not found: vendor\\Sounds\\Title.mp3");
            return;
        }

        try
        {
            _reader = new AudioFileReader(soundPath) { Volume = (float)VolumeSlider.Value };
            _visualizingProvider = new VisualizingSampleProvider(_reader, RingSize);

            var deviceChannels = DetectDefaultDeviceChannelCount();
            var sweepOrder = SpeakerLayout.BuildSweepOrder(deviceChannels);
            _sweepProvider = new ChannelSweepSampleProvider(_visualizingProvider, deviceChannels, sweepOrder);

            // Sweep defaults to on (rather than requiring it to be toggled on every time) whenever there's
            // actually more than one speaker to sweep through; a mono/1-channel device has
            // nothing to sweep, so leave it off there and let the disabled button below reflect that.
            _sweepProvider.SweepEnabled = sweepOrder.Length > 1;
            SweepToggleButton.Foreground = new SolidColorBrush(_sweepProvider.SweepEnabled
                ? Color.FromRgb(0x3D, 0xC9, 0x6E)
                : Color.FromRgb(0x1B, 0x1B, 0x1E));

            // WasapiOut, not WaveOutEvent: the sweep needs to address each physical channel of the
            // device's actual layout, and matching ChannelSweepSampleProvider's output channel count
            // exactly to the device's own means the shared-mode audio engine maps our channels straight
            // through 1:1 rather than running its own up/downmix.
            _waveOut = new WasapiOut();
            _waveOut.Init(new SampleToWaveProvider(_sweepProvider));

            SweepToggleButton.IsEnabled = sweepOrder.Length > 1;
            SweepToggleButton.ToolTip = sweepOrder.Length > 1
                ? "Sweep the audio through each speaker in turn"
                : "Mono output device — nothing to sweep";

            _speakerLayoutKey = SpeakerLayout.GetDiagramKey(deviceChannels);

            _fileAvailable = true;
            DurationText.Text = FormatTime(_reader.TotalTime);
            SeekSlider.Maximum = Math.Max(0.01, _reader.TotalTime.TotalSeconds);
            _positionTimer.Start();
            UpdateSpeakerOverlay();
        }
        catch
        {
            // Best-effort, same posture as every other optional-file-open call in this codebase: bad
            // codec/locked file disables the controls rather than leaving them live against nothing.
            ShowMissingFile("Audio file could not be opened.");
        }
    }

    // Best-effort probe of the default render endpoint's real channel count via WASAPI. Falls back to
    // plain stereo if there's no default device or the query throws — an inability to detect a surround
    // layout should degrade to ordinary stereo playback, not block the whole tool. Queried against
    // Role.Console specifically because that's the role WasapiOut's own default constructor resolves
    // against, so the count here always matches the endpoint playback actually uses.
    private static int DetectDefaultDeviceChannelCount()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return Math.Max(1, device.AudioClient.MixFormat.Channels);
        }
        catch
        {
            return 2;
        }
    }

    private void ShowMissingFile(string message)
    {
        MissingFileText.Text = message;
        MissingFileText.Visibility = Visibility.Visible;
        PlayPauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        SeekSlider.IsEnabled = false;
        _fileAvailable = false;
    }

    // ===================== Play / Pause / Stop =====================

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_fileAvailable || _waveOut is null) return;

        if (_isPlaying)
        {
            _waveOut.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "\u25B6"; // ▶
        }
        else
        {
            _waveOut.Play();
            _isPlaying = true;
            PlayPauseButton.Content = "\u275A\u275A"; // ❚❚
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_fileAvailable || _waveOut is null || _reader is null) return;

        _waveOut.Stop();
        _reader.Position = 0;
        _sweepProvider?.ResetPhase();
        ResetTransportUi();
    }

    // ===================== Channel sweep =====================

    private void SweepToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sweepProvider is null) return;

        _sweepProvider.SweepEnabled = !_sweepProvider.SweepEnabled;
        SweepToggleButton.Foreground = new SolidColorBrush(_sweepProvider.SweepEnabled
            ? Color.FromRgb(0x3D, 0xC9, 0x6E)
            : Color.FromRgb(0x1B, 0x1B, 0x1E));

        if (!_sweepProvider.SweepEnabled) SweepStatusText.Text = string.Empty;
    }

    private void ResetTransportUi()
    {
        _isPlaying = false;
        PlayPauseButton.Content = "\u25B6";
        SeekSlider.Value = 0;
        ElapsedText.Text = FormatTime(TimeSpan.Zero);
    }

    // ===================== Seek =====================

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isDraggingSeek = true;

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSeek = false;
        if (_fileAvailable && _reader is not null)
            _reader.CurrentTime = TimeSpan.FromSeconds(SeekSlider.Value);
    }

    // ===================== Volume =====================

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_reader is not null) _reader.Volume = (float)e.NewValue;
    }

    // ===================== Position display / natural end detection =====================

    private void UpdatePositionDisplay()
    {
        if (_reader is null) return;

        // NAudio's playback naturally stops on its own device thread at end-of-file; polling CurrentTime
        // here (UI thread, same timer that already drives the seek bar) catches that and resets the
        // transport instead of leaving Play/Pause stuck on "playing" against a silent, finished clip.
        if (_isPlaying && _reader.CurrentTime >= _reader.TotalTime)
        {
            _waveOut?.Stop();
            _reader.Position = 0;
            ResetTransportUi();
            return;
        }

        if (_isDraggingSeek) return;
        SeekSlider.Value = _reader.CurrentTime.TotalSeconds;
        ElapsedText.Text = FormatTime(_reader.CurrentTime);
    }

    private static string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    // ===================== Fullscreen =====================

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        _windowedBounds = new Rect(Left, Top, Width, Height);
        TitleBar.Visibility = Visibility.Collapsed;

        // Same manual-bounds approach as MouseTouchTesterWindow, rather than WindowState="Maximized" —
        // avoids Maximized's occasional WindowStyle="None" content-clipped-past-the-work-area quirk.
        // Always targets the primary screen, same simplification that window already makes.
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        _isFullscreen = true;
        FadeInPill();
        ResetPillIdleTimer();
    }

    private void ExitFullscreen()
    {
        TitleBar.Visibility = Visibility.Visible;
        Left = _windowedBounds.Left;
        Top = _windowedBounds.Top;
        Width = _windowedBounds.Width;
        Height = _windowedBounds.Height;

        _isFullscreen = false;
        FadeInPill();
        ResetPillIdleTimer();
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    // Only the visualizer surface triggers this, not the pill sitting on top of it — double-clicking a
    // control (e.g. the fullscreen button itself) never accidentally double-toggles fullscreen. Canvas is
    // a Panel, not a Control, so it has no MouseDoubleClick event to hook — ClickCount on the plain
    // left-button-down event is the substitute.
    private void VisualizerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleFullscreen();
    }

    // ===================== Controls pill fade =====================

    private void ContentGrid_MouseMove(object sender, MouseEventArgs e)
    {
        FadeInPill();
        ResetPillIdleTimer();
    }

    private void ResetPillIdleTimer()
    {
        _pillIdleTimer.Stop();
        _pillIdleTimer.Start();
    }

    private void FadeInPill() =>
        ControlsPill.BeginAnimation(OpacityProperty, new DoubleAnimation(ControlsPill.Opacity, 1, TimeSpan.FromMilliseconds(150)));

    private void FadeOutPill() =>
        ControlsPill.BeginAnimation(OpacityProperty, new DoubleAnimation(ControlsPill.Opacity, 0, TimeSpan.FromMilliseconds(400)));

    // ===================== Visualizer =====================

    private void BuildVisualizerVisuals()
    {
        var barBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xC9, 0x6E));
        barBrush.Freeze();

        for (var i = 0; i < BarCount; i++)
        {
            var rect = new Rectangle { Fill = barBrush, RadiusX = 2, RadiusY = 2 };
            VisualizerCanvas.Children.Add(rect);
            _barRects[i] = rect;
        }

        _waveformLine = new Polyline
        {
            Stroke = barBrush,
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed
        };
        VisualizerCanvas.Children.Add(_waveformLine);
    }

    // Space, "if available" — only when a visualizer actually has audio behind it. Called from
    // Window_PreviewKeyDown.
    private void SwitchVisualizerType()
    {
        _visualizerIsBars = !_visualizerIsBars;
        foreach (var rect in _barRects)
            rect.Visibility = _visualizerIsBars ? Visibility.Visible : Visibility.Collapsed;
        _waveformLine!.Visibility = _visualizerIsBars ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderVisualizerFrame()
    {
        if (_visualizingProvider is null) return;

        var w = VisualizerCanvas.ActualWidth;
        var h = VisualizerCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        _visualizingProvider.CopyLatest(_sampleBuffer);

        if (_visualizerIsBars) RenderBars(w, h);
        else RenderWaveform(w, h);

        if (_sweepProvider is { SweepEnabled: true })
            SweepStatusText.Text = _sweepProvider.CurrentLabel;

        UpdateSpeakerOverlay();
    }

    // ===================== Speaker overlay =====================

    // Base "{key}.png" (no channel highlighted) whenever nothing is actively sweeping — whether that's
    // because playback is stopped/paused or because sweep is turned off. Only shown
    // while sweeping does the overlay swap to "{key}_{Label}.png" (or, for UNIVERSAL, overlay the label as
    // text) to track ChannelSweepSampleProvider.CurrentImageLabel. Not an explicit spec for the
    // playing-but-not-sweeping case — treated the same as idle since there's no single "current channel"
    // to point at when every channel is getting identical audio.
    private void UpdateSpeakerOverlay()
    {
        if (!_fileAvailable) return;

        var sweepingNow = _sweepProvider is { SweepEnabled: true } && _isPlaying;
        var label = sweepingNow ? _sweepProvider!.CurrentImageLabel : null;

        if (_speakerLayoutKey == "UNIVERSAL")
        {
            LoadSpeakerImage("UNIVERSAL");
            SpeakerOverlayFallbackText.Text = label ?? string.Empty;
            SpeakerOverlayFallbackText.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            LoadSpeakerImage(label is null ? _speakerLayoutKey : $"{_speakerLayoutKey}_{label}");
            SpeakerOverlayFallbackText.Visibility = Visibility.Collapsed;
        }
    }

    // Loads vendor\images\Speaker\{fileBase}.png into the overlay. Skips the decode entirely when it's
    // already the image on screen — this runs off the ~33ms visualizer tick, and most ticks land mid-dwell
    // with nothing to change. A missing/corrupt file hides the whole overlay rather than showing a broken
    // image — same best-effort posture LoadAudio already takes with the sound file itself.
    private void LoadSpeakerImage(string fileBase)
    {
        if (fileBase == _loadedSpeakerImage)
        {
            SpeakerOverlayBorder.Visibility = Visibility.Visible;
            return;
        }

        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "vendor", "images", "Speaker", $"{fileBase}.png");
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            SpeakerOverlayImage.Source = bitmap;
            SpeakerOverlayBorder.Visibility = Visibility.Visible;
            _loadedSpeakerImage = fileBase;
        }
        catch
        {
            SpeakerOverlayBorder.Visibility = Visibility.Collapsed;
            _loadedSpeakerImage = null;
        }
    }

    private void RenderBars(double w, double h)
    {
        // Hann window keeps the FFT from smearing energy across bins at the edges of the sample block.
        for (var i = 0; i < FftSize; i++)
        {
            var windowCoeff = 0.5f * (1f - MathF.Cos(2 * MathF.PI * i / (FftSize - 1)));
            _fftReal[i] = _sampleBuffer[i] * windowCoeff;
            _fftImag[i] = 0;
        }
        SimpleFft.Transform(_fftReal, _fftImag);

        var usableBins = FftSize / 2;
        var barWidth = w / BarCount;

        for (var b = 0; b < BarCount; b++)
        {
            // Log-spaced bucket boundaries so bass detail isn't squashed into the first couple of bars —
            // a linear split would dump most of a typical track's energy into bar 0.
            var loBin = Math.Max(1, (int)(Math.Pow(b / (double)BarCount, 2) * usableBins));
            var hiBin = Math.Max(loBin + 1, (int)(Math.Pow((b + 1) / (double)BarCount, 2) * usableBins));

            float peak = 0;
            for (var bin = loBin; bin < hiBin && bin < usableBins; bin++)
            {
                var mag = MathF.Sqrt(_fftReal[bin] * _fftReal[bin] + _fftImag[bin] * _fftImag[bin]);
                if (mag > peak) peak = mag;
            }

            // Not calibrated to any absolute dB scale — just enough headroom that a normal-volume track
            // reaches a reasonable fraction of the canvas height without constantly clipping to full.
            var target = (float)Math.Min(1.0, peak / (FftSize / 8.0)) * (float)h;

            // Fast attack, slow decay, so bars visibly "fall" between frames instead of snapping.
            _barHeights[b] = target > _barHeights[b] ? target : _barHeights[b] * 0.85f;

            var rect = _barRects[b];
            rect.Width = Math.Max(1, barWidth - 2);
            rect.Height = Math.Max(1, _barHeights[b]);
            Canvas.SetLeft(rect, b * barWidth);
            Canvas.SetTop(rect, h - rect.Height);
        }
    }

    private void RenderWaveform(double w, double h)
    {
        var points = new PointCollection();
        var step = Math.Max(1, FftSize / 256);
        for (var i = 0; i < FftSize; i += step)
        {
            var x = w * i / FftSize;
            // *3 headroom so a quiet clip's trace is still visibly more than a flat line.
            var y = Math.Clamp(h / 2 - _sampleBuffer[i] * (h / 2) * 3, 0, h);
            points.Add(new Point(x, y));
        }
        _waveformLine!.Points = points;
    }

    // ===================== Window chrome =====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_isFullscreen) ExitFullscreen();
                else Close();
                break;

            case Key.Space when _fileAvailable:
                e.Handled = true;
                SwitchVisualizerType();
                break;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _positionTimer.Stop();
        _visualizerTimer.Stop();
        _pillIdleTimer.Stop();

        _waveOut?.Stop();
        _waveOut?.Dispose();
        _reader?.Dispose();
    }

    public static void Show(Window owner)
    {
        var window = new AudioTesterWindow(owner);
        window.ShowDialog();
    }
}
