using System;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace UvcViewer;

public partial class RoleSelectionPage
{
    // Also ask for confirmation if Android Back is pressed before starting a scan.
    protected override bool OnBackButtonPressed()
    {
        MainThread.BeginInvokeOnMainThread(() => ExitConfirmationOverlay.IsVisible = true);
        return true;
    }

    private void OnExitStayClicked(object sender, EventArgs e)
        => ExitConfirmationOverlay.IsVisible = false;

    private void OnExitYesClicked(object sender, EventArgs e)
    {
        ExitConfirmationOverlay.IsVisible = false;
        Application.Current?.Quit();
    }
}
