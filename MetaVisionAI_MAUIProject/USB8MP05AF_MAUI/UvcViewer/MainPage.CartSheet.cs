using System;
using Microsoft.Maui.Controls;

namespace UvcViewer;

// Layout-only cart expansion. Keeps the UVC TextureView and native stream mounted
// underneath the cart at all times; no camera visibility or lifecycle changes.
public partial class MainPage
{
    private bool _cartExpanded;
    private double _cartPanLastY;

    private void OnCartSwipeUp(object sender, SwipedEventArgs e) => SetCartExpanded(true);

    private void OnCartSwipeDown(object sender, SwipedEventArgs e) => SetCartExpanded(false);

    private void OnCartHeaderTapped(object sender, TappedEventArgs e) =>
        SetCartExpanded(!_cartExpanded);

    private void OnCartHeaderPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started)
        {
            _cartPanLastY = 0;
            return;
        }
        if (e.StatusType == GestureStatus.Running)
        {
            _cartPanLastY = e.TotalY;
            return;
        }
        if (e.StatusType != GestureStatus.Completed) return;

        // Some Android handlers reset TotalY on Completed. Use last Running value.
        double distanceY = _cartPanLastY;
        if (distanceY < -40)
            SetCartExpanded(true);
        else if (distanceY > 40)
            SetCartExpanded(false);
    }

    private void SetCartExpanded(bool expanded)
    {
        // The processing overlay remains modal and non-interactive underneath.
        if (MuseScanLoadingOverlay.IsVisible || !ItemsPanel.IsVisible) return;
        if (_cartExpanded == expanded) return;

        _cartExpanded = expanded;
        // Spanning both rows overlays the camera without hiding/unmounting it.
        // This preserves UVC surface/USB camera state even while cart is full-screen.
        Grid.SetRow(ItemsPanel, expanded ? 0 : 1);
        Grid.SetRowSpan(ItemsPanel, expanded ? 2 : 1);
        ItemsPanel.ZIndex = expanded ? 5 : 0;
        SemanticProperties.SetDescription(CartDragHeader,
            expanded ? "Swipe down to show the camera" : "Swipe up to expand scanned items");
    }
}
