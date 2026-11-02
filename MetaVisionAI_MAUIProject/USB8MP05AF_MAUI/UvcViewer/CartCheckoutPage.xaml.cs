using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Microsoft.Maui.Controls;

namespace UvcViewer;

// Separate editable checkout screen; scanner remains the source of cart rows.
public partial class CartCheckoutPage : ContentPage
{
    private readonly ObservableCollection<BarcodeCartRow> _items;
    private readonly Action _updateScannerCount;
    private static readonly CultureInfo MoneyCulture = CultureInfo.GetCultureInfo("en-US");
    private bool _closed;

    public event EventHandler? Closed;

    public CartCheckoutPage(ObservableCollection<BarcodeCartRow> items,
                            Action updateScannerCount)
    {
        InitializeComponent();
        _items = items;
        _updateScannerCount = updateScannerCount;
        CheckoutItems.ItemsSource = _items;
        _items.CollectionChanged += OnItemsChanged;
        UpdateSummary();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => UpdateSummary();

    private void UpdateSummary()
    {
        int entries = _items.Count;
        int units = _items.Sum(x => x.Quantity);
        decimal amount = _items.Sum(x => x.LineTotal);
        CartItemCount.Text = entries == 1 ? "1 item" : $"{entries} items";
        TotalQuantityLabel.Text = units.ToString(CultureInfo.InvariantCulture);
        TotalAmountLabel.Text = amount.ToString("C2", MoneyCulture);
        CheckoutButton.IsEnabled = entries > 0;
        CheckoutButton.Opacity = entries > 0 ? 1 : 0.5;
    }

    private void OnDecreaseQuantityClicked(object sender, EventArgs e)
    {
        if (sender is Button { BindingContext: BarcodeCartRow row })
        {
            row.Quantity = Math.Max(1, row.Quantity - 1);
            UpdateSummary();
        }
    }

    private void OnIncreaseQuantityClicked(object sender, EventArgs e)
    {
        if (sender is Button { BindingContext: BarcodeCartRow row })
        {
            row.Quantity = Math.Min(999, row.Quantity + 1);
            UpdateSummary();
        }
    }

    private void OnQuantityTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not Entry { BindingContext: BarcodeCartRow row }) return;
        // Accept changes while typing, but don't turn a temporary blank into 1.
        if (int.TryParse(e.NewTextValue, NumberStyles.None,
                         CultureInfo.InvariantCulture, out int quantity) && quantity > 0)
        {
            row.Quantity = Math.Clamp(quantity, 1, 999);
            UpdateSummary();
        }
    }

    private void OnQuantityUnfocused(object sender, FocusEventArgs e)
    {
        if (sender is not Entry { BindingContext: BarcodeCartRow row } entry) return;
        // Restore a valid number if the user left this field blank/invalid.
        entry.Text = row.Quantity.ToString(CultureInfo.InvariantCulture);
        UpdateSummary();
    }

    private async void OnDeleteClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: BarcodeCartRow row } button) return;
        button.IsEnabled = false;
        await UiButtonFeedback.TapAsync((button.Parent as VisualElement) ?? button);
        if (!_items.Remove(row)) return;
        for (int i = 0; i < _items.Count; i++)
            _items[i].SetNumber(i + 1);
        _updateScannerCount();
        UpdateSummary();
    }

    private async void OnBackClicked(object sender, EventArgs e)
    {
        if (sender is Button button)
            await UiButtonFeedback.TapAsync((button.Parent as VisualElement) ?? button);
        await Navigation.PopModalAsync();
    }

    private async void OnCheckoutClicked(object sender, EventArgs e)
    {
        if (_items.Count == 0) return;
        if (sender is Button button)
            await UiButtonFeedback.TapAsync(button);
        int units = _items.Sum(x => x.Quantity);
        decimal amount = _items.Sum(x => x.LineTotal);
        await DisplayAlert("Demo checkout",
            $"{units} units • {amount.ToString("C2", MoneyCulture)}\n\n" +
            "Prices without a company API or printed-price verification are DEMO amounts. No payment is processed.",
            "OK");
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_closed) return;
        _closed = true;
        _items.CollectionChanged -= OnItemsChanged;
        _updateScannerCount();
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
