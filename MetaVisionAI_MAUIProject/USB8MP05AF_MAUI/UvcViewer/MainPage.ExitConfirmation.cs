using System;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace UvcViewer;

public partial class MainPage
{
    // Handle the Android Back key on the scanner screen.
    protected override bool OnBackButtonPressed()
    {
        MainThread.BeginInvokeOnMainThread(() => ExitConfirmationOverlay.IsVisible = true);
        return true; // Stop Android from closing immediately.
    }

    private void OnExitStayClicked(object sender, EventArgs e)
        => ExitConfirmationOverlay.IsVisible = false;

    private void OnExitYesClicked(object sender, EventArgs e)
    {
        ExitConfirmationOverlay.IsVisible = false;
        _listenRequested = false;
        StopVoiceRecognition();
        Application.Current?.Quit();
    }
}
