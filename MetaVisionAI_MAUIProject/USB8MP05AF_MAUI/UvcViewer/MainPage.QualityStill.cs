using Android.Graphics;
using Android.Views;
using Microsoft.Maui.ApplicationModel;

namespace UvcViewer;

public partial class MainPage
{
    /// <summary>
    /// Captures the USB SurfaceTexture into a still bitmap at the selected
    /// camera mode's aspect ratio, up to 1920 pixels wide.
    /// NOTE: TextureView scales the surface content into the requested bitmap.
    /// This is NOT a raw/native MJPEG frame and cannot restore detail lost
    /// to blur, focus, or a low-resolution UVC stream.
    /// </summary>
    private Task<Bitmap?> CaptureQualityStillAsync()
    {
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (CurrentSession?.IsStreaming != true ||
                Preview.Handler?.PlatformView is not TextureView texture ||
                !texture.IsAvailable)
                return (Bitmap?)null;

            // Use the requested MJPEG mode for size/aspect ratio, but bound
            // the bitmap to avoid large copies, memory pressure and API latency.
            int cameraWidth = _wantedWidth > 0 ? _wantedWidth : texture.Width;
            int cameraHeight = _wantedHeight > 0 ? _wantedHeight : texture.Height;
            if (cameraWidth <= 0 || cameraHeight <= 0)
                return (Bitmap?)null;

            double factor = Math.Min(1.0, 1920.0 / cameraWidth);
            int copyWidth = Math.Max(2, (int)Math.Round(cameraWidth * factor));
            int copyHeight = Math.Max(2, (int)Math.Round(cameraHeight * factor));
            // TextureView.GetBitmap(width,height) copies the preview surface;
            // it does not increase the UVC camera's native sensor resolution.
            Bitmap? frame = texture.GetBitmap(copyWidth, copyHeight);

            global::Android.Util.Log.Info("UvcQuality",
                $"Requested UVC {_wantedWidth}x{_wantedHeight}@{_wantedFps}; " +
                $"TextureView {texture.Width}x{texture.Height}; " +
                $"Still bitmap {frame?.Width ?? 0}x{frame?.Height ?? 0}");

            return frame;
        });
    }
}
