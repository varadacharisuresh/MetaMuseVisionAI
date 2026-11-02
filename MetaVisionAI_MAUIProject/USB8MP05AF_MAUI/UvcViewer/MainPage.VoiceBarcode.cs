using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Android.Media;
using Android.Util;
using Microsoft.Maui.ApplicationModel;

namespace UvcViewer;

// Only entry point: OnRecognitionResult -> exact word "Scan" -> this method.
// One USB preview frame is sent to Muse. No pictures/videos are saved.
public partial class MainPage
{
    private static readonly HttpClient ScanMuseHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };
    private const string ScanMuseEndpoint = "https://api.meta.ai/v1/chat/completions";
    private const string ScanMuseModel = "muse-spark-1.3";

    private readonly SemaphoreSlim _museScanGate = new(1, 1);
    private ToneGenerator? _scanTone;

    private async Task ScanMuseBarcodeAsync()
    {
        // Ignore a second 'Scan' while a request is running; don't queue frames.
        if (!await _museScanGate.WaitAsync(0)) return;
        bool success = false;
        bool resultProcessed = false;
        string? error = null;
        try
        {
            // Show feedback immediately, even when capture or configuration fails.
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                // Hide the pre-scan message as soon as a real Scan begins.
                // The existing Muse request and voice/manual triggers are unchanged.
                ResultReadyPrompt.IsVisible = false;
                MainSplitGrid.IsEnabled = false;
                SettingsOverlay.IsEnabled = false;
                VoiceFab.IsEnabled = false;
                MuseScanLoadingOverlay.IsVisible = true;
                MuseScanLoadingSpinner.IsRunning = true;
            });
            // Allow the UI to paint the blocking overlay before doing I/O.
            await Task.Delay(80);
            global::Android.Util.Log.Info("MuseScan", "Scan started; capturing USB frame");
            // Do NOT reject a visibly working camera solely because a
            // separate streaming-status flag is momentarily out of sync.
            // CaptureBitmapFromUsbPreviewAsync checks the real TextureView frame.

            string key = (MuseDemoConfiguration.ApiKey ?? "").Trim();
            if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(7).Trim();
            if (string.IsNullOrWhiteSpace(key) ||
                key == "PASTE_YOUR_META_API_KEY_HERE")
                throw new InvalidOperationException("Muse API key not configured.");

            using var bitmap = await CaptureBitmapFromUsbPreviewAsync();
            if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0)
                throw new InvalidOperationException("USB camera image is unavailable.");
            global::Android.Util.Log.Info("MuseScan",
                $"Captured USB frame {bitmap.Width}x{bitmap.Height}");

            using var jpeg = new MemoryStream();
            if (!bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 95, jpeg))
                throw new InvalidOperationException("Could not encode preview frame.");

            // Workflow determines what Muse extracts, not the spoken command.
            string prompt = BuildWorkflowPrompt();

            var payload = new
            {
                model = ScanMuseModel,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new
                                { url = "data:image/jpeg;base64," +
                                        Convert.ToBase64String(jpeg.ToArray()) } }
                        }
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, ScanMuseEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload),
                    System.Text.Encoding.UTF8,
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"))
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            global::Android.Util.Log.Info("MuseScan", "Sending frame to Muse AI");
            using var response = await ScanMuseHttp.SendAsync(request, timeout.Token);
            global::Android.Util.Log.Info("MuseScan",
                $"Muse AI HTTP status: {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Muse AI returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

            string apiJson = await response.Content.ReadAsStringAsync(timeout.Token);
            string? aiContent = ExtractMuseMessage(apiJson);
            VisualScanBatch extracted = VisualScanBatch.Parse(aiContent);
            var outcome = await ApplyWorkflowResultAsync(extracted);
            resultProcessed = true;
            success = outcome.Added;
            // A mismatch / review is displayed in the lower result panel.
            // Do not show an extra popup for a processed negative result.
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn("MuseScan", "Scan failed: " + ex);
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                MuseScanLoadingSpinner.IsRunning = false;
                MuseScanLoadingOverlay.IsVisible = false;
                MainSplitGrid.IsEnabled = true;
                SettingsOverlay.IsEnabled = true;
                VoiceFab.IsEnabled = true;
                PlayScanTone(success);
            });
            _museScanGate.Release();
            // User controls the microphone. Return to recognition after every scan
            // (success or error), but never start while Muse is processing.
            if (_scanPageLoaded && _listenRequested)
                _ = StartVoiceRecognitionAsync();
        }
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowResultAsync(CurrentTask.Name, error,
                "Scan failed", Verdict.Fail,
                "Check the USB camera, network connection and Muse API key, then scan again.");
        }
        else if (!resultProcessed)
        {
            await ShowResultAsync(CurrentTask.Name, "No result was returned for this task.",
                "No result", Verdict.Review, "Try scanning again with a clearer image.");
        }
    }

    private static string? ExtractMuseMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
            return null;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind == JsonValueKind.Array)
            return string.Join("\n", content.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object &&
                            p.TryGetProperty("text", out var t) &&
                            t.ValueKind == JsonValueKind.String)
                .Select(p => p.GetProperty("text").GetString()));
        return null;
    }

    private static string[] ParseBarcodeValues(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return Array.Empty<string>();
        int first = response.IndexOf('{');
        int last = response.LastIndexOf('}');
        if (first < 0 || last <= first) return Array.Empty<string>();
        try
        {
            using var json = JsonDocument.Parse(response.Substring(first, last - first + 1));
            if (!json.RootElement.TryGetProperty("barcodes", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var values = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                string? code = null;
                if (item.ValueKind == JsonValueKind.String)
                    code = item.GetString();
                else if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    // Accept the previous response format too, but reject
                    // explicitly low-confidence guesses.
                    if (item.TryGetProperty("confidence", out var confidence) &&
                        confidence.ValueKind == JsonValueKind.String &&
                        !string.Equals(confidence.GetString(), "HIGH",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    code = value.GetString();
                }

                code = code?.Trim();
                if (code != null && PlausibleCode(code)) values.Add(code);
            }
            return values.ToArray();
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static bool PlausibleCode(string code)
    {
        if (code.Length < 3 || code.Length > 1024 ||
            string.Equals(code, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "UNREADABLE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "NONE", StringComparison.OrdinalIgnoreCase) ||
            code.Any(c => char.IsControl(c)))
            return false;

        // A checksum can reject SOME AI transcription errors for GS1/UPC/EAN.
        if (code.All(c => c is >= '0' and <= '9') &&
            (code.Length == 8 || code.Length == 12 || code.Length == 13 ||
             code.Length == 14 || code.Length == 18))
            return ValidGs1CheckDigit(code);
        return true;
    }

    private static bool ValidGs1CheckDigit(string digits)
    {
        int sum = 0;
        bool weight3 = true;
        for (int i = digits.Length - 2; i >= 0; --i)
        {
            sum += (digits[i] - '0') * (weight3 ? 3 : 1);
            weight3 = !weight3;
        }
        return digits[^1] - '0' == (10 - sum % 10) % 10;
    }

    private void PlayScanTone(bool success)
    {
        try
        {
            _scanTone ??= new ToneGenerator(Android.Media.Stream.Music, 100);
            _scanTone.StopTone();
            _scanTone.StartTone(success ? Tone.PropAck : Tone.PropNack,
                success ? 650 : 950);
        }
        catch (Exception ex)
        {
            Log.Warn("MuseScan", "Tone failed: " + ex.Message);
        }
    }

    private void ReleaseScanTone()
    {
        try { _scanTone?.Release(); } catch { }
        _scanTone = null;
    }
}
