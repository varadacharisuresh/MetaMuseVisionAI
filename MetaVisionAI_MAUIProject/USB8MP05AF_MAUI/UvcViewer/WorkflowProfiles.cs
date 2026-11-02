using System;
using System.Linq;
namespace UvcViewer;

public sealed record ScanTask(string Id, string Name, string Instructions);
public sealed record ScanRole(string Id, string Name, ScanTask[] Tasks);

public static class WorkflowProfiles
{
    private static readonly ScanTask Barcode = new(
        "barcode", "Read barcode",
        "Point the camera at a barcode and tap SCAN or say 'Scan'. The app reads visible barcode-associated text. For guaranteed encoded values, use a barcode decoder.");

    private static readonly ScanTask CountItems = new(
        "count_items", "Count Items",
        "Place the items together where you like: table, cardboard, ground, carpet, or bed. Keep the items clearly visible and separated; center the group in the camera. The surface and unrelated background are excluded. Only scan NEW groups when adding to the running count; use Reset count to start over.");

    private static readonly ScanTask CountTotal = new(
        "currency_notes", "Count Total",
        "Show the US dollar banknotes clearly so their denominations can be read. The app counts recognized notes and calculates the dollar total. Notes underneath other notes may not be counted. Only scan NEW groups when adding to the total; use Reset count to start over.");

    private static readonly ScanTask VerifyAddress = new(
        "delivery_address", "Verify Delivery Address",
        "Point the camera at the package SHIP TO address and tap SCAN or say 'Scan'. The app checks the destination against the phone's GPS location; a result may require manual verification.");

    // Shared basic tasks, plus role-specific tasks where useful.
    public static readonly ScanRole[] Roles =
    {
        new("cashier", "Cashier", new[] { Barcode, CountItems, CountTotal }),
        new("receptionist", "Receptionist", new[] { Barcode, CountItems }),
        new("delivery", "Delivery", new[] { Barcode, CountItems, VerifyAddress }),
        new("packer", "Packer", new[] { Barcode, CountItems }),
        new("pharmacist", "Pharmacist", new[] { Barcode, CountItems }),
        new("general", "General", new[] { Barcode, CountItems })
    };

    public static ScanRole Role(string id) => Roles.FirstOrDefault(r => r.Id == id) ?? Roles[^1];
    public static ScanTask Task(ScanRole role, string id) => role.Tasks.FirstOrDefault(t => t.Id == id) ?? role.Tasks[0];
}

public static class WorkflowSession
{
    public static string RoleId { get; set; } = "general";
    public static string TaskId { get; set; } = "barcode";
}
