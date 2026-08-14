using LiveTranslate.App.Localization;
using LiveTranslate.App.Views;
using LiveTranslate.Core.Audio;
using LiveTranslate.Core.Data;
using LiveTranslate.Core.Export;
using LiveTranslate.Core.Live;
using LiveTranslate.Core.Text;
using Microsoft.UI.Dispatching;

namespace LiveTranslate.App.Services;

public enum SessionStatus
{
    Idle,
    Starting,
    Running,
    Reconnecting,
    Stopped,
    Error,
}

/// <summary>
/// Orchestrates one subtitle session: audio capture → Live API client → overlay subtitles,
/// translated-audio playback and transcript accumulation. All public state is mutated on the
/// UI thread; client/capture callbacks are marshaled through the DispatcherQueue.
/// </summary>
public sealed class SubtitleSessionService
{
    private readonly UserSettingsRepository _settings;
    private readonly ApiKeyStore _keys;
    private readonly DispatcherQueue _dispatcher;

    private volatile LiveTranslateClient? _client;
    private SystemAudioCapturer? _systemCapturer;
    private MicAudioCapturer? _micCapturer;
    private PcmMixer? _mixer;
    private TranslatedAudioPlayer? _player;
    private OverlayWindow? _overlay;
    private bool _captureStarted;

    private readonly TranscriptAccumulator _overlayInput = new(800);
    private readonly TranscriptAccumulator _overlayOutput = new(800);
    private readonly TranscriptAccumulator _fullInput = new(200_000);
    private readonly TranscriptAccumulator _fullOutput = new(200_000);

    public SessionStatus Status { get; private set; } = SessionStatus.Idle;
    public string StatusMessage { get; private set; } = "";
    public string InputPreview { get; private set; } = "";
    public string OutputPreview { get; private set; } = "";
    public bool CanExport { get; private set; }
    public string LastInputFull { get; private set; } = "";
    public string LastOutputFull { get; private set; } = "";
    private DateTime _lastStoppedAt;

    public bool IsActive => Status is SessionStatus.Starting or SessionStatus.Running or SessionStatus.Reconnecting;

    /// <summary>Raised on the UI thread whenever status changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised on the UI thread when caption previews change (not status/buttons).</summary>
    public event Action? TranscriptChanged;

    public SubtitleSessionService(UserSettingsRepository settings, ApiKeyStore keys, DispatcherQueue dispatcher)
    {
        _settings = settings;
        _keys = keys;
        _dispatcher = dispatcher;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    private static readonly string LogPath =
        Path.Combine(UserSettingsRepository.DefaultDirectory(), "session.log");

    private int _loggedInputs;
    private int _loggedOutputs;
    private int _transcriptFlushScheduled;
    private DispatcherQueueTimer? _transcriptTimer;
    private DispatcherQueueTimer? _stableTimer;

    private string? _currentApiKey;
    private bool _reconnectInFlight;
    private int _unexpectedReconnects;
    private bool _rotateKeyNext;
    private CancellationTokenSource? _reconnectCts;

    private const int MaxUnexpectedReconnects = 5;
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(30);

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
        }
        catch
        {
        }
    }

    private static void RotateLogIfLarge()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 256 * 1024) info.Delete();
        }
        catch
        {
        }
    }

    public async Task StartAsync()
    {
        if (IsActive) return;

        var apiKey = _keys.NextRotatedKey();
        if (apiKey == null)
        {
            SetStatus(SessionStatus.Error, L.NoApiKey);
            return;
        }

        _overlayInput.Reset();
        _overlayOutput.Reset();
        _fullInput.Reset();
        _fullOutput.Reset();
        InputPreview = "";
        OutputPreview = "";
        CanExport = false;
        _captureStarted = false;
        _loggedInputs = 0;
        _loggedOutputs = 0;
        _unexpectedReconnects = 0;
        _reconnectInFlight = false;
        _rotateKeyNext = false;
        _currentApiKey = apiKey;
        RotateLogIfLarge();

        var settings = _settings.Current;
        SetStatus(SessionStatus.Starting, "");

        _player = new TranslatedAudioPlayer();
        _player.SetVolume((float)settings.TranslatedVolume);
        _player.SetEnabled(settings.PlayTranslatedAudio);

        _overlay = new OverlayWindow(_settings);
        _overlay.ShowNoActivate();

        var client = BindClient();
        _client = client;

        await client.ConnectAsync(new SessionConfig(
            settings.Endpoint,
            apiKey,
            settings.ModelId,
            settings.TargetLanguageCode,
            settings.EchoTargetLanguage));
    }

    private LiveTranslateClient BindClient()
    {
        var client = new LiveTranslateClient();
        client.StateChanged += (state, message) => OnClientStateChanged(client, state, message);
        client.GoAwayReceived += timeLeft => OnGoAway(client, timeLeft);
        client.InputTranscript += text => OnTranscript(client, text, isInput: true);
        client.OutputTranscript += text => OnTranscript(client, text, isInput: false);
        client.AudioChunk += (pcm, mime) =>
        {
            if (ReferenceEquals(_client, client)) _player?.PlayPcm(pcm, mime);
        };
        client.ErrorOccurred += message => _dispatcher.TryEnqueue(() =>
        {
            if (ReferenceEquals(_client, client) && IsActive)
            {
                StatusMessage = message;
                StateChanged?.Invoke();
            }
        });
        return client;
    }

    private void ForwardPcm(byte[] pcm) => _client?.SendPcm16Le(pcm);

    private bool _finalizing;

    public async Task StopAsync()
    {
        if (Status is SessionStatus.Idle or SessionStatus.Stopped) return;
        if (_finalizing) return;
        _finalizing = true;
        try
        {
            await TearDownAsync();

            LastInputFull = _fullInput.Text;
            LastOutputFull = _fullOutput.Text;
            CanExport = LastInputFull.Length > 0 || LastOutputFull.Length > 0;
            _lastStoppedAt = DateTime.Now;
            SetStatus(SessionStatus.Stopped, "");
        }
        finally
        {
            _finalizing = false;
        }
    }

    public async Task ShutdownAsync()
    {
        await TearDownAsync();
        _settings.Flush();
    }

    public (bool Ok, string Message) ExportLastSession()
    {
        try
        {
            var stoppedAt = _lastStoppedAt == default ? DateTime.Now : _lastStoppedAt;
            var markdown = MarkdownExporter.BuildMarkdown(
                SentenceLineBreaker.Format(LastInputFull),
                SentenceLineBreaker.Format(LastOutputFull),
                stoppedAt);
            var path = MarkdownExporter.SaveToDownloads(markdown, stoppedAt);
            return (true, L.ExportedTo(path));
        }
        catch (Exception ex)
        {
            return (false, L.ExportFailed(ex.Message));
        }
    }

    private void OnClientStateChanged(LiveTranslateClient client, LiveConnectionState state, string? message)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_client, client)) return;

            switch (state)
            {
                case LiveConnectionState.Ready:
                    OnClientReady();
                    break;

                case LiveConnectionState.Failed:
                    HandleDisconnect(client, message ?? "Connection failed.");
                    break;

                case LiveConnectionState.Closed when IsActive:
                    HandleDisconnect(client, message ?? "");
                    break;
            }
        });
    }

    private void OnClientReady()
    {
        _reconnectInFlight = false;
        if (!_captureStarted)
        {
            StartCapturePipeline();
            return;
        }

        SetStatus(SessionStatus.Running, _systemCapturer is { UsingProcessExclude: false } ? L.UsingClassicLoopback : "");
        ArmStableTimer();
    }

    private void OnGoAway(LiveTranslateClient client, TimeSpan? timeLeft)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_client, client) || _finalizing || !IsActive) return;
            Log($"goAway timeLeft={timeLeft}");
            _ = ReconnectAsync(expected: true, reason: "");
        });
    }

    private void HandleDisconnect(LiveTranslateClient client, string message)
    {
        if (!ReferenceEquals(_client, client) || _finalizing || !IsActive) return;

        if (LiveTranslateClient.IsQuotaError(message))
            _rotateKeyNext = true;

        if (!LiveTranslateClient.IsReconnectableFailure(message))
        {
            _ = FailSessionAsync(message);
            return;
        }

        _reconnectInFlight = false;
        _ = ReconnectAsync(expected: false, reason: message);
    }

    private async Task ReconnectAsync(bool expected, string reason)
    {
        if (_finalizing || !IsActive) return;
        if (_reconnectInFlight) return;
        _reconnectInFlight = true;

        if (!expected)
        {
            _unexpectedReconnects++;
            if (_unexpectedReconnects > MaxUnexpectedReconnects)
            {
                _reconnectInFlight = false;
                await FailSessionAsync(L.ReconnectGaveUp);
                return;
            }
        }

        SetStatus(SessionStatus.Reconnecting, expected ? "" : reason);
        Log($"reconnect expected={expected} unexpected={_unexpectedReconnects}");

        var delay = expected || _unexpectedReconnects <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Min(16, Math.Pow(2, _unexpectedReconnects - 1)));

        var old = _client;
        var replacement = BindClient();
        _client = replacement;

        var cts = new CancellationTokenSource();
        _reconnectCts?.Cancel();
        _reconnectCts = cts;

        try
        {
            if (old != null)
            {
                try { await old.CloseAsync().ConfigureAwait(false); } catch { }
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);

            if (_finalizing || !ReferenceEquals(_client, replacement) || cts.IsCancellationRequested)
            {
                _reconnectInFlight = false;
                return;
            }

            var settings = _settings.Current;
            var key = _currentApiKey;
            if (_rotateKeyNext)
            {
                key = _keys.NextRotatedKey() ?? key;
                _currentApiKey = key;
                _rotateKeyNext = false;
            }

            if (string.IsNullOrEmpty(key))
            {
                _reconnectInFlight = false;
                _dispatcher.TryEnqueue(() => _ = FailSessionAsync(L.NoApiKey));
                return;
            }

            await replacement.ConnectAsync(new SessionConfig(
                settings.Endpoint,
                key,
                settings.ModelId,
                settings.TargetLanguageCode,
                settings.EchoTargetLanguage)).ConfigureAwait(false);

            if (_finalizing || !ReferenceEquals(_client, replacement))
            {
                try { await replacement.CloseAsync().ConfigureAwait(false); } catch { }
                _reconnectInFlight = false;
            }
        }
        catch (OperationCanceledException)
        {
            _reconnectInFlight = false;
        }
        catch (Exception ex)
        {
            _reconnectInFlight = false;
            _dispatcher.TryEnqueue(() =>
            {
                if (!_finalizing && ReferenceEquals(_client, replacement))
                    _ = FailSessionAsync(ex.Message);
            });
        }
    }

    private void ArmStableTimer()
    {
        _stableTimer ??= _dispatcher.CreateTimer();
        _stableTimer.Stop();
        _stableTimer.Interval = StableAfter;
        _stableTimer.IsRepeating = false;
        _stableTimer.Tick -= OnStableTick;
        _stableTimer.Tick += OnStableTick;
        _stableTimer.Start();
    }

    private void OnStableTick(DispatcherQueueTimer sender, object args) => _unexpectedReconnects = 0;

    private async Task FailSessionAsync(string message)
    {
        // A manual Stop may already be finalizing on this (UI) thread — don't fight over the final status.
        if (_finalizing) return;
        _finalizing = true;
        try
        {
            await TearDownAsync();
            LastInputFull = _fullInput.Text;
            LastOutputFull = _fullOutput.Text;
            CanExport = LastInputFull.Length > 0 || LastOutputFull.Length > 0;
            _lastStoppedAt = DateTime.Now;
            SetStatus(SessionStatus.Error, message);
        }
        finally
        {
            _finalizing = false;
        }
    }

    private void StartCapturePipeline()
    {
        if (_captureStarted) return;
        _captureStarted = true;
        var mode = _settings.Current.AudioSourceMode;

        // Capture start-up joins device/COM threads — keep it off the UI thread.
        // The capturers are built as locals and only published to the fields on the UI
        // thread, where teardown also runs: either the session is still current and takes
        // ownership, or they are disposed immediately (no leaked live microphone).
        _ = Task.Run(() =>
        {
            SystemAudioCapturer? system = null;
            MicAudioCapturer? mic = null;
            PcmMixer? mixer = null;
            var warning = "";
            try
            {
                Action<string> onCaptureError = message => _dispatcher.TryEnqueue(() =>
                {
                    if (!_finalizing && IsActive) _ = FailSessionAsync(L.CaptureStopped(message));
                });

                if (mode.NeedsSystemAudio())
                {
                    system = new SystemAudioCapturer
                    {
                        ShouldMuteOutgoing = () => _player?.IsActivelyPlaying == true,
                        OnCaptureError = onCaptureError,
                    };
                }
                if (mode.NeedsMicrophone())
                {
                    mic = new MicAudioCapturer { OnCaptureError = onCaptureError };
                }

                if (mode == AudioSourceMode.MediaAndMic)
                {
                    var localMixer = new PcmMixer(mixed => ForwardPcm(mixed));
                    mixer = localMixer;
                    system!.Start(pcm => localMixer.OfferMedia(pcm));
                    mic!.Start(pcm => localMixer.OfferMic(pcm));
                }
                else if (mode == AudioSourceMode.Media)
                {
                    system!.Start(pcm => ForwardPcm(pcm));
                }
                else
                {
                    mic!.Start(pcm => ForwardPcm(pcm));
                }

                if (system != null)
                {
                    Log($"system capture: processExclude={system.UsingProcessExclude}");
                    if (!system.UsingProcessExclude) warning = L.UsingClassicLoopback;
                }
            }
            catch (Exception ex)
            {
                try { system?.Dispose(); } catch { }
                try { mic?.Dispose(); } catch { }
                _dispatcher.TryEnqueue(() =>
                {
                    if (!_finalizing && IsActive) _ = FailSessionAsync(L.CaptureFailed(ex.Message));
                });
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                // Capture outlives a single WebSocket: publish if the session is still up,
                // even if we already swapped to a replacement client during start-up.
                if (!_finalizing && IsActive && _systemCapturer is null && _micCapturer is null)
                {
                    _systemCapturer = system;
                    _micCapturer = mic;
                    _mixer = mixer;
                    _reconnectInFlight = false;
                    SetStatus(SessionStatus.Running, warning);
                    ArmStableTimer();
                }
                else
                {
                    // Session was stopped while capture was spinning up — don't leak it.
                    try { system?.Dispose(); } catch { }
                    try { mic?.Dispose(); } catch { }
                }
            });
        });
    }

    private void OnTranscript(LiveTranslateClient client, string text, bool isInput)
    {
        if (!ReferenceEquals(_client, client)) return;

        if (isInput)
        {
            _overlayInput.Append(text);
            _fullInput.Append(text);
        }
        else
        {
            _overlayOutput.Append(text);
            _fullOutput.Append(text);
        }

        var logged = isInput ? Interlocked.Increment(ref _loggedInputs) : Interlocked.Increment(ref _loggedOutputs);
        if (logged is 1 or 10 or 50) Log($"transcript {(isInput ? "in" : "out")} #{logged}: {Tail(text, 60)}");

        ScheduleTranscriptFlush();
    }

    private void ScheduleTranscriptFlush()
    {
        if (Interlocked.CompareExchange(ref _transcriptFlushScheduled, 1, 0) != 0) return;
        _dispatcher.TryEnqueue(() =>
        {
            _transcriptTimer ??= CreateTranscriptTimer();
            if (!_transcriptTimer.IsRunning) _transcriptTimer.Start();
        });
    }

    private DispatcherQueueTimer CreateTranscriptTimer()
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => FlushTranscriptUi();
        return timer;
    }

    private void FlushTranscriptUi()
    {
        Interlocked.Exchange(ref _transcriptFlushScheduled, 0);
        _transcriptTimer?.Stop();

        var source = SentenceLineBreaker.Format(_overlayInput.Text);
        var translation = SentenceLineBreaker.Format(_overlayOutput.Text);
        InputPreview = Tail(source, 300);
        OutputPreview = Tail(translation, 300);
        _overlay?.SetTexts(source, translation);
        TranscriptChanged?.Invoke();
    }

    private async Task TearDownAsync()
    {
        _reconnectCts?.Cancel();
        _reconnectInFlight = false;
        _stableTimer?.Stop();
        _transcriptTimer?.Stop();
        FlushTranscriptUi();

        var client = _client;
        _client = null;

        var systemCapturer = _systemCapturer;
        var micCapturer = _micCapturer;
        _systemCapturer = null;
        _micCapturer = null;
        _mixer = null;

        var player = _player;
        _player = null;

        await Task.Run(() =>
        {
            try { systemCapturer?.Dispose(); } catch { }
            try { micCapturer?.Dispose(); } catch { }
            try { player?.Dispose(); } catch { }
        });

        if (client != null)
        {
            try { await client.CloseAsync(); } catch { }
        }

        _overlay?.CloseOverlay();
        _overlay = null;
    }

    private void OnSettingsChanged(UserSettings settings)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _player?.SetVolume((float)settings.TranslatedVolume);
            _player?.SetEnabled(settings.PlayTranslatedAudio);
            _overlay?.ApplyAppearance(settings);
        });
    }

    private void SetStatus(SessionStatus status, string message)
    {
        Status = status;
        StatusMessage = message;
        Log($"status={status} {message}".TrimEnd());
        StateChanged?.Invoke();
    }

    private static string Tail(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var start = text.Length - maxChars;
        if (char.IsLowSurrogate(text[start])) start++; // never split a surrogate pair
        return text[start..];
    }
}
