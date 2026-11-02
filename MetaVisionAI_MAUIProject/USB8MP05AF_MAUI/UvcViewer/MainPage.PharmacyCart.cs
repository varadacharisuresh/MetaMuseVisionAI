using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace UvcViewer;

// Pharmacist uses the same editable checkout page as Cashier, but with a
// separate cart. Nothing is added from a scan until the '+' icon is pressed.
public partial class MainPage
{
    private readonly ObservableCollection<CashierOrderItem> _pharmacyCart = new();
    private readonly Dictionary<string, (string Name, string Source, int Count)> _pharmacyPending =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _pharmacyCheckoutOpen;
    private bool IsPharmacistRole =>
        string.Equals(WorkflowSession.RoleId, "pharmacist", StringComparison.OrdinalIgnoreCase);

    private static string PharmacyCanonicalName(string text)
    {
        string name = text.Trim();
        // Normalize only common object names: broader pluralization can mangle
        // actual product names and medication descriptions.
        if (name.Equals("tablet", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("tablets", StringComparison.OrdinalIgnoreCase)) return "Tablets";
        if (name.Equals("pill", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pills", StringComparison.OrdinalIgnoreCase)) return "Pills";
        if (name.Equals("capsule", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("capsules", StringComparison.OrdinalIgnoreCase)) return "Capsules";
        return name;
    }

    private void ClearPharmacyPending()
    {
        _pharmacyPending.Clear();
        RefreshPharmacyCartUI();
    }

    private void StagePharmacyCountedItems(IEnumerable<(string Name, int Count)> items)
    {
        _pharmacyPending.Clear();
        if (IsPharmacistRole && CurrentTask.Id == "count_items")
        {
            foreach (var (name, count) in items)
            {
                if (string.IsNullOrWhiteSpace(name) || count <= 0) continue;
                string normalized = PharmacyCanonicalName(name);
                string key = "Visual count (estimated):" + normalized.ToUpperInvariant();
                if (_pharmacyPending.TryGetValue(key, out var existing))
                    _pharmacyPending[key] = (normalized, "Visual count (estimated)",
                        Math.Min(9999, existing.Count + count));
                else
                    _pharmacyPending[key] = (normalized, "Visual count (estimated)", Math.Min(9999, count));
            }
        }
        RefreshPharmacyCartUI();
    }

    private void StagePharmacyBarcodes(IEnumerable<string> codes)
    {
        _pharmacyPending.Clear();
        if (IsPharmacistRole && CurrentTask.Id == "barcode")
        {
            foreach (string code in codes.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                string trimmed = code.Trim();
                string key = "Barcode:" + trimmed.ToUpperInvariant();
                if (_pharmacyPending.TryGetValue(key, out var existing))
                    _pharmacyPending[key] = (trimmed, "Barcode (unverified)", existing.Count + 1);
                else
                    _pharmacyPending[key] = (trimmed, "Barcode (unverified)", 1);
            }
        }
        RefreshPharmacyCartUI();
    }

    private void OnPharmacyAddClicked(object sender, EventArgs e)
    {
        if (!IsPharmacistRole || _pharmacyCheckoutOpen ||
            _museScanGate.CurrentCount == 0 || MuseScanLoadingOverlay.IsVisible ||
            _pharmacyPending.Count == 0) return;

        // Consume the latest scan exactly once. A correction or next batch must
        // be scanned again; re-tapping '+' cannot silently double the count.
        foreach (var (name, source, count) in _pharmacyPending.Values)
        {
            string key = source + ":" + name.ToUpperInvariant();
            var existing = _pharmacyCart.FirstOrDefault(x => x.Key == key);
            if (existing == null)
                _pharmacyCart.Add(new CashierOrderItem(name, source, count));
            else
                existing.Quantity = Math.Min(9999, existing.Quantity + count);
        }
        _pharmacyPending.Clear();
        RefreshPharmacyCartUI();
    }

    private void RefreshPharmacyCartUI()
    {
        if (PharmacyAddGrid == null || PharmacyCartGrid == null || PharmacyOrderPanel == null)
            return;
        bool visible = IsPharmacistRole;
        bool eligible = visible && (CurrentTask.Id == "count_items" || CurrentTask.Id == "barcode");
        PharmacyAddGrid.IsVisible = eligible;
        PharmacyCartGrid.IsVisible = visible;
        bool hasPending = eligible && _pharmacyPending.Count > 0;
        PharmacyAddButton.IsEnabled = hasPending;
        PharmacyAddBorder.BackgroundColor = Color.FromArgb(hasPending ? "#116EA8" : "#31465D");
        int pieces = _pharmacyCart.Sum(x => x.Quantity);
        PharmacyCartCount.Text = pieces.ToString();
        PharmacyCheckoutButton.IsEnabled = pieces > 0;
        PharmacyCartBorder.BackgroundColor = Color.FromArgb(pieces > 0 ? "#24648F" : "#31465D");
        PharmacyOrderPanel.IsVisible = visible && _pharmacyCart.Count > 0;
        PharmacyOrderUnits.Text = pieces == 1 ? "1 item" : $"{pieces} items";
        PharmacyOrderPreviewList.Children.Clear();
        foreach (var item in _pharmacyCart)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                ColumnSpacing = 8,
                Padding = new Thickness(10, 7),
                BackgroundColor = Color.FromArgb("#213E56")
            };
            row.Children.Add(new Label
            {
                Text = item.Description,
                FontSize = 14,
                TextColor = Colors.White,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.WordWrap
            });
            var qty = new Label
            {
                Text = $"× {item.Quantity}",
                FontSize = 16,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#83D7FF"),
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(qty, 1);
            row.Children.Add(qty);
            PharmacyOrderPreviewList.Children.Add(row);
        }
    }

    private async void OnPharmacyCheckoutClicked(object sender, EventArgs e)
    {
        if (!IsPharmacistRole || _pharmacyCheckoutOpen || _pharmacyCart.Count == 0 ||
            _museScanGate.CurrentCount == 0 || MuseScanLoadingOverlay.IsVisible)
            return;

        _pharmacyCheckoutOpen = true;
        _listenRequested = false;
        StopVoiceRecognition();
        UpdateListenButton();
        var page = new CashierCheckoutPage(_pharmacyCart, "Pharmacist Checkout");
        page.Closed += OnPharmacyCheckoutClosed;
        page.OrderReset += OnPharmacyOrderReset;
        try
        {
            await Navigation.PushModalAsync(page);
        }
        catch (Exception ex)
        {
            page.Closed -= OnPharmacyCheckoutClosed;
            page.OrderReset -= OnPharmacyOrderReset;
            _pharmacyCheckoutOpen = false;
            await ShowResultAsync("Pharmacist checkout", ex.Message,
                "Checkout unavailable", Verdict.Review, "");
        }
    }

    private void OnPharmacyOrderReset(object? sender, EventArgs e)
    {
        // Also discard any not-yet-added scan when the pharmacist starts a new order.
        ClearPharmacyPending();
    }

    private void OnPharmacyCheckoutClosed(object? sender, EventArgs e)
    {
        if (sender is CashierCheckoutPage page)
        {
            page.Closed -= OnPharmacyCheckoutClosed;
            page.OrderReset -= OnPharmacyOrderReset;
        }
        _pharmacyCheckoutOpen = false;
        RefreshPharmacyCartUI();
    }
}
