using System;

namespace UvcViewer;

// Main scanner reset for Cashier and Pharmacist. The checkout reset stays unchanged.
public partial class MainPage
{
    private void UpdateMainOrderResetVisibility()
    {
        MainOrderResetGrid.IsVisible = IsCashierRole || IsPharmacistRole;
    }

    private void OnMainOrderResetClicked(object sender, EventArgs e)
    {
        if ((!IsCashierRole && !IsPharmacistRole) ||
            _cashierCheckoutOpen || _pharmacyCheckoutOpen ||
            _museScanGate.CurrentCount == 0 || MuseScanLoadingOverlay.IsVisible)
            return;

        // Stop voice recognition before asking: a new scan must not change the cart
        // while the user is confirming whether it should be cleared.
        _listenRequested = false;
        StopVoiceRecognition();
        UpdateListenButton();
        MainOrderResetConfirmationOverlay.IsVisible = true;
    }

    private void OnCancelMainOrderResetClicked(object sender, EventArgs e)
    {
        MainOrderResetConfirmationOverlay.IsVisible = false;
    }

    private void OnConfirmMainOrderResetClicked(object sender, EventArgs e)
    {
        if (!MainOrderResetConfirmationOverlay.IsVisible)
            return;
        MainOrderResetConfirmationOverlay.IsVisible = false;
        if (_museScanGate.CurrentCount == 0 || MuseScanLoadingOverlay.IsVisible)
            return;

        if (IsCashierRole)
        {
            _cashierOrderItems.Clear();
            RefreshCashierOrderUI();
        }
        else if (IsPharmacistRole)
        {
            _pharmacyCart.Clear();
            ClearPharmacyPending(); // Also discards the un-added pharmacist scan.
            RefreshPharmacyCartUI();
        }
        else
            return;

        _countedObjects.Clear();
        _countedNotes.Clear();
        ResetWorkflowResult();
    }
}
