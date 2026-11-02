using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Android.Graphics;
using Android.Views;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace UvcViewer;

// One cart row per decoded code, including repeated scans of the same code.
public sealed class BarcodeCartRow : INotifyPropertyChanged
{
    public string Code { get; }
    public string? AssociatedId { get; }
    private string? _productName;
    public string ProductName => _productName ?? "";
    public string AssociatedIdLabel => string.IsNullOrWhiteSpace(AssociatedId) ? "" : "Associated ID: " + AssociatedId;
    private int _number;
    private int _quantity = 1;
    // Demo price is generated ONCE when the barcode is scanned, not on each visit.
    public decimal UnitPrice { get; private set; }
    private string _priceSource = "Sample unit price — demo only";
    public string PriceSourceLabel => _priceSource;
    public string UnitPriceLabel => UnitPrice.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    public decimal LineTotal => UnitPrice * Quantity;
    public string LineTotalLabel => LineTotal.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    public int Quantity
    {
        get => _quantity;
        set
        {
            int safeValue = Math.Clamp(value, 1, 999);
            if (_quantity == safeValue) return;
            _quantity = safeValue;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Quantity)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotal)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotalLabel)));
        }
    }
    public string NumberLabel => $"{_number:D2}";
    public event PropertyChangedEventHandler? PropertyChanged;

    public BarcodeCartRow(string code, int number, string? associatedId = null,
                          string? productName = null, decimal? printedPrice = null)
    {
        Code = code;
        AssociatedId = associatedId;
        _productName = productName;
        _number = number;
        UnitPrice = Random.Shared.Next(199, 4999) / 100m;
        if (printedPrice.HasValue && printedPrice.Value >= 0)
        {
            UnitPrice = printedPrice.Value;
            _priceSource = "Printed price — AI extracted, unverified";
        }
    }

    public void UpdateFromApi(CompanyLookupResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.ProductName))
        {
            _productName = response.ProductName;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProductName)));
        }
        if (response.UnitPriceUsd.HasValue)
        {
            UnitPrice = response.UnitPriceUsd.Value;
            _priceSource = "Company API price (USD)";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnitPrice)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnitPriceLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotal)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotalLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PriceSourceLabel)));
        }
    }
    public void SetNumber(int number)
    {
        if (_number == number) return;
        _number = number;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NumberLabel)));
    }
}

public partial class MainPage
{
    private readonly ObservableCollection<BarcodeCartRow> _cartRows = new();

    // Called by the existing MainPage.xaml.cs constructor after InitializeComponent.
    private void InitializeToolModes()
    {
        ModeTitle.Text = "Scanned items";
        ItemsList.ItemsSource = _cartRows;
        RefreshCartCount();
    }

    // The old camera lifecycle still invokes this; automatic scanning is disabled.
    private void StopAutoScan() { }

    private BarcodeCartRow AddBarcodeToCart(string code, string? associatedId = null,
        string? productName = null, decimal? printedPrice = null)
    {
        var row = new BarcodeCartRow(code, _cartRows.Count + 1, associatedId, productName, printedPrice);
        _cartRows.Add(row);
        RefreshCartCount();
        ItemsList.ScrollTo(row, position: ScrollToPosition.End, animate: true);
        return row;
    }

    private async void OnDeleteCartItemClicked(object sender, EventArgs args)
    {
        if (MuseScanLoadingOverlay.IsVisible) return;
        if (sender is not Button { CommandParameter: BarcodeCartRow row } button) return;
        button.IsEnabled = false;
        await UiButtonFeedback.TapAsync((button.Parent as VisualElement) ?? button);
        if (!_cartRows.Remove(row)) return;
        for (int i = 0; i < _cartRows.Count; i++)
            _cartRows[i].SetNumber(i + 1);
        RefreshCartCount();
    }

    private void RefreshCartCount()
    {
        if (_workflowUiReady && _workflowIndex is not (0 or 3))
        {
            CartCountLabel.Text = $"{_workflowResults.Count} results";
            return;
        }
        var total = _cartRows.Count;
        CartCountLabel.Text = total == 1 ? "1 item" : $"{total} items";
    }

    // Keep the original USB camera TextureView capture path unchanged.
    private Task<Bitmap?> CaptureBitmapFromUsbPreviewAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (Preview.Handler?.PlatformView is not TextureView texture || !texture.IsAvailable)
                return (Bitmap?)null;
            return texture.Bitmap;
        });
}
