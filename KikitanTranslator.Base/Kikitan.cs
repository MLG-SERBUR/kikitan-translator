using KikitanTranslator.Base.Outputs;
using KikitanTranslator.Base.Translators;
using KikitanTranslator.Recognizers;
using KikitanTranslator.Utility;
using Serilog;

namespace KikitanTranslator.Base;

public class Kikitan : IDisposable
{
    private IRecognizer _recognizer;
    private ITranslator _translator;
    private IErrorHandler _errorHandler;
    private List<IOutput> _outputs = [];

    private List<string[]> _queue = [];
    private readonly object _queueLock = new object();

    private bool _running;
    private bool _isLoopback;
    // FORK: optional gate checked at recognition time (not send time), so speech
    // captured before the gate closed still completes. Used for mic-mute.
    private readonly Func<bool>? _shouldProcess;
    // FORK: true once an interim result passes the gate for the current
    // utterance. A final result is allowed through if the utterance started
    // while the gate was open, even if it closed mid-utterance.
    private bool _utteranceStarted;
    
    public event OnRecognizerStatus? OnRecognizerStatusChanged;

    public Kikitan(IRecognizer recognizer, ITranslator translator, IErrorHandler errorHandler, bool loopback, Func<bool>? shouldProcess = null)
    {
        _recognizer = recognizer;
        _translator = translator;
        _errorHandler = errorHandler;
        _shouldProcess = shouldProcess;

        recognizer.OnRecognitionReceived += OnRecognition;
        recognizer.OnRecognizerStatusChanged += OnRecognizerStatus;
        
        _isLoopback = loopback;
        
        Log.Information($"[KKTN] Kikitan is starting up: desktop={_isLoopback}, recognizer={_recognizer.GetType().Name}, translator={_translator.GetType().Name}");
    }

    public void AddOutput(IOutput output) => _outputs.Add(output);

    public void Start()
    {
        _recognizer.Start(_isLoopback ? AppConfig.ConfigObject.TargetLanguage : AppConfig.ConfigObject.SourceLanguage, _errorHandler);
        _running = true;
        Log.Information($"[KKTN] Recognizer start result: desktop={_isLoopback}, status={_recognizer.Status()}");
        
        Task.Run(QueueWorker);
    }

    public void Stop()
    {
        _recognizer.Stop();
        _running = false;
        
        Log.Information("[KKTN] Kikitan has stopped");
    }

    private void OnRecognition(string text, bool final)
    {
        if (final) Log.Debug($"[KKTN] Final recognition received: desktop={_isLoopback}, chars={text.Length}, running={_running}");
        // FORK: gate logic. Interim results passing the gate mark the utterance
        // as started; a final result is allowed through if the utterance started
        // while the gate was open, even if it closed mid-utterance (e.g. user
        // muted before the recognizer finished). Utterances that never passed
        // the gate are still dropped.
        bool gateOpen = _shouldProcess == null || _shouldProcess();
        if (!final)
        {
            if (gateOpen) _utteranceStarted = true;
            else
            {
                Log.Debug($"[KKTN] Recognition dropped by gate: desktop={_isLoopback}, final={final}");
                return;
            }
        }
        else
        {
            if (!_utteranceStarted && !gateOpen)
            {
                Log.Debug($"[KKTN] Recognition dropped by gate: desktop={_isLoopback}, final={final}");
                return;
            }
            _utteranceStarted = false;
        }
        if (AppConfig.ConfigObject.Recognizer == 2)
        {
            foreach (var output in _outputs)
            {
                output.Send(text.Split("|")[0], text.Split("|")[1], final);
            }

            return;
        }
        
        foreach (var output in _outputs)
        {
            output.Send(text, "", false);
        }

        if (!final || text.Length == 0) return;

        try
        {
            var translationTimer = System.Diagnostics.Stopwatch.StartNew();
            Log.Debug($"[KKTN] Translation started: desktop={_isLoopback}, translator={_translator.GetType().Name}, chars={text.Length}, transcriptionOnly={AppConfig.ConfigObject.SpeechToTextOnly}");
            var translated = AppConfig.ConfigObject.SpeechToTextOnly ? "" : _isLoopback ? _translator.Translate(text, AppConfig.ConfigObject.TargetLanguage, AppConfig.ConfigObject.SourceLanguage) : _translator.Translate(text, AppConfig.ConfigObject.SourceLanguage, AppConfig.ConfigObject.TargetLanguage);
        
            Log.Debug($"[KKTN] Translation completed: desktop={_isLoopback}, elapsedMs={translationTimer.ElapsedMilliseconds}, resultChars={translated?.Length}, hasResult={translated != null}");
            if (translated == null) Log.Warning($"[KKTN] No translation result; final output skipped: desktop={_isLoopback}");
            if (translated != null)
            {
                lock (_queueLock)
                {
                    _queue.Add([text, translated]);
                    Log.Debug($"[KKTN] Translation queued: desktop={_isLoopback}, depth={_queue.Count}");
                }
            
                foreach (var output in _outputs.Where(v => !v.IsDelayed())) output.Send(text, translated, true);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, $"[KKTN] Translation/output processing failed: desktop={_isLoopback}");
            
            _errorHandler.OnError($"Error while translating: {e.Message}");
        }
    }

    private void OnRecognizerStatus(RecognizerStatus status) => OnRecognizerStatusChanged?.Invoke(status);

    private async void QueueWorker()
    {
        while (_running)
        {
            if (_queue.Count == 0)
            {
                await Task.Delay(50);
                
                continue;
            }

            lock (_queueLock)
            {
                var texts = _queue.First();
                _queue.RemoveAt(0);
            
                Log.Debug($"[KKTN] Processing delayed output: desktop={_isLoopback}, remaining={_queue.Count}, sourceChars={texts[0].Length}, translationChars={texts[1].Length}");
            
                foreach (var output in _outputs.Where(v => v.IsDelayed())) output.Send(texts[0], texts[1], true);
                // FORK: no artificial per-char delay; send ASAP. Typing indicator
                // is handled by the Chatbox output itself.
            }
        }
    }

    public void ManualTranslate(string text) => OnRecognition(text, true);

    public void Dispose()
    {
        _running = false;
        _recognizer.Dispose();
        _translator.Dispose();
    }
}