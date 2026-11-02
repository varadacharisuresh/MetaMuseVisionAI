using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace UvcViewer;

// Cashier line items are separate from the legacy BarcodeCartRow type and
// never created for non-cashier roles.
public sealed class CashierOrderItem : INotifyPropertyChanged
{
    private int _quantity;
    private decimal? _unitPrice;

    public string Key { get; }
    public string Description { get; }
    public string SourceType { get; }
    public int Quantity
    {
        get => _quantity;
        set
        {
            int safe = Math.Clamp(value, 1, 9999);
            if (_quantity == safe) return;
            _quantity = safe;
            Notify(nameof(LineTotalText));
        }
    }
    public decimal? UnitPrice
    {
        get => _unitPrice;
        set
        {
            if (_unitPrice == value) return;
            _unitPrice = value;
            Notify(nameof(LineTotalText));
        }
    }
    public string UnitPriceText => UnitPrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
    public decimal? LineTotal => UnitPrice is decimal price ? price * Quantity : null;
    public string LineTotalText => LineTotal?.ToString("C2", CultureInfo.GetCultureInfo("en-US")) ?? "Set unit price";
    public event PropertyChangedEventHandler? PropertyChanged;

    public CashierOrderItem(string description, string sourceType, int quantity)
    {
        Description = description;
        SourceType = sourceType;
        Key = sourceType + ":" + description.Trim().ToUpperInvariant();
        _quantity = Math.Clamp(quantity, 1, 9999);
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainPage
{
    private readonly ObservableCollection<CashierOrderItem> _cashierOrderItems = new();
    private bool _cashierCheckoutOpen;
    private bool IsCashierRole => string.Equals(WorkflowSession.RoleId, "cashier", StringComparison.OrdinalIgnoreCase);

    private void AddCashierBarcodeValues(IEnumerable<string> values)
    {
        if (!IsCashierRole) return;
        foreach (string barcode in values.Where(v => !string.IsNullOrWhiteSpace(v)))
            AddCashierOrderItem(barcode.Trim(), "Barcode", 1);
        RefreshCashierOrderUI();
    }

    private void AddCashierCountedItems(IEnumerable<(string Name, int Count)> objects)
    {
        if (!IsCashierRole) return;
        foreach (var item in objects)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.Count <= 0) continue;
            AddCashierOrderItem(item.Name.Trim(), "Visual count (estimated)", item.Count);
        }
        RefreshCashierOrderUI();
    }

    private void AddCashierOrderItem(string description, string source, int quantity)
    {
        string key = source + ":" + description.Trim().ToUpperInvariant();
        var existing = _cashierOrderItems.FirstOrDefault(x => x.Key == key);
        if (existing == null)
            _cashierOrderItems.Add(new CashierOrderItem(description, source, quantity));
        else
            existing.Quantity = Math.Min(9999, existing.Quantity + quantity);
    }

    private void RefreshCashierOrderUI()
    {
        if (CashierCheckoutButton == null || CashierOrderPanel == null) return;
        CashierCheckoutButton.IsVisible = IsCashierRole;
        if (!IsCashierRole)
        {
            CashierOrderPanel.IsVisible = false;
            return;
        }

        int units = _cashierOrderItems.Sum(x => x.Quantity);
        CashierCheckoutButton.Text = $"Checkout ({units})";
        CashierOrderPanel.IsVisible = _cashierOrderItems.Count > 0;
        CashierOrderUnits.Text = units == 1 ? "1 item" : $"{units} items";
        CashierOrderPreviewList.Children.Clear();
        foreach (var item in _cashierOrderItems)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                ColumnSpacing = 10,
                Padding = new Thickness(10, 8),
                BackgroundColor = Color.FromArgb("#213E56")
            };
            var text = new VerticalStackLayout { Spacing = 2 };
            text.Children.Add(new Label
            {
                Text = item.Description,
                FontSize = 14,
                TextColor = Colors.White,
                LineBreakMode = LineBreakMode.WordWrap
            });
            text.Children.Add(new Label
            {
                Text = item.SourceType,
                FontSize = 10,
                TextColor = Color.FromArgb("#A1BDD0")
            });
            row.Children.Add(text);
            var qty = new Label
            {
                Text = $"× {item.Quantity}",
                TextColor = Color.FromArgb("#83D7FF"),
                FontAttributes = FontAttributes.Bold,
                FontSize = 16,
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(qty, 1);
            row.Children.Add(qty);
            CashierOrderPreviewList.Children.Add(row);
        }
    }

    private async void OnCashierCheckoutClicked(object sender, EventArgs args)
    {
        if (!IsCashierRole || _cashierCheckoutOpen || _museScanGate.CurrentCount == 0 ||
            MuseScanLoadingOverlay.IsVisible)
            return;
        _cashierCheckoutOpen = true;
        // Stop voice monitoring while a cashier edits quantities and prices.
        // User can reactivate the microphone after returning to the scanner.
        _listenRequested = false;
        StopVoiceRecognition();
        UpdateListenButton();
        var page = new CashierCheckoutPage(_cashierOrderItems);
        page.Closed += OnCashierCheckoutClosed;
        try
        {
            await Navigation.PushModalAsync(page);
        }
        catch (Exception ex)
        {
            page.Closed -= OnCashierCheckoutClosed;
            _cashierCheckoutOpen = false;
            await ShowResultAsync("Cashier checkout", ex.Message,
                "Checkout unavailable", Verdict.Review, "");
        }
    }

    private void OnCashierCheckoutClosed(object? sender, EventArgs e)
    {
        if (sender is CashierCheckoutPage page)
            page.Closed -= OnCashierCheckoutClosed;
        _cashierCheckoutOpen = false;
        RefreshCashierOrderUI();
    }
}
