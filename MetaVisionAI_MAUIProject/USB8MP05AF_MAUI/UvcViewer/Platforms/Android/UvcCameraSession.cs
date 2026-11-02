using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Usb;
using Android.OS;
using Android.Views;

namespace UvcViewer.Platforms.Android;

/// <summary>Owns USB Host permission, stream, texture surface and native UVC resources.</summary>
public sealed class UvcCameraSession : Java.Lang.Object, TextureView.ISurfaceTextureListener
{
    private readonly Context _context;
    private readonly TextureView _view;
    private readonly UsbManager _usb;
    // The previous TextureView/session can still be closing when the app reopens.
    // Serialize native open/close across ALL session instances, not just one instance.
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _permissionCancellation;
    private UvcJni? _jni;
    private UsbDeviceConnection? _connection;
    private Surface? _surface;
    private TaskCompletionSource<bool> _surfaceReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _receivedFrames;
    // Only count TextureView callbacks after native start has returned.
    // Surface updates before start may just redraw a blank texture.
    private volatile bool _countingNativeFrames;
    public IReadOnlyList<UvcVideoMode> SupportedModes { get; private set; } =
        Array.Empty<UvcVideoMode>();

    // Count updates after native start returned; unlike IsStreaming this can
    // distinguish a negotiated but frozen stream from an updating preview.
    public long PreviewUpdateCount => Interlocked.Read(ref _receivedFrames);
    public bool IsStreaming => _jni != null;

    // Safe to query periodically while the app is in the foreground.
    public bool HasUvcDevice() => _usb.DeviceList.Values.Any(IsVideoDevice);

    public event Action<string>? StatusChanged;

    public UvcCameraSession(Context context, TextureView view)
    {
        _context = context;
        _view = view;
        _usb = (UsbManager?)context.GetSystemService(Context.UsbService)
            ?? throw new NotSupportedException("Android USB host service is unavailable.");
    }

    // Only diagnostics; doesn't change stream selection or camera settings.
    private void Status(string message)
    {
        global::Android.Util.Log.Info("UvcTrace", message);
        StatusChanged?.Invoke(message);
    }

    public async Task StartAsync(int width = 1280, int height = 720, int fps = 30,
        CancellationToken cancellationToken = default, string format = "MJPEG")
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_jni != null) { Status("Camera is already streaming."); return; }
            if (!_view.IsAvailable || _view.SurfaceTexture == null)
            {
                // TextureView can be created after MainPage.OnAppearing.
                await _surfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!_view.IsAvailable || _view.SurfaceTexture == null)
                throw new InvalidOperationException("Preview surface is not ready.");

            var device = _usb.DeviceList.Values.FirstOrDefault(IsVideoDevice)
                ?? throw new InvalidOperationException("No USB Video Class (UVC) device detected. Check OTG and cable.");

            Status($"Found USB camera {device.VendorId:X4}:{device.ProductId:X4}");
            if (!_usb.HasPermission(device))
            {
                Status("Allow USB camera access in the Android prompt.");
                var permissionIntent = new Intent(_context.PackageName + ".USB_UVC_PERMISSION")
                    .SetPackage(_context.PackageName);
                var flags = PendingIntentFlags.UpdateCurrent;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                    flags |= PendingIntentFlags.Mutable;
                using var pending = PendingIntent.GetBroadcast(_context, 724, permissionIntent, flags)
                    ?? throw new InvalidOperationException("Unable to request USB permission.");
                _usb.RequestPermission(device, pending);
                _permissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _permissionCancellation.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    while (!_usb.HasPermission(device))
                    {
                        await Task.Delay(250, _permissionCancellation.Token);
                        if (!_usb.DeviceList.Values.Any(x => x.DeviceId == device.DeviceId))
                            throw new InvalidOperationException("USB camera was disconnected.");
                    }
                }
                catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (System.OperationCanceledException)
                {
                    throw new InvalidOperationException("USB permission not granted. Reconnect camera or return to the app to retry.");
                }
                finally
                {
                    _permissionCancellation.Dispose();
                    _permissionCancellation = null;
                }
            }

            _connection = _usb.OpenDevice(device)
                ?? throw new InvalidOperationException("Cannot open USB camera despite permission.");
            _surface = new Surface(_view.SurfaceTexture!);
            // Initialize native class metadata on the UI thread, then stream off the UI thread.
            // StartAsync is invoked on the Android main thread by the automatic monitor.
            _jni = new UvcJni(_context);
            Status("Starting native UVC stream…");
            var fd = _connection.FileDescriptor;
            var camera = _jni;
            var surface = _surface;
            _countingNativeFrames = false;
            Interlocked.Exchange(ref _receivedFrames, 0);
            Status($"Requesting {width}x{height}@{fps}; TextureView available={_view.IsAvailable}; surface valid={surface.IsValid}");
            var result = await Task.Run(() => camera.OpenAndStart(fd, surface,
                width, height, fps, format), cancellationToken);
            SupportedModes = camera.SupportedModes;

            // SurfaceTexture update callbacks are not a reliable UVC frame count.
            // At some resolutions native streaming succeeds without three callbacks.
            // Keep callbacks for diagnostics, but do not stop a negotiated stream.
            Interlocked.Exchange(ref _receivedFrames, 0);
            _countingNativeFrames = true;
            Status(result);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("UvcTrace", "Start failed: " + ex);
            try { await CleanupAsync(); }
            catch (Exception cleanupError) { Status("Cleanup error: " + cleanupError.Message); }
            throw;
        }
        finally { _lock.Release(); }
    }

    public async Task StopAsync()
    {
        try { _permissionCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        await _lock.WaitAsync();
        try
        {
            await CleanupAsync();
            Status("USB camera stopped.");
        }
        finally { _lock.Release(); }
    }

    private async Task CleanupAsync()
    {
        _countingNativeFrames = false;
        if (_jni is { } jni)
        {
            _jni = null;
            try { await Task.Run(jni.StopAndClose); }
            finally { jni.Dispose(); }
        }
        _surface?.Release();
        _surface?.Dispose();
        _surface = null;
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
    }

    public async Task<UvcFocusInfo> GetFocusInfoAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var camera = _jni ?? throw new InvalidOperationException("Camera not streaming.");
            return await Task.Run(camera.ReadFocusInfo);
        }
        finally { _lock.Release(); }
    }

    public async Task SetAutoFocusAsync(bool enabled)
    {
        await _lock.WaitAsync();
        try
        {
            var camera = _jni ?? throw new InvalidOperationException("Camera not streaming.");
            await Task.Run(() => camera.SetAutoFocus(enabled));
        }
        finally { _lock.Release(); }
    }

    public async Task SetManualFocusAsync(int position)
    {
        await _lock.WaitAsync();
        try
        {
            var camera = _jni ?? throw new InvalidOperationException("Camera not streaming.");
            await Task.Run(() => camera.SetManualFocus(position));
        }
        finally { _lock.Release(); }
    }

    private static bool IsVideoDevice(UsbDevice device)
    {
        for (var i = 0; i < device.InterfaceCount; i++)
            if (device.GetInterface(i)?.InterfaceClass == UsbClass.Video) // USB_CLASS_VIDEO
                return true;
        return false;
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        => _surfaceReady.TrySetResult(true);
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        // Android must not release the texture while the native camera may still be writing to it.
        _surfaceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = StopAndReleaseAsync(surface);
        return false;
    }
    private async Task StopAndReleaseAsync(SurfaceTexture surface)
    {
        try { await StopAsync(); }
        catch (Exception ex) { Status("Cleanup: " + ex.Message); }
        finally { surface.Release(); }
    }
    public void OnSurfaceTextureUpdated(SurfaceTexture surface)
    {
        if (!_countingNativeFrames)
        {
            // This is a redraw, not a verified camera frame. Do not count it.
            global::Android.Util.Log.Debug("UvcTrace", "TextureView update before stream was verified");
            return;
        }

        long count = Interlocked.Increment(ref _receivedFrames);
        if (count == 1 || count % 30 == 0)
            global::Android.Util.Log.Info("UvcTrace",
                $"Post-start TextureView updates: {count}");
    }
}
