using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace UvcViewer;

// Vision results are suggestions until independently decoded or checked against business data.
public sealed class VisualScanItem
{
    public string? BarcodeCandidate { get; init; }
    public string? PrintedBarcodeText { get; init; }
    public string? AssociatedId { get; init; }
    public string? ProductName { get; init; }
    public decimal? PrintedPrice { get; init; }
    public int Quantity { get; init; } = 1;
    public Dictionary<string, string> Fields { get; init; } = new();
}

public sealed class VisualScanBatch
{
    public List<VisualScanItem> Items { get; } = new();
    public Dictionary<string, string> Address { get; } = new();
    public string? PackingDate { get; private set; }
    public string? ExpiryDate { get; private set; }
    public string? Notes { get; private set; }
    public int? TabletCount { get; private set; }
    public int? ObjectCount { get; private set; }
    public Dictionary<string, int> Bills { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Coins { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static VisualScanBatch Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new FormatException("Muse returned an empty response.");

        // Handles responses that may contain a markdown code fence.
        int first = content.IndexOf('{');
        int last = content.LastIndexOf('}');
        if (first < 0 || last <= first)
            throw new FormatException("Muse did not return a JSON object.");

        using var doc = JsonDocument.Parse(content.Substring(first, last - first + 1));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected a JSON object.");

        var result = new VisualScanBatch
        {
            PackingDate = ReadText(root, "packingDate"),
            ExpiryDate = ReadText(root, "expiryDate"),
            Notes = ReadText(root, "notes"),
            TabletCount = ReadPositiveInt(root, "tabletCount"),
            ObjectCount = ReadPositiveInt(root, "objectCount")
        };
        if (root.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.Object)
            AddFields(address, result.Address);

        if (root.TryGetProperty("money", out var money) && money.ValueKind == JsonValueKind.Object)
        {
            if (money.TryGetProperty("bills", out var bills) && bills.ValueKind == JsonValueKind.Object)
                AddCounts(bills, result.Bills);
            if (money.TryGetProperty("coins", out var coins) && coins.ValueKind == JsonValueKind.Object)
                AddCounts(coins, result.Coins);
        }
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
                    AddFields(f, fields);
                decimal? price = null;
                string? rawPrice = ReadText(item, "pricePrinted");
                if (decimal.TryParse(rawPrice, System.Globalization.NumberStyles.Currency,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
                    price = parsed;

                result.Items.Add(new VisualScanItem
                {
                    BarcodeCandidate = ReadText(item, "barcodeCandidate"),
                    PrintedBarcodeText = ReadText(item, "printedBarcodeText"),
                    AssociatedId = ReadText(item, "associatedId"),
                    ProductName = ReadText(item, "productName"),
                    PrintedPrice = price,
                    Quantity = Math.Clamp(ReadPositiveInt(item, "quantity") ?? 1, 1, 1000),
                    Fields = fields
                });
            }
        }
        return result;
    }

    public static string? ReadText(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value)) return null;
        string? text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("unreadable", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        return text;
    }

    private static int? ReadPositiveInt(JsonElement source, string field)
    {
        string? raw = ReadText(source, field);
        return int.TryParse(raw, out int count) && count >= 0 && count <= 10000 ? count : null;
    }

    private static void AddCounts(JsonElement json, Dictionary<string, int> destination)
    {
        foreach (var prop in json.EnumerateObject())
        {
            if (int.TryParse(prop.Value.ToString(), out int count) && count >= 0 && count <= 10000)
                destination[prop.Name] = count;
        }
    }

    private static void AddFields(JsonElement objectJson, Dictionary<string, string> result)
    {
        foreach (var prop in objectJson.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                string? value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() : prop.Value.GetRawText();
                if (!string.IsNullOrWhiteSpace(value)) result[prop.Name] = value;
            }
        }
    }
}

public sealed class WorkflowResultRow
{
    public string Title { get; init; } = "Scan";
    public string Details { get; init; } = "";
    public string Verdict { get; init; } = "Needs review";
}

public sealed class CompanyLookupResponse
{
    public bool Succeeded { get; init; }
    public string? ProductName { get; init; }
    public decimal? UnitPriceUsd { get; init; }
    public bool? DeliveryMatch { get; init; }
    public bool LocationProvided { get; init; }
    public string? Message { get; init; }
}
