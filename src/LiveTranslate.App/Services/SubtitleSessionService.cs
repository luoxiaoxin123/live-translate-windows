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

    private LiveTranslateClient? _client;
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

    public bool IsActive => Status is SessionStatus.Starting or SessionStatus.Running;

    /// <summary>Raised on the UI thread whenever status/previews change.</summary>
    public event Action? StateChanged;

    public SubtitleSessionService(UserSettingsRepository settings, ApiKeyStore keys, DispatcherQueue dispatcher)
    {
        _settings = settings;
        _keys = keys;
        _dispatcher = dispatcher;
        _settings.SettingsChanged += OnSettingsChanged;
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

        var settings = _settings.Current;
        SetStatus(SessionStatus.Starting, "");

        _player = new TranslatedAudioPlayer();
        _player.SetVolume((float)settings.TranslatedVolume);
        _player.SetEnabled(settings.PlayTranslatedAudio);

        _overlay = new OverlayWindow(_settings);
        _overlay.ShowNoActivate();

        var client = new LiveTranslateClient();
        _client = client;
        client.StateChanged += (state, message) => OnClientStateChanged(client, state, message);
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

        await client.ConnectAsync(new SessionConfig(
            settings.Endpoint,
            apiKey,
            settings.ModelId,
            settings.TargetLanguageCode));
    }

    public async Task StopAsync()
    {
        if (Status is SessionStatus.Idle or SessionStatus.Stopped) return;
        await TearDownAsync();

        LastInputFull = _fullInput.Text;
        LastOutputFull = _fullOutput.Text;
        CanExport = LastInputFull.Length > 0 || LastOutputFull.Length > 0;
        SetStatus(SessionStatus.Stopped, "");
    }

    public async Task ShutdownAsync()
    {
        await TearDownAsync();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    public (bool Ok, string Message) ExportLastSession()
    {
        try
        {
            var stoppedAt = DateTime.Now;
            var markdown = MarkdownExporter.BuildMarkdown(LastInputFull, LastOutputFull, stoppedAt);
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
                case LiveConnectionState.Ready when Status == SessionStatus.Starting:
                    StartCapturePipeline(client);
                    break;

                case LiveConnectionState.Failed:
                    _ = FailSessionAsync(message ?? "Connection failed.");
                    break;

                case LiveConnectionState.Closed when IsActive:
                    _ = FailSessionAsync(L.SessionClosed(message ?? ""));
                    break;
            }
        });
    }

    private async Task FailSessionAsync(string message)
    {
        await TearDownAsync();
        LastInputFull = _fullInput.Text;
        LastOutputFull = _fullOutput.Text;
        CanExport = LastInputFull.Length > 0 || LastOutputFull.Length > 0;
        SetStatus(SessionStatus.Error, message);
    }

    private void StartCapturePipeline(LiveTranslateClient client)
    {
        if (_captureStarted) return;
        _captureStarted = true;
        var mode = _settings.Current.AudioSourceMode;

        // Capture start-up joins device/COM threads — keep it off the UI thread.
        _ = Task.Run(() =>
        {
            var warning = "";
            try
            {
                if (mode.NeedsSystemAudio())
                {
                    _systemCapturer = new SystemAudioCapturer
                    {
                        ShouldMuteOutgoing = () => _player?.IsActivelyPlaying == true,
                    };
                }
                if (mode.NeedsMicrophone())
                {
                    _micCapturer = new MicAudioCapturer();
                }

                if (mode == AudioSourceMode.MediaAndMic)
                {
                    _mixer = new PcmMixer(mixed => client.SendPcm16Le(mixed));
                    _systemCapturer!.Start(pcm => _mixer?.OfferMedia(pcm));
                    _micCapturer!.Start(pcm => _mixer?.OfferMic(pcm));
                }
                else if (mode == AudioSourceMode.Media)
                {
                    _systemCapturer!.Start(pcm => client.SendPcm16Le(pcm));
                }
                else
                {
                    _micCapturer!.Start(pcm => client.SendPcm16Le(pcm));
                }

                if (_systemCapturer is { UsingProcessExclude: false })
                {
                    warning = L.UsingClassicLoopback;
                }
            }
            catch (Exception ex)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_client, client)) _ = FailSessionAsync($"Audio capture failed: {ex.Message}");
                });
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                if (ReferenceEquals(_client, client) && Status == SessionStatus.Starting)
                {
                    SetStatus(SessionStatus.Running, warning);
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

        _dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_client, client)) return;
            InputPreview = Tail(_overlayInput.Text, 300);
            OutputPreview = Tail(_overlayOutput.Text, 300);
            _overlay?.SetTexts(_overlayInput.Text, _overlayOutput.Text);
            StateChanged?.Invoke();
        });
    }

    private async Task TearDownAsync()
    {
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
        StateChanged?.Invoke();
    }

    private static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[^maxChars..];
}
