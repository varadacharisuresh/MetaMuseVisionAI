using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Microsoft.Maui.Controls;

namespace UvcViewer;

public partial class CashierCheckoutPage : ContentPage
{
    private readonly ObservableCollection<CashierOrderItem> _order;
    private static readonly CultureInfo CurrencyCulture = CultureInfo.GetCultureInfo("en-US");
    private bool _closed;
    private readonly bool _canResetOrder;
    public event EventHandler? Closed;
    public event EventHandler? OrderReset;

    public CashierCheckoutPage(ObservableCollection<CashierOrderItem> items, string title = "Cashier Checkout")
    {
        InitializeComponent();
        CheckoutTitleLabel.Text = title;
        _canResetOrder = string.Equals(title, "Cashier Checkout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "Pharmacist Checkout", StringComparison.OrdinalIgnoreCase);
        ResetOrderButton.IsVisible = _canResetOrder;
        _order = items;
        CheckoutItems.ItemsSource = _order;
        _order.CollectionChanged += OnCollectionChanged;
        UpdateSummary();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        int units = _order.Sum(x => x.Quantity);
        int unpriced = _order.Count(x => x.UnitPrice == null);
        OrderCountLabel.Text = units == 1 ? "1 item" : $"{units} items";
        TotalUnitsLabel.Text = units.ToString(CultureInfo.InvariantCulture);
        TotalPriceLabel.Text = unpriced == 0 && _order.Count > 0
            ? _order.Sum(x => x.LineTotal ?? 0).ToString("C2", CurrencyCulture)
            : "Set prices";
        MissingPricesLabel.Text = unpriced == 0
            ? "Ready to review. No payment has been taken."
            : $"Enter a unit price for {unpriced} line item(s); no prices are invented.";
        CheckoutButton.IsEnabled = _order.Count > 0 && unpriced == 0;
        CheckoutButton.Opacity = CheckoutButton.IsEnabled ? 1.0 : 0.55;
    }

    private void OnQuantityChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not Entry { BindingContext: CashierOrderItem row }) return;
        if (int.TryParse(e.NewTextValue, NumberStyles.None, CultureInfo.InvariantCulture, out int quantity)
            && quantity is >= 1 and <= 9999)
        {
            row.Quantity = quantity;
            UpdateSummary();
        }
    }

    private void OnQuantityUnfocused(object sender, FocusEventArgs e)
    {
        if (sender is Entry { BindingContext: CashierOrderItem row } field)
            field.Text = row.Quantity.ToString(CultureInfo.InvariantCulture);
    }

    private void OnPriceChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not Entry { BindingContext: CashierOrderItem row }) return;
        string value = (e.NewTextValue ?? "").Trim();
        if (value.Length == 0)
        {
            row.UnitPrice = null;
            UpdateSummary();
        }
        else if (decimal.TryParse(value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out decimal price) && price >= 0 && price <= 9999999)
        {
            row.UnitPrice = price;
            UpdateSummary();
        }
    }

    private void OnDeleteClicked(object sender, EventArgs e)
    {
        if (sender is Button { BindingContext: CashierOrderItem row })
            _order.Remove(row);
        UpdateSummary();
    }

    private async void OnBackClicked(object sender, EventArgs e) => await Navigation.PopModalAsync();

    private void OnCheckoutClicked(object sender, EventArgs e)
    {
        if (_order.Count == 0 || _order.Any(x => x.UnitPrice == null)) return;
        int pieces = _order.Sum(x => x.Quantity);
        decimal total = _order.Sum(x => x.LineTotal ?? 0m);
        SummaryText.Text = $"{_order.Count} line items • {pieces} units\nTotal: {total.ToString("C2", CurrencyCulture)}";
        SummaryOverlay.IsVisible = true;
    }

    private void OnCloseSummaryClicked(object sender, EventArgs e) => SummaryOverlay.IsVisible = false;

    private void OnResetOrderClicked(object sender, EventArgs e)
    {
        if (!_canResetOrder) return;
        ResetOrderConfirmationOverlay.IsVisible = true;
    }

    private void OnCancelResetOrderClicked(object sender, EventArgs e)
    {
        ResetOrderConfirmationOverlay.IsVisible = false;
    }

    private async void OnConfirmResetOrderClicked(object sender, EventArgs e)
    {
        if (!_canResetOrder) return;
        ResetOrderConfirmationOverlay.IsVisible = false;
        SummaryOverlay.IsVisible = false;
        _order.Clear(); // Clears only the collection passed to this checkout page.
        OrderReset?.Invoke(this, EventArgs.Empty); // Allow the role to discard a pending scan.
        await Navigation.PopModalAsync();
    }
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_closed) return;
        _closed = true;
        _order.CollectionChanged -= OnCollectionChanged;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
