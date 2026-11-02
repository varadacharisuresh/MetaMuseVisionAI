using System;
using Microsoft.Maui.Controls;

namespace UvcViewer;

public partial class MainPage
{
    private bool _checkoutScreenOpen;

    private async void OnOpenCartCheckoutTapped(object sender, TappedEventArgs e)
    {
        if (_checkoutScreenOpen || MuseScanLoadingOverlay.IsVisible || _workflowIndex is not (0 or 3)) return;
        _checkoutScreenOpen = true;

        // The cart header and rows are tappable surfaces rather than Buttons.
        // Give them the same visible feedback before opening the checkout screen.
        if (sender is VisualElement tappedElement)
            await UiButtonFeedback.TapAsync(tappedElement);

        // Pause voice requests while the user edits the shopping cart.
        // Keep the user's mic preference so listening can resume on return.
        if (_listenRequested)
            StopVoiceRecognition();

        var page = new CartCheckoutPage(_cartRows, RefreshCartCount);
        page.Closed += OnCartCheckoutClosed;
        try
        {
            await Navigation.PushModalAsync(page);
        }
        catch (Exception ex)
        {
            page.Closed -= OnCartCheckoutClosed;
            _checkoutScreenOpen = false;
            ShowScanProblem("Could not open shopping cart: " + ex.Message);
            if (_scanPageLoaded && _listenRequested)
                _ = StartVoiceRecognitionAsync();
        }
    }

    private void OnCartCheckoutClosed(object? sender, EventArgs e)
    {
        if (sender is CartCheckoutPage page)
            page.Closed -= OnCartCheckoutClosed;
        _checkoutScreenOpen = false;
        RefreshCartCount();
        if (_scanPageLoaded && _listenRequested && !MuseScanLoadingOverlay.IsVisible)
            _ = StartVoiceRecognitionAsync();
    }
}
