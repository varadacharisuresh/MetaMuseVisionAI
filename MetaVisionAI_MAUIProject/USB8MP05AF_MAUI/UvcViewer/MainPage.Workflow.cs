using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Devices.Sensors;

namespace UvcViewer;

public partial class MainPage
{
    // Kept for compatibility with previously supplied hidden cart partial classes.
    private readonly ObservableCollection<WorkflowResultRow> _workflowResults = new();
    private bool _workflowUiReady;
    private int _workflowIndex;
    private ScanRole CurrentRole => WorkflowProfiles.Role(WorkflowSession.RoleId);
    private ScanTask CurrentTask => WorkflowProfiles.Task(CurrentRole, WorkflowSession.TaskId);
    private readonly Dictionary<string, int> _countedObjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<decimal, int> _countedNotes = new();
    private string? _googleMapsUrl;
    private enum Verdict { Pass, Fail, Review }

    private void OnWorkflowPickerLoaded(object sender, EventArgs e)
    {
        if (_workflowUiReady) return;
        var role = CurrentRole;
        SelectedRoleLabel.Text = role.Name;
        int choice = Array.FindIndex(role.Tasks, t => t.Id == WorkflowSession.TaskId);
        _workflowIndex = choice >= 0 ? choice : 0;
        WorkflowSession.TaskId = role.Tasks[_workflowIndex].Id;
        WorkflowSelectionLabel.Text = role.Tasks[_workflowIndex].Name;
        _workflowUiReady = true;
        UpdateMainOrderResetVisibility();
        UpdateResetButton();
        ResetWorkflowResult();
        RefreshCashierOrderUI();
        RefreshPharmacyCartUI();
    }

    private void OnTaskSelectorClicked(object sender, EventArgs e)
    {
        if (!_workflowUiReady || _museScanGate.CurrentCount == 0) return;
        RefreshTaskOptions();
        TaskChoicesOverlay.IsVisible = true;
    }

    private void OnTaskSelectorCancelClicked(object sender, EventArgs e) =>
        TaskChoicesOverlay.IsVisible = false;

    private void RefreshTaskOptions()
    {
        TaskOptionsPanel.Children.Clear();
        var tasks = CurrentRole.Tasks;
        for (int i = 0; i < tasks.Length; i++)
        {
            var selected = i == _workflowIndex;
            var row = new Border
            {
                BackgroundColor = Color.FromArgb(selected ? "#21496B" : "#172F49"),
                Stroke = new SolidColorBrush(Color.FromArgb(selected ? "#1688FF" : "#3A5876")),
                StrokeThickness = selected ? 1.5 : 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(12) },
                Padding = new Thickness(12, 2)
            };
            var layout = new Grid { HeightRequest = 52 };
            layout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            layout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(32)));
            var name = new Label { Text = tasks[i].Name, TextColor = Colors.White, FontSize = 16,
                                   VerticalOptions = LayoutOptions.Center };
            Grid.SetColumn(name, 0);
            layout.Children.Add(name);
            if (selected)
            {
                var tick = new Label { Text = "✓", TextColor = Color.FromArgb("#6CD6FF"), FontSize = 23,
                                       HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.Center };
                Grid.SetColumn(tick, 1);
                layout.Children.Add(tick);
            }
            var button = new Button { Text = "", CommandParameter = i, BackgroundColor = Colors.Transparent };
            button.Clicked += OnTaskOptionClicked;
            Grid.SetColumnSpan(button, 2);
            layout.Children.Add(button);
            row.Content = layout;
            TaskOptionsPanel.Children.Add(row);
        }
    }

    private void OnTaskOptionClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: int index } ||
            index < 0 || index >= CurrentRole.Tasks.Length || _museScanGate.CurrentCount == 0)
            return;
        _workflowIndex = index;
        string nextTask = CurrentRole.Tasks[index].Id;
        if (!string.Equals(WorkflowSession.TaskId, nextTask, StringComparison.Ordinal))
        {
            _countedObjects.Clear();
            _countedNotes.Clear();
        }
        WorkflowSession.TaskId = nextTask;
        ClearPharmacyPending();
        WorkflowSelectionLabel.Text = CurrentRole.Tasks[index].Name;
        TaskChoicesOverlay.IsVisible = false;
        UpdateResetButton();
        ResetWorkflowResult();
        RefreshPharmacyCartUI();
    }

    private void OnChangeRoleClicked(object sender, EventArgs e)
    {
        if (_museScanGate.CurrentCount == 0) return;
        _listenRequested = false;
        StopVoiceRecognition();
        if (Window != null) Window.Page = new RoleSelectionPage();
    }

    private void UpdateResetButton()
    {
        // Pharmacist displays each scan separately and does not need a running-count reset.
        ResetSessionButton.IsVisible = !IsPharmacistRole &&
            (CurrentTask.Id is "count_items" or "currency_notes");
        ResetSessionButton.Text = "Reset count";
        ManualScanActionLabel.Text = CurrentTask.Id switch
        {
            "count_items" => "COUNT ITEMS",
            "currency_notes" => "COUNT TOTAL",
            _ => "SCAN"
        };
    }

    private void OnResetSessionClicked(object sender, EventArgs e)
    {
        if (_museScanGate.CurrentCount == 0) return;
        _countedObjects.Clear();
        _countedNotes.Clear();
        ClearPharmacyPending();
        ResetWorkflowResult();
    }

    private void ResetWorkflowResult()
    {
        ResultReadyPrompt.IsVisible = true;
        _googleMapsUrl = null;
        DestinationMapsLink.IsVisible = false;
        ResultTotalLabel.Text = "";
        ResultTotalLabel.IsVisible = false;
        ResultHeadingLabel.Text = "";
        ResultHeadingLabel.IsVisible = false;
        ResultStatusLabel.Text = "";
        ResultStatusLabel.IsVisible = false;
        ResultDetailsLabel.Text = "";
        ResultDetailsLabel.IsVisible = false;
        CountedItemsList.Children.Clear();
        CountedItemsList.IsVisible = false;
        ResultVerdictLabel.Text = "";
        ResultVerdictBorder.IsVisible = false;
        ResultNoteLabel.Text = "";
        ResultNoteLabel.IsVisible = false;
        VerdictCheckGlyph.IsVisible = VerdictErrorGlyph.IsVisible = VerdictReviewGlyph.IsVisible = false;
    }

    private void OnWorkflowInfoClicked(object sender, EventArgs e)
    {
        InfoRoleLabel.Text = CurrentRole.Name;
        InfoTaskLabel.Text = CurrentTask.Name;
        InfoWhatLabel.Text = CurrentTask.Id switch
        {
            "barcode" => "Reads visible text associated with barcodes in the camera image.",
            "count_items" => "Estimates visible objects in a presented group on any supporting surface, not just a table.",
            "currency_notes" => "Counts legible USD banknotes and calculates their total value.",
            "delivery_address" => "Compares the package destination with the phone's approximate GPS location.",
            _ => "Reads an image"
        };
        InfoHowLabel.Text = CurrentTask.Instructions + " Use the on-screen button or say 'Scan'.";
        InfoCautionLabel.Text = CurrentTask.Id switch
        {
            "barcode" => "AI reading of printed digits is not the same as decoding the barcode bars. Verify before critical use.",
            "count_items" => "AI may count unrelated objects if the intended group is not clear. Overlapping items may be missed; repeated photos of the same group will double-count it.",
            "currency_notes" => "AI can misread denominations or miss overlapping banknotes. Do not rely on an unverified amount for real cash transactions.",
            _ => "GPS can indicate a likely building match, but cannot confirm a particular door or apartment."
        };
        WorkflowInfoOverlay.IsVisible = true;
    }

    private void OnWorkflowInfoCloseClicked(object sender, EventArgs e) =>
        WorkflowInfoOverlay.IsVisible = false;

    // A small task-specific JSON schema is easier to parse and less likely to
    // cause hallucinated fields than the previous all-industries schema.
    private string BuildWorkflowPrompt()
    {
        const string noGuess = "Do not guess missing or hidden data. Return valid JSON only, no markdown. ";
        return CurrentTask.Id switch
        {
            "barcode" => noGuess +
                "Read human-readable text directly printed next to each visible barcode. " +
                "Do not return unrelated label numbers, item descriptions or tracking fields. " +
                "If no legible text exists, return items=[]. Return JSON: " +
                "{\"items\":[{\"printedBarcodeText\":\"value\",\"barcodeCandidate\":null}]}",
            "count_items" => noGuess +
                "Count the CLEARLY VISIBLE physical objects belonging to the group intentionally presented in the central foreground of the image. " +
                "Items may rest on ANY surface: table, cardboard, floor, carpet, bed, shelf, tray or in a container. " +
                "DO NOT require a tabletop or a boundary. Do not count the supporting surface or container itself, " +
                "people, hands, furniture, or unrelated background objects. Count pens, boxes, bottles, tablets, keys, " +
                "scissors, pillows, bags, or any other distinct object types, grouped by productName. " +
                "If a group is clear, include each item type and positive integer quantity; if unclear, return an empty list. " +
                "Return ONLY JSON: {\"items\":[{\"productName\":\"Pen\",\"quantity\":3}]}",
            "currency_notes" => noGuess +
                "Count only clearly visible UNITED STATES DOLLAR PAPER BANKNOTES that have readable denomination. " +
                "Return denominations 1,2,5,10,20,50,100 as integer quantities in money.bills. " +
                "Ignore coins and other objects. Never guess denominations of hidden or overlapped notes. " +
                "Return ONLY JSON: {\"money\":{\"bills\":{\"20\":3,\"10\":2}}}",
            "delivery_address" => noGuess +
                "Read the SHIP TO / recipient address only, not the return address. " +
                "Return address.street INCLUDING the building number, address.unit if visible, " +
                "city, state, postalCode, country. Unreadable components should be null. " +
                "Return JSON: {\"address\":{\"street\":null,\"unit\":null,\"city\":null,\"state\":null,\"postalCode\":null,\"country\":null}}",
            _ => noGuess + "Return JSON: {}"
        };
    }

    private Task<(bool Added, string? Error)> ApplyWorkflowResultAsync(VisualScanBatch batch) =>
        CurrentTask.Id switch
        {
            "barcode" => ApplyBarcodeAsync(batch),
            "count_items" => ApplyCountItemsAsync(batch),
            "currency_notes" => ApplyCurrencyNotesAsync(batch),
            "delivery_address" => ApplyDeliveryAsync(batch),
            _ => Task.FromResult((false, (string?)"Unsupported task"))
        };

    private async Task<(bool Added, string? Error)> ApplyBarcodeAsync(VisualScanBatch batch)
    {
        var values = batch.Items
            .Select(i => i.PrintedBarcodeText ?? i.BarcodeCandidate)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Where(PlausibleCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (IsPharmacistRole)
            await MainThread.InvokeOnMainThreadAsync(() => StagePharmacyBarcodes(values));
        if (values.Length > 0 && IsCashierRole)
            await MainThread.InvokeOnMainThreadAsync(() => AddCashierBarcodeValues(values));
        await ShowResultAsync("", values.Length > 0 ? string.Join("\n", values) : "No readable barcode", "", Verdict.Review, "");
        return (values.Length > 0, null);
    }

    private async Task<(bool Added, string? Error)> ApplyCountItemsAsync(VisualScanBatch batch)
    {
        var objects = batch.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.ProductName) && i.Quantity > 0 && i.Quantity <= 1000)
            .Where(i => !IsSupportSurface(i.ProductName!))
            .GroupBy(i => i.ProductName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Name = g.Key, Count = g.Sum(i => i.Quantity) })
            .ToArray();
        if (IsPharmacistRole)
        {
            // For the pharmacist, each scan shows NEW items only. The cart
            // accumulates ONLY on an explicit plus tap, preventing 8 + 10 = 18.
            _countedObjects.Clear();
            await MainThread.InvokeOnMainThreadAsync(() =>
                StagePharmacyCountedItems(objects.Select(o => (o.Name, o.Count))));
        }
        if (objects.Length == 0)
        {
            await ShowResultAsync("", "No clearly countable items found", "", Verdict.Review, "");
            return (false, null);
        }
        foreach (var obj in objects)
            _countedObjects[obj.Name] = _countedObjects.GetValueOrDefault(obj.Name) + obj.Count;
        if (IsCashierRole)
            await MainThread.InvokeOnMainThreadAsync(() =>
                AddCashierCountedItems(objects.Select(o => (o.Name, o.Count))));
        string list = string.Join("\n", _countedObjects.OrderBy(x => x.Key)
            .Select(x => $"{x.Key}  × {x.Value}"));
        await ShowResultAsync("", list, "", Verdict.Review, "");
        return (true, null);
    }

    private static bool IsSupportSurface(string name)
    {
        // Don't count a support surface as an item. This does not prevent Muse
        // counting unrelated background objects when the scene is ambiguous.
        string value = name.Trim().ToLowerInvariant();
        return value is "table" or "tabletop" or "floor" or "ground" or "carpet" or "rug" or "bed" or
            "mattress" or "cardboard surface" or "cardboard sheet" or "support surface" or
            "background" or "person" or "hand" or "hands" or "shelf" or "tray";
    }

    private async Task<(bool Added, string? Error)> ApplyCurrencyNotesAsync(VisualScanBatch batch)
    {
        decimal[] supported = { 1m, 2m, 5m, 10m, 20m, 50m, 100m };
        int added = 0;
        foreach (var entry in batch.Bills)
        {
            if (!decimal.TryParse(entry.Key.Trim().TrimStart('$'), NumberStyles.Number,
                CultureInfo.InvariantCulture, out decimal denomination) ||
                !supported.Contains(denomination) || entry.Value <= 0 || entry.Value > 1000)
                continue;
            _countedNotes[denomination] = _countedNotes.GetValueOrDefault(denomination) + entry.Value;
            added += entry.Value;
        }
        if (added == 0)
        {
            await ShowResultAsync("", "No readable currency notes found", "", Verdict.Review, "");
            return (false, null);
        }
        string breakdown = string.Join("\n", _countedNotes.OrderByDescending(x => x.Key)
            .Select(x => $"{x.Value} × {x.Key.ToString("C2", CultureInfo.GetCultureInfo("en-US"))}"));
        int totalNotes = _countedNotes.Values.Sum();
        await ShowResultAsync("", $"{totalNotes} notes\n{breakdown}", "", Verdict.Review, "");
        return (true, null);
    }

    private async Task<(bool Added, string? Error)> ApplyDeliveryAsync(VisualScanBatch batch)
    {
        var check = await CheckDeliveryLocationAsync(batch.Address);
        Verdict signal = check.Confirmed ? Verdict.Pass :
            check.Status == "Mismatch" ? Verdict.Fail : Verdict.Review;
        string status = check.Confirmed ? "Location appears correct" :
            check.Status == "Mismatch" ? "You are not at the correct delivery location" :
            "Cannot confirm location — verify manually";
        await ShowResultAsync("Verify address", check.Details, status, signal, "");
        string destination = string.Join(", ", new[]
        {
            ValueOr(batch.Address, "street", ""), ValueOr(batch.Address, "unit", ""),
            ValueOr(batch.Address, "city", ""), ValueOr(batch.Address, "state", ""),
            ValueOr(batch.Address, "postalCode", ""), ValueOr(batch.Address, "country", "")
        }.Where(v => !string.IsNullOrWhiteSpace(v)));
        _googleMapsUrl = check.Status == "Mismatch" && destination.Length > 0
            ? "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(destination)
            : null;
        await MainThread.InvokeOnMainThreadAsync(() =>
            DestinationMapsLink.IsVisible = !string.IsNullOrWhiteSpace(_googleMapsUrl));
        return (check.Confirmed, null);
    }

    private async void OnGoogleMapsClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_googleMapsUrl)) return;
        try { await Launcher.Default.OpenAsync(new Uri(_googleMapsUrl)); }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("MuseScan", "Cannot open Google Maps: " + ex);
            await ShowResultAsync("Verify address", "Could not open Google Maps.",
                "Map unavailable", Verdict.Review, "");
        }
    }

    private Task ShowResultAsync(string heading, string details, string verdict, Verdict signal, string note) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            ResultReadyPrompt.IsVisible = false;
            DestinationMapsLink.IsVisible = false;
            CountedItemsList.IsVisible = false;
            bool isError = verdict == "Scan failed" || verdict == "Map unavailable";
            if (IsPharmacistRole && isError)
                ClearPharmacyPending();
            if (CurrentTask.Id == "barcode" && !isError)
            {
                ResultTotalLabel.IsVisible = false;
                ResultHeadingLabel.IsVisible = false;
                ResultStatusLabel.IsVisible = false;
                ResultVerdictBorder.IsVisible = false;
                ResultNoteLabel.IsVisible = false;
                ResultDetailsLabel.Text = details;
                ResultDetailsLabel.IsVisible = true;
                return;
            }
            bool isCounting = CurrentTask.Id == "count_items" && !isError;
            bool isMoney = CurrentTask.Id == "currency_notes" && !isError;
            ResultTotalLabel.Text = isCounting && _countedObjects.Count > 0
                ? $"{_countedObjects.Values.Sum()} items (estimated)" :
                isMoney && _countedNotes.Count > 0
                ? _countedNotes.Sum(x => x.Key * x.Value).ToString("C2", CultureInfo.GetCultureInfo("en-US")) : "";
            ResultTotalLabel.IsVisible = !string.IsNullOrWhiteSpace(ResultTotalLabel.Text);
            ResultHeadingLabel.Text = isCounting ? "Counted items" : isMoney ? "Total currency notes" : heading;
            ResultHeadingLabel.IsVisible = isCounting || isMoney || !string.IsNullOrWhiteSpace(heading);
            ResultStatusLabel.IsVisible = false;
            // Count results are displayed as visible rows, not one clipped text label.
            // The UI scrolls only these rows; the action buttons are fixed below it.
            CountedItemsList.Children.Clear();
            CountedItemsList.IsVisible = isCounting && _countedObjects.Count > 0 && !IsCashierRole;
            if (isCounting && !IsCashierRole)
            {
                foreach (var entry in _countedObjects.OrderBy(e => e.Key))
                {
                    var itemRow = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitionCollection
                        {
                            new ColumnDefinition { Width = GridLength.Star },
                            new ColumnDefinition { Width = GridLength.Auto }
                        },
                        ColumnSpacing = 12,
                        Padding = new Thickness(12, 9),
                        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#203A50")
                    };
                    var itemName = new Label
                    {
                        Text = entry.Key,
                        TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#E4F2FF"),
                        FontSize = 15,
                        VerticalOptions = LayoutOptions.Center,
                        LineBreakMode = LineBreakMode.WordWrap
                    };
                    var itemQty = new Label
                    {
                        Text = $"× {entry.Value}",
                        TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#8BDBFF"),
                        FontSize = 17,
                        FontAttributes = FontAttributes.Bold,
                        VerticalOptions = LayoutOptions.Center,
                        HorizontalTextAlignment = TextAlignment.End
                    };
                    Grid.SetColumn(itemQty, 1);
                    itemRow.Children.Add(itemName);
                    itemRow.Children.Add(itemQty);
                    CountedItemsList.Children.Add(itemRow);
                }
            }
            ResultDetailsLabel.Text = isCounting && _countedObjects.Count > 0 ? "" : details;
            ResultDetailsLabel.IsVisible = !string.IsNullOrWhiteSpace(ResultDetailsLabel.Text);
            ResultVerdictLabel.Text = verdict;
            ResultVerdictBorder.IsVisible = !isCounting && !isMoney && !string.IsNullOrWhiteSpace(verdict);
            VerdictCheckGlyph.IsVisible = signal == Verdict.Pass;
            VerdictErrorGlyph.IsVisible = signal == Verdict.Fail;
            VerdictReviewGlyph.IsVisible = signal == Verdict.Review;
            string bg = signal switch { Verdict.Pass => "#123D33", Verdict.Fail => "#51242E", _ => "#1C334C" };
            string stroke = signal switch { Verdict.Pass => "#257E62", Verdict.Fail => "#AE5262", _ => "#3E7095" };
            ResultVerdictBorder.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb(bg);
            ResultVerdictBorder.Stroke = new SolidColorBrush(Microsoft.Maui.Graphics.Color.FromArgb(stroke));
            ResultNoteLabel.Text = note;
            ResultNoteLabel.IsVisible = !string.IsNullOrWhiteSpace(note);
        });

    private sealed record DeliveryAssessment(
        bool Confirmed, string Status, string Details, string Verdict,
        string Note, string ErrorMessage);

    private static string ValueOr(Dictionary<string, string> fields, string key, string fallback = "Not found") =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim() : fallback;

    private static string StreetNumber(string text)
    {
        var match = Regex.Match(text, @"^\s*(\d+[A-Za-z]?)\b");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
    }

    private static string NormalizeStreet(string street)
    {
        street = Regex.Replace(street, @"^\s*\d+[A-Za-z]?\s+", "");
        street = Regex.Replace(street.ToLowerInvariant(), @"\b(st|street)\b", "street");
        street = Regex.Replace(street, @"\b(rd|road)\b", "road");
        street = Regex.Replace(street, @"\b(ave|avenue)\b", "avenue");
        street = Regex.Replace(street, @"\b(blvd|boulevard)\b", "boulevard");
        return Regex.Replace(street, @"[^a-z0-9]", "");
    }

    private static string NormalizePostal(string postal) =>
        Regex.Replace(postal.ToUpperInvariant(), @"[^A-Z0-9]", "");

    private async Task<DeliveryAssessment> CheckDeliveryLocationAsync(Dictionary<string, string> address)
    {
        string street = ValueOr(address, "street", "");
        string unit = ValueOr(address, "unit", "");
        string city = ValueOr(address, "city", "");
        string state = ValueOr(address, "state", "");
        string zip = ValueOr(address, "postalCode", "");
        string country = ValueOr(address, "country", "");
        string destination = string.Join(", ", new[] { street, unit, city, state, zip, country }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
        string details = "PACKAGE DESTINATION\n" +
            (destination.Length == 0 ? "Address not readable" : destination);
        const string manual = "CHECK — Verify delivery location manually";
        const string caution = "GPS and geocoding are approximate; confirm the building and apartment/unit before completing delivery.";

        if (StreetNumber(street).Length == 0 || city.Length == 0 || zip.Length == 0)
            return new(false, "Review", details + "\n\nCURRENT LOCATION\nNot checked",
                manual, "Package address must include a house number, city and postal code.",
                "The delivery label is incomplete or unreadable.");

        try
        {
            var permission = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (permission != PermissionStatus.Granted)
                permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            if (permission != PermissionStatus.Granted)
                return new(false, "Review", details + "\n\nCURRENT LOCATION\nPermission denied",
                    manual, "Allow precise location access to compare delivery addresses.",
                    "Location permission is required for delivery verification.");

            Location? gps = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(12)));
            if (gps == null)
                return new(false, "Review", details + "\n\nCURRENT LOCATION\nGPS unavailable",
                    manual, "Enable location services and try again.",
                    "Current GPS position is unavailable.");

            double accuracy = gps.Accuracy ?? double.PositiveInfinity;
            IEnumerable<Placemark>? reverse = await Geocoding.Default.GetPlacemarksAsync(
                gps.Latitude, gps.Longitude);
            Placemark? currentPlace = reverse?.FirstOrDefault();

            string currentAddress = currentPlace == null ? "Address lookup unavailable" :
                string.Join(", ", new[]
                {
                    $"{currentPlace.SubThoroughfare} {currentPlace.Thoroughfare}".Trim(),
                    currentPlace.Locality, currentPlace.AdminArea,
                    currentPlace.PostalCode, currentPlace.CountryName
                }.Where(v => !string.IsNullOrWhiteSpace(v)));

            details += "\n\nCURRENT LOCATION (PHONE GPS)\n" + currentAddress +
                "\nGPS accuracy: " + (double.IsInfinity(accuracy) ? "unknown" :
                    $"±{accuracy:0} m");

            // Geocode the *full* destination; don't infer correctness from a shared ZIP/city.
            IEnumerable<Location>? matches = await Geocoding.Default.GetLocationsAsync(
                string.Join(", ", new[] { street, city, state, zip, country }
                    .Where(v => !string.IsNullOrWhiteSpace(v))));
            Location? destinationGps = matches?.FirstOrDefault();
            if (destinationGps == null)
                return new(false, "Review", details, manual,
                    "The package address could not be geocoded. " + caution,
                    "Could not resolve the destination address.");

            double meters = Location.CalculateDistance(gps, destinationGps,
                DistanceUnits.Kilometers) * 1000d;
            details += $"\nDistance from destination: approximately {meters:0} m";

            // Reject mock or very imprecise GPS readings. GPS alone never proves
            // a particular door, floor, apartment or unit number.
            if (gps.IsFromMockProvider || accuracy > 100 || currentPlace == null)
                return new(false, "Review", details, manual,
                    "Location accuracy or address lookup is insufficient. " + caution,
                    "Location cannot be confirmed reliably.");

            string expectedNumber = StreetNumber(street);
            bool numberMatches = expectedNumber.Equals(
                (currentPlace.SubThoroughfare ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            bool roadMatches = NormalizeStreet(street) ==
                NormalizeStreet(currentPlace.Thoroughfare ?? "");
            bool postalMatches = NormalizePostal(zip) ==
                NormalizePostal(currentPlace.PostalCode ?? "");

            // If the destination is clearly far away and GPS is reasonably precise,
            // report a likely mismatch, but still require a human confirmation.
            if (accuracy <= 75 && meters >= 300)
                return new(false, "Mismatch", details,
                    "NO — You appear to be at the wrong delivery location",
                    "Destination is significantly farther away than the device location accuracy. " + caution,
                    "Possible wrong delivery location.");

            if (accuracy <= 50 && meters <= 75 &&
                numberMatches && roadMatches && postalMatches)
                return new(true, "Likely match", details,
                    "YES — You appear to be at the correct delivery location",
                    caution, "");

            return new(false, "Review", details, manual,
                "Street number, street name, postal code or GPS accuracy could not all be confirmed. " + caution,
                "Delivery location needs manual verification.");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("MuseScan", "Location check failed: " + ex);
            return new(false, "Review", details + "\n\nCURRENT LOCATION\nUnavailable",
                manual, "Could not complete location comparison. Check GPS/network access.",
                "Location check failed; verify manually.");
        }
    }
}
