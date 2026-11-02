using System;
using Microsoft.Maui.Controls;

namespace UvcViewer;

public partial class MainPage
{
    // Manual and spoken "Scan" share the same Muse API, loading overlay,
    // success/error sounds, and shopping-cart append logic.
    private async void OnManualScanClicked(object sender, EventArgs e)
    {
        // The same gate prevents a second request while Muse is busy.
        if (MuseScanLoadingOverlay.IsVisible || _museScanGate.CurrentCount == 0)
            return;

        // Prevent speech recognition from triggering an overlapping scan.
        // Do not change _listenRequested: if the mic was active it will
        // automatically resume after ScanMuseBarcodeAsync completes.
        if (_listenRequested)
            StopVoiceRecognition();

        await ScanMuseBarcodeAsync();
    }
}
