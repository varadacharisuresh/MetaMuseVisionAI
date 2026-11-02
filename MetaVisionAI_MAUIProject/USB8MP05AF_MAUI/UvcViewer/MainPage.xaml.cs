using System.Collections.ObjectModel;
using Microsoft.Maui.Dispatching;
using UvcViewer.Platforms.Android;

namespace UvcViewer;

public partial class MainPage : ContentPage
{
    // Barcode processing can add strings here later; currently the list is empty.
    public ObservableCollection<string> ScannedItems { get; } = new();

    private enum CameraAction { Start, Restart, Stop, Recover }

    // Layout-only state: resizing the preview never closes or restarts UVC.
    private bool _previewExpanded;

    // Never overlap native UVC open/close/start calls on the same camera.
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    private CancellationTokenSource? _startCancellation;
    private UvcCameraSession? _observedSession;
    private IDispatcherTimer? _usbMonitor;
    private bool _visible;
    private bool _foreground = true;
    // An old session may still hold a native stream after the page was backgrounded.
    // Force a serialized close/open when returning to the foreground.
    private bool _reopenAfterBackground;
    private int _cameraRequestVersion;
    private bool _hadUsbDevice;
    private int _missingUsbPolls;
    private bool _permissionRefused;
    private bool _populatingPickers;
    private DateTime _retryAfterUtc = DateTime.MinValue;
    // Preview health is separate from UVC start success. A native stream can
    // negotiate successfully but stop producing TextureView updates.
    private long _lastPreviewUpdateCount = -1;
    private DateTime _lastPreviewProgressUtc = DateTime.MinValue;
    private int _frameStallRecoveries;
    private const int MaxFrameStallRecoveries = 3;
    private static readonly TimeSpan FrameStallTimeout = TimeSpan.FromSeconds(8);

    private int _wantedWidth;
    private int _wantedHeight;
    private int _wantedFps;
    private string _wantedFormat;
    private List<UvcVideoMode> _supported = new();
    private List<(int Width, int Height)> _resolutions = new();
    private List<int> _fpsValues = new();

    public MainPage()
    {
        InitializeComponent();
        ItemsList.ItemsSource = ScannedItems;
        InitializeToolModes();

        // Read new settings, but preserve the previous project's saved resolution
        // and FPS when these new keys are not present yet.
        (int Width, int Height)[] legacy =
        {
            (640, 480), (1280, 720), (1920, 1080),
            (2592, 1944), (3264, 2448)
        };
        int oldIndex = Math.Clamp(Preferences.Default.Get("uvc_resolution", 0), 0, legacy.Length - 1);
        _wantedWidth = Preferences.Default.Get("uvc_width", legacy[oldIndex].Width);
        _wantedHeight = Preferences.Default.Get("uvc_height", legacy[oldIndex].Height);
        _wantedFps = Preferences.Default.Get("uvc_frame_rate",
            Preferences.Default.Get("uvc_fps", 1) == 0 ? 15 : 30);
        _wantedFormat = "MJPEG"; // MJPEG is the only format exposed by this UI.

        Preview.HandlerChanged += (_, _) =>
        {
            SubscribeSession();
            if (_visible && _foreground)
                EnsureCameraRunning();
        };
    }

    private UvcCameraSession? CurrentSession =>
        (Preview.Handler as UvcPreviewHandler)?.Session;

    private void SubscribeSession()
    {
        if (_observedSession != null)
            _observedSession.StatusChanged -= OnStatusChanged;

        _observedSession = CurrentSession;
        if (_observedSession != null)
            _observedSession.StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(string message) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_visible && _foreground)
                Status.Text = message;
        });

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        if (_foreground)
        {
            DeviceDisplay.Current.KeepScreenOn = true;
            StartUsbMonitor();
            EnsureCameraRunning();
        }
    }

    protected override void OnDisappearing()
    {
        _reopenAfterBackground = true;
        ResetPreviewWatch();
        _visible = false;
        StopAutoScan();
        StopUsbMonitor();
        DeviceDisplay.Current.KeepScreenOn = false;
        CancelPendingStart();
        _ = ExecuteCameraAsync(CameraAction.Stop);
        base.OnDisappearing();
    }

    public void AppStopped()
    {
        _reopenAfterBackground = true;
        ResetPreviewWatch();
        _foreground = false;
        StopAutoScan();
        StopUsbMonitor();
        DeviceDisplay.Current.KeepScreenOn = false;
        CancelPendingStart();
        _ = ExecuteCameraAsync(CameraAction.Stop);
    }

    public void AppResumed()
    {
        _foreground = true;
        _permissionRefused = false;
        _frameStallRecoveries = 0;
        ResetPreviewWatch();
        if (_visible)
        {
            DeviceDisplay.Current.KeepScreenOn = true;
            StartUsbMonitor();
            EnsureCameraRunning();
        }
    }

    private void EnsureCameraRunning()
    {
        if (!_visible || !_foreground) return;
        var session = CurrentSession;
        if (session is null) return;  // HandlerChanged will call us when ready.

        if (_reopenAfterBackground)
        {
            _reopenAfterBackground = false;
            _retryAfterUtc = DateTime.MinValue;
            _missingUsbPolls = 0;
            _permissionRefused = false;
            _frameStallRecoveries = 0;
            ResetPreviewWatch();
            if (session.HasUvcDevice())
            {
                Status.Text = "Reinitializing USB camera…";
                global::Android.Util.Log.Info("UvcTrace",
                    "App foregrounded: serializing camera close/reopen.");
                _ = ExecuteCameraAsync(CameraAction.Restart);
                return;
            }
        }

        ProbeUsbCamera();
    }

    private void StartUsbMonitor()
    {
        if (_usbMonitor is null)
        {
            _usbMonitor = Dispatcher.CreateTimer();
            _usbMonitor.Interval = TimeSpan.FromSeconds(2);
            _usbMonitor.Tick += (_, _) => ProbeUsbCamera();
        }
        if (!_usbMonitor.IsRunning)
            _usbMonitor.Start();
    }

    private void StopUsbMonitor() => _usbMonitor?.Stop();

    // Checking Android USB host's device list avoids reliance on attach broadcasts,
    // which can be missed when the device is connected before the app starts.
    private void ProbeUsbCamera()
    {
        if (!_visible || !_foreground) return;

        var session = CurrentSession;
        if (session == null) return;

        bool present;
        try { present = session.HasUvcDevice(); }
        catch (Exception ex)
        {
            Status.Text = "USB detection error: " + ex.Message;
            return;
        }

        if (!present)
        {
            // A UVC mode change can briefly interrupt USB enumeration.
            // Do not stop a valid/restarting stream on a single missed poll.
            if (++_missingUsbPolls < 3)
                return;

            if (_missingUsbPolls == 3)
                global::Android.Util.Log.Warn("UvcTrace",
                    "USB camera absent on 3 consecutive checks; treating as disconnected.");

            bool needsStop = _hadUsbDevice || session.IsStreaming;
            _hadUsbDevice = false;
            _permissionRefused = false;
            _retryAfterUtc = DateTime.MinValue;
            _frameStallRecoveries = 0;
            ResetPreviewWatch();
            if (needsStop)
            {
                CancelPendingStart();
                _ = ExecuteCameraAsync(CameraAction.Stop);
            }
            Status.Text = "Waiting for USB camera…";
            if (SettingsOverlay.IsVisible)
                ShowSupportedModes();
            return;
        }

        _missingUsbPolls = 0;

        if (!_hadUsbDevice)
        {
            _hadUsbDevice = true;
            _permissionRefused = false;
            _retryAfterUtc = DateTime.MinValue;
        }

        if (session.IsStreaming)
        {
            CheckPreviewProgress(session);
            return;
        }

        if (!_permissionRefused &&
            _cameraGate.CurrentCount > 0 && DateTime.UtcNow >= _retryAfterUtc)
        {
            // A failure will be retried after a short cooldown; permission denial
            // requires replugging the camera or returning to the app.
            _retryAfterUtc = DateTime.UtcNow.AddSeconds(8);
            _ = ExecuteCameraAsync(CameraAction.Start);
        }
    }

    private void ResetPreviewWatch()
    {
        _lastPreviewUpdateCount = -1;
        _lastPreviewProgressUtc = DateTime.MinValue;
    }

    private void CheckPreviewProgress(UvcCameraSession session)
    {
        // Start/stop may expose a non-null JNI instance before start() returns.
        if (_cameraGate.CurrentCount == 0) return;

        long count = session.PreviewUpdateCount;
        DateTime now = DateTime.UtcNow;
        if (_lastPreviewUpdateCount < 0 || count < _lastPreviewUpdateCount)
        {
            _lastPreviewUpdateCount = count;
            _lastPreviewProgressUtc = now;
            return;
        }

        if (count > _lastPreviewUpdateCount)
        {
            _lastPreviewUpdateCount = count;
            _lastPreviewProgressUtc = now;
            // Only a series of callbacks, not one stale redraw, proves progress.
            if (count >= 10)
                _frameStallRecoveries = 0;
            return;
        }

        if (now - _lastPreviewProgressUtc < FrameStallTimeout)
            return;

        if (_frameStallRecoveries >= MaxFrameStallRecoveries)
        {
            Status.Text = "USB camera is connected, but video frames are not arriving. Reconnect the camera.";
            global::Android.Util.Log.Error("UvcTrace",
                $"Preview stalled after {MaxFrameStallRecoveries} recoveries; updates={count}.");
            // Don't spam the status and logs every USB monitor tick.
            _lastPreviewProgressUtc = now.AddMinutes(10);
            return;
        }

        _frameStallRecoveries++;
        Status.Text = $"Video preview stalled. Reinitializing ({_frameStallRecoveries}/{MaxFrameStallRecoveries})…";
        global::Android.Util.Log.Warn("UvcTrace",
            $"Preview stalled; updates={count}, recovery={_frameStallRecoveries}/{MaxFrameStallRecoveries}.");
        ResetPreviewWatch();
        _ = ExecuteCameraAsync(CameraAction.Recover);
    }

    private void CancelPendingStart()
    {
        try { _startCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task ExecuteCameraAsync(CameraAction action)
    {
        // Ignore older queued Start/Stop operations after a newer request is issued.
        // This prevents a late background Stop from closing a newly reopened camera.
        int version = Interlocked.Increment(ref _cameraRequestVersion);
        if (action != CameraAction.Start)
            CancelPendingStart();

        await _cameraGate.WaitAsync();
        CancellationTokenSource? pending = null;
        try
        {
            if (version != Volatile.Read(ref _cameraRequestVersion)) return;
            var session = CurrentSession ?? _observedSession;
            if (session is null) return;

            global::Android.Util.Log.Info("UvcTrace",
                $"Camera operation {action} (request {version}) started.");
            if (action != CameraAction.Start)
                await session.StopAsync();
            if (action == CameraAction.Recover)
                await Task.Delay(400); // Give the USB camera time to settle after stop/close.

            if (version != Volatile.Read(ref _cameraRequestVersion)) return;
            if (action == CameraAction.Stop || !_foreground || !_visible || !session.HasUvcDevice())
                return;

            session = CurrentSession;
            if (session is null || session.IsStreaming) return;

            pending = new CancellationTokenSource();
            _startCancellation = pending;

            var permission = await Permissions.RequestAsync<Permissions.Camera>();
            pending.Token.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref _cameraRequestVersion)) return;
            if (permission != PermissionStatus.Granted)
            {
                _permissionRefused = true;
                Status.Text = "Camera permission denied. Enable it in Android Settings.";
                return;
            }

            try
            {
                // Only run this primer after a verified frame stall. The log
                // showed 800x600 restoring delivery, followed by successful
                // higher-resolution streams. Keep the user's saved mode intact.
                if (action == CameraAction.Recover &&
                    (_wantedWidth != 800 || _wantedHeight != 600 ||
                     _wantedFps != 30 || _wantedFormat != "MJPEG"))
                {
                    try
                    {
                        Status.Text = "Reinitializing camera at 800 × 600…";
                        await session.StartAsync(800, 600, 30, pending.Token, "MJPEG");
                        DateTime primingDeadline = DateTime.UtcNow.AddSeconds(4);
                        while (session.PreviewUpdateCount < 3 &&
                               DateTime.UtcNow < primingDeadline)
                            await Task.Delay(200, pending.Token);
                        global::Android.Util.Log.Info("UvcTrace",
                            $"Recovery primer preview updates: {session.PreviewUpdateCount}.");
                    }
                    catch (Exception ex) when (ex is not System.OperationCanceledException)
                    {
                        global::Android.Util.Log.Warn("UvcTrace",
                            "800x600 recovery primer failed: " + ex.Message);
                    }
                    finally
                    {
                        await session.StopAsync();
                    }
                    await Task.Delay(400, pending.Token);
                }

                global::Android.Util.Log.Info("UvcTrace",
                    $"Opening USB stream: {_wantedWidth}x{_wantedHeight}@{_wantedFps} {_wantedFormat}.");
                await session.StartAsync(_wantedWidth, _wantedHeight, _wantedFps,
                    pending.Token, _wantedFormat);
                ResetPreviewWatch();
                _lastPreviewProgressUtc = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is not System.OperationCanceledException &&
                (_wantedWidth != 640 || _wantedHeight != 480 ||
                 _wantedFormat != "MJPEG" || _wantedFps != 30))
            {
                // Preserve the known-good video mode if another mode fails.
                await session.StopAsync();
                pending.Token.ThrowIfCancellationRequested();
                Status.Text = "Requested mode failed; recovering at 640 × 480 MJPEG…";
                await session.StartAsync(640, 480, 30, pending.Token, "MJPEG");
                Status.Text = $"Requested mode failed: {ex.Message}. Streaming at 640 × 480 MJPEG.";
                // Do not overwrite the saved preferred settings on fallback.
            }

            if (SettingsOverlay.IsVisible)
                ShowSupportedModes();

            if (!_foreground || !_visible)
                await session.StopAsync();
        }
        catch (System.OperationCanceledException)
        {
            // Expected when the app goes to background or camera is disconnected.
        }
        catch (Exception ex)
        {
            if (_visible && _foreground)
            {
                Status.Text = "Camera error: " + ex.Message;
                if (ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
                    _permissionRefused = true;
            }
            var session = CurrentSession ?? _observedSession;
            if (session != null)
            {
                try { await session.StopAsync(); }
                catch (Exception stopEx)
                {
                    global::Android.Util.Log.Warn("UvcTrace", "Stop error: " + stopEx);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_startCancellation, pending))
                _startCancellation = null;
            pending?.Dispose();
            _cameraGate.Release();
        }
    }

    private void OnTogglePreviewClicked(object sender, EventArgs e) =>
        SetPreviewExpanded(!_previewExpanded);

    // Change orientation and layout only; never restart the USB camera here.
    private void SetPreviewExpanded(bool expanded)
    {
        if (_previewExpanded == expanded) return;
        _previewExpanded = expanded;

        ItemsPanel.IsVisible = !expanded;
        MainSplitGrid.RowSpacing = expanded ? 0 : 10;
        MainSplitGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        MainSplitGrid.RowDefinitions[1].Height = expanded
            ? new GridLength(0, GridUnitType.Absolute)
            : new GridLength(1, GridUnitType.Star);
        ExpandPreviewGlyph.IsVisible = !expanded;
        CollapsePreviewGlyph.IsVisible = expanded;
        SemanticProperties.SetDescription(ExpandPreviewButton,
            expanded ? "Restore portrait split screen" : "Expand camera in landscape");

        // MainActivity already handles orientation/screen-size config changes.
        // The device/OS may impose orientation restrictions in some modes.
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity != null)
            activity.RequestedOrientation = expanded
                ? global::Android.Content.PM.ScreenOrientation.Landscape
                : global::Android.Content.PM.ScreenOrientation.Portrait;
    }

    private void OnSettingsClicked(object sender, EventArgs e)
    {
        SettingsOverlay.IsVisible = true;
        ShowSupportedModes();
    }

    private void OnCloseSettingsClicked(object sender, EventArgs e) =>
        SettingsOverlay.IsVisible = false;

    // Settings are populated from MJPEG modes advertised by the connected camera.
    private void ShowSupportedModes()
    {
        var session = CurrentSession;
        _supported = session is { IsStreaming: true } && session.HasUvcDevice()
            ? session.SupportedModes
                .Where(m => string.Equals(m.Format, "MJPEG", StringComparison.OrdinalIgnoreCase))
                .Distinct().ToList()
            : new List<UvcVideoMode>();

        _populatingPickers = true;
        try
        {
            ResolutionPicker.Items.Clear();
            FrameRatePicker.Items.Clear();
            _resolutions.Clear();
            _fpsValues.Clear();

            bool available = _supported.Count > 0;
            ResolutionPicker.IsEnabled = available;
            FrameRatePicker.IsEnabled = available;
            ApplyVideoButton.IsEnabled = available;
            ModesInfoLabel.Text = available
                ? $"{_supported.Count} MJPEG modes detected. Applying a different mode restarts the camera."
                : "Waiting for USB camera to report supported MJPEG modes…";
            if (!available) return;
        }
        finally { _populatingPickers = false; }

        PopulateResolutions(_wantedWidth, _wantedHeight, _wantedFps);
    }

    private void PopulateResolutions(int preferredWidth, int preferredHeight, int preferredFps)
    {
        _populatingPickers = true;
        try
        {
            _resolutions = _supported
                .Where(m => string.Equals(m.Format, "MJPEG", StringComparison.OrdinalIgnoreCase))
                .Select(m => (m.Width, m.Height))
                .Distinct()
                .OrderBy(m => m.Width * (long)m.Height)
                .ToList();
            ResolutionPicker.Items.Clear();
            foreach (var r in _resolutions)
                ResolutionPicker.Items.Add($"{r.Width} × {r.Height}");
            int index = _resolutions.FindIndex(r => r.Width == preferredWidth && r.Height == preferredHeight);
            ResolutionPicker.SelectedIndex = index >= 0 ? index : (_resolutions.Count > 0 ? 0 : -1);
        }
        finally { _populatingPickers = false; }
        PopulateFrameRates(preferredFps);
    }

    private void OnResolutionChanged(object sender, EventArgs e)
    {
        if (_populatingPickers) return;
        PopulateFrameRates(_wantedFps);
    }

    private void PopulateFrameRates(int preferredFps)
    {
        _populatingPickers = true;
        try
        {
            FrameRatePicker.Items.Clear();
            _fpsValues.Clear();
            if (ResolutionPicker.SelectedIndex < 0 || ResolutionPicker.SelectedIndex >= _resolutions.Count)
                return;
            var resolution = _resolutions[ResolutionPicker.SelectedIndex];
            _fpsValues = _supported
                .Where(m => string.Equals(m.Format, "MJPEG", StringComparison.OrdinalIgnoreCase) && m.Width == resolution.Width &&
                            m.Height == resolution.Height)
                .Select(m => m.Fps).Distinct().OrderByDescending(f => f).ToList();
            foreach (var fps in _fpsValues) FrameRatePicker.Items.Add($"{fps} FPS");
            int index = _fpsValues.IndexOf(preferredFps);
            FrameRatePicker.SelectedIndex = index >= 0 ? index : (_fpsValues.Count > 0 ? 0 : -1);
        }
        finally { _populatingPickers = false; }
    }

    private async void OnApplyVideoClicked(object sender, EventArgs e)
    {
        if (ResolutionPicker.SelectedIndex < 0 || ResolutionPicker.SelectedIndex >= _resolutions.Count ||
            FrameRatePicker.SelectedIndex < 0 || FrameRatePicker.SelectedIndex >= _fpsValues.Count)
            return;

        const string format = "MJPEG";
        var res = _resolutions[ResolutionPicker.SelectedIndex];
        int fps = _fpsValues[FrameRatePicker.SelectedIndex];
        bool changed = format != _wantedFormat || res.Width != _wantedWidth ||
            res.Height != _wantedHeight || fps != _wantedFps;
        _wantedFormat = format;
        _wantedWidth = res.Width;
        _wantedHeight = res.Height;
        _wantedFps = fps;
        Preferences.Default.Set("uvc_video_type", _wantedFormat);
        Preferences.Default.Set("uvc_width", _wantedWidth);
        Preferences.Default.Set("uvc_height", _wantedHeight);
        Preferences.Default.Set("uvc_frame_rate", _wantedFps);
        SettingsOverlay.IsVisible = false;

        if (changed && _visible && _foreground)
        {
            _frameStallRecoveries = 0;
            ResetPreviewWatch();
            // Do not let the periodic USB monitor race with the intentional
            // Stop -> Start sequence when the video mode changes.
            StopUsbMonitor();
            _missingUsbPolls = 0;
            Status.Text = "Changing video settings…";
            try
            {
                await ExecuteCameraAsync(CameraAction.Restart);
            }
            finally
            {
                if (_visible && _foreground)
                {
                    StartUsbMonitor();
                    ProbeUsbCamera();
                }
            }
        }
    }
}
