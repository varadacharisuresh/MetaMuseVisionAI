using System;
using Microsoft.Maui.ApplicationModel;

namespace UvcViewer;

// Startup runtime permissions only. No USB camera/JNI changes.
public partial class MainPage
{
    private bool _startupMediaPermissionCheckDone;

    private async void OnRequestMediaPermissions(object sender, EventArgs e)
    {
        if (_startupMediaPermissionCheckDone)
            return;

        _startupMediaPermissionCheckDone = true;

        try
        {
            // CAMERA is also required for USB Video Class permission on Android 9+.
            var camera = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (camera != PermissionStatus.Granted)
                camera = await Permissions.RequestAsync<Permissions.Camera>();

            // Enables speech recognition and audio recording.
            var microphone = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (microphone != PermissionStatus.Granted)
                microphone = await Permissions.RequestAsync<Permissions.Microphone>();

            if (camera != PermissionStatus.Granted ||
                microphone != PermissionStatus.Granted)
            {
                await DisplayAlertAsync(
                    "Permissions needed",
                    "Camera and microphone access are needed for USB video and voice/audio recording. " +
                    "If access was denied, enable it in Android Settings > Apps > UvcViewer > Permissions.",
                    "OK");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("UvcMediaPermissions", ex.ToString());
            await DisplayAlertAsync("Permission error", ex.Message, "OK");
        }
    }
}
