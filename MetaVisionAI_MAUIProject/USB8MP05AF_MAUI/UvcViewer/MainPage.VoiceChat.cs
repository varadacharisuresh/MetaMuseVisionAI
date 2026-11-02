using System;
using System.Linq;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Speech;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace UvcViewer;

// Contextual voice command: "Count Items", "Count Total", or "Scan".
// Backward-compatible "Scan" works for any task.
// The user explicitly starts/stops listening; there is no chat/history.
public partial class MainPage
{
    private SpeechRecognizer? _speechRecognizer;
    private VoiceRecognitionListener? _voiceListener;
    private Window? _voiceWindow;
    private bool _scanPageLoaded;
    private bool _listenRequested;
    private bool _listenEnabled;
    private bool _startPending;
    private int _listenGeneration;
    private int _speechErrorCount;

    private void OnScanPageLoaded(object sender, EventArgs e)
    {
        if (_scanPageLoaded) return;
        _scanPageLoaded = true;
        UpdateListenButton();
        _voiceWindow = Window;
        if (_voiceWindow != null)
        {
            _voiceWindow.Stopped += OnVoiceWindowStopped;
            _voiceWindow.Resumed += OnVoiceWindowResumed;
        }
    }

    private void OnScanPageUnloaded(object sender, EventArgs e)
    {
        _scanPageLoaded = false;
        _listenRequested = false;
        if (_voiceWindow != null)
        {
            _voiceWindow.Stopped -= OnVoiceWindowStopped;
            _voiceWindow.Resumed -= OnVoiceWindowResumed;
            _voiceWindow = null;
        }
        StopVoiceRecognition();
        ReleaseScanTone();
        UpdateListenButton();
    }

    private void OnVoiceWindowStopped(object? sender, EventArgs e) => StopVoiceRecognition();

    private void OnVoiceWindowResumed(object? sender, EventArgs e)
    {
        if (_scanPageLoaded && _listenRequested)
            _ = StartVoiceRecognitionAsync();
    }

    private void OnToggleListeningClicked(object sender, EventArgs e)
    {
        if (_listenRequested)
        {
            _listenRequested = false;
            StopVoiceRecognition();
            UpdateListenButton();
            return;
        }

        if (MuseScanLoadingOverlay.IsVisible) return;
        _listenRequested = true;
        UpdateListenButton();
        _ = StartVoiceRecognitionAsync();
    }

    private void UpdateListenButton()
    {
        VoiceFabBackground.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb(
            _listenRequested ? "#B93450" : "#246BA5");
        var phrase = CurrentTask.Id switch
        {
            "count_items" => "Count Items",
            "currency_notes" => "Count Total",
            _ => "Scan"
        };
        SemanticProperties.SetDescription(ListenButton,
            _listenRequested ? $"Stop listening for {phrase}" : $"Start listening for {phrase}");
    }

    private async Task StartVoiceRecognitionAsync()
    {
        if (!_scanPageLoaded || !_listenRequested || _listenEnabled || _startPending) return;
        _startPending = true;
        try
        {
            var permission = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (permission != PermissionStatus.Granted)
                permission = await Permissions.RequestAsync<Permissions.Microphone>();

            if (!_scanPageLoaded || !_listenRequested) return;
            if (permission != PermissionStatus.Granted)
            {
                ShowScanProblem("Microphone permission is required.");
                _listenRequested = false;
                UpdateListenButton();
                return;
            }

            var activity = Platform.CurrentActivity;
            if (activity == null || !SpeechRecognizer.IsRecognitionAvailable(activity))
            {
                ShowScanProblem("Speech recognition is unavailable. Check Android speech services and the manifest RecognitionService query.");
                _listenRequested = false;
                UpdateListenButton();
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (!_scanPageLoaded || !_listenRequested) return;
                StopVoiceRecognition();
                _speechRecognizer = SpeechRecognizer.CreateSpeechRecognizer(activity);
                _voiceListener = new VoiceRecognitionListener(this);
                _speechRecognizer.SetRecognitionListener(_voiceListener);
                _listenEnabled = true;
                _speechErrorCount = 0;
                global::Android.Util.Log.Info("MuseScan", "Voice recognizer started");
                StartNextRecognitionSession();
            });
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("MuseScan", "Cannot start recognition: " + ex);
            ShowScanProblem("Could not start microphone recognition.");
            _listenRequested = false;
            StopVoiceRecognition();
            UpdateListenButton();
        }
        finally
        {
            _startPending = false;
        }
    }

    private void StopVoiceRecognition()
    {
        _listenEnabled = false;
        ++_listenGeneration; // Invalidates any scheduled restarts.
        try { _speechRecognizer?.Cancel(); } catch { }
        try { _speechRecognizer?.Destroy(); } catch { }
        _speechRecognizer = null;
        _voiceListener = null;
    }

    private void StartNextRecognitionSession()
    {
        if (!_listenEnabled || !_listenRequested || !_scanPageLoaded ||
            _speechRecognizer == null) return;
        try
        {
            using var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
            intent.PutExtra(RecognizerIntent.ExtraLanguageModel,
                RecognizerIntent.LanguageModelFreeForm);
            intent.PutExtra(RecognizerIntent.ExtraLanguage, "en-US");
            intent.PutExtra(RecognizerIntent.ExtraPartialResults, false);
            intent.PutExtra(RecognizerIntent.ExtraMaxResults, 5);
            global::Android.Util.Log.Debug("MuseScan", "Listening for Scan");
            _speechRecognizer.StartListening(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("MuseScan", "Recognition session failed: " + ex.Message);
            QueueNextRecognitionSession(1600);
        }
    }

    private void QueueNextRecognitionSession(int delayMs)
    {
        if (!_listenEnabled || !_listenRequested || !_scanPageLoaded) return;
        int version = ++_listenGeneration;
        _ = RestartAfterDelayAsync(version, delayMs);
    }

    private async Task RestartAfterDelayAsync(int version, int delayMs)
    {
        await Task.Delay(delayMs);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (!_listenEnabled || !_listenRequested || !_scanPageLoaded ||
                version != _listenGeneration) return;
            StartNextRecognitionSession();
        });
    }

    private bool IsScanCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string cleaned = text.Trim().TrimEnd('.', '!', '?', ',', '。').Trim();
        // Preserve the original Scan command for users accustomed to it,
        // while accepting the natural command for the selected task.
        if (string.Equals(cleaned, "Scan", StringComparison.OrdinalIgnoreCase)) return true;
        return CurrentTask.Id switch
        {
            "count_items" => string.Equals(cleaned, "Count items", StringComparison.OrdinalIgnoreCase),
            "currency_notes" => string.Equals(cleaned, "Count total", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private void OnRecognitionResult(Bundle? bundle)
    {
        if (!_listenEnabled || !_listenRequested || !_scanPageLoaded) return;
        _speechErrorCount = 0;
        var results = bundle?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
        if (results != null && results.Any(IsScanCommand))
        {
            global::Android.Util.Log.Info("MuseScan", "Scan command recognized");
            // Pause recognition during AI processing. It is resumed afterward
            // if the microphone has not been turned off.
            StopVoiceRecognition();
            _ = ScanMuseBarcodeAsync();
            return;
        }
        QueueNextRecognitionSession(900);
    }

    private void OnRecognitionError(SpeechRecognizerError error)
    {
        if (!_listenEnabled || !_listenRequested) return;
        int code = (int)error;
        // 6 = speech timeout and 7 = no match. These are normal when idle,
        // so do not treat repeated silence as a broken recognition service.
        if (code == 6 || code == 7)
        {
            _speechErrorCount = 0;
            QueueNextRecognitionSession(850);
            return;
        }

        _speechErrorCount = Math.Min(_speechErrorCount + 1, 8);
        int delay = Math.Min(6500, 900 * _speechErrorCount);
        global::Android.Util.Log.Warn("MuseScan", "Speech recognition error: " + error);

        // 5 = client error, 9 = insufficient permissions. Other repeated
        // failures should also surface visibly rather than retry forever.
        if (code == 9 || (code == 5 && _speechErrorCount >= 3) ||
            _speechErrorCount >= 5)
        {
            _listenRequested = false;
            StopVoiceRecognition();
            UpdateListenButton();
            ShowScanProblem("Speech recognition stopped (error " + error +
                "). Check microphone access and speech services.");
            return;
        }
        QueueNextRecognitionSession(delay);
    }

    private static void ShowScanProblem(string message)
    {
        global::Android.Util.Log.Warn("MuseScan", message);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var context = Platform.CurrentActivity;
            if (context != null)
                global::Android.Widget.Toast.MakeText(context, message,
                    global::Android.Widget.ToastLength.Long)?.Show();
        });
    }

    private sealed class VoiceRecognitionListener : Java.Lang.Object, IRecognitionListener
    {
        private readonly MainPage _owner;
        public VoiceRecognitionListener(MainPage owner) => _owner = owner;
        public void OnReadyForSpeech(Bundle? parameters) { }
        public void OnBeginningOfSpeech() { }
        public void OnRmsChanged(float rmsdB) { }
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEndOfSpeech() { }
        public void OnPartialResults(Bundle? partialResults) { }
        public void OnEvent(int eventType, Bundle? parameters) { }
        public void OnEndOfSegmentedSession() { }
        public void OnSegmentResults(Bundle? segmentResults) { }
        public void OnLanguageDetection(Bundle? results) { }
        public void OnError(SpeechRecognizerError error) => _owner.OnRecognitionError(error);
        public void OnResults(Bundle? results) => _owner.OnRecognitionResult(results);
    }
}
