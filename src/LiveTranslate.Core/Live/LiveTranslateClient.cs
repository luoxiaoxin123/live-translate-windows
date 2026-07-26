using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace LiveTranslate.Core.Live;

public enum LiveConnectionState
{
    Idle,
    Connecting,
    Ready,
    Failed,
    Closed,
}

/// <summary>
/// Gemini Live Translate WebSocket client.
///
/// Wire format aligned with https://ai.google.dev/gemini-api/docs/live-api/live-translate :
/// on open we send a setup message with translationConfig (target language only — the source
/// language is auto-detected server-side), then stream 16 kHz PCM via realtimeInput. The server
/// answers with setupComplete, input/output transcriptions and translated audio as inlineData.
/// Both camelCase and snake_case field names are accepted; binary frames carry UTF-8 JSON too.
/// </summary>
public sealed class LiveTranslateClient : IDisposable
{
    private const string AudioMimeType = "audio/pcm;rate=16000";

    private readonly object _stateLock = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _sessionCts;
    private Channel<byte[]>? _sendQueue;
    private volatile bool _setupComplete;
    private volatile bool _intentionalClose;

    public LiveConnectionState State { get; private set; } = LiveConnectionState.Idle;

    public string? FailureMessage { get; private set; }

    public event Action<LiveConnectionState, string?>? StateChanged;
    public event Action<string>? InputTranscript;
    public event Action<string>? OutputTranscript;
    public event Action<byte[], string?>? AudioChunk;
    public event Action<string>? ErrorOccurred;

    public async Task ConnectAsync(SessionConfig config)
    {
        await CloseAsync().ConfigureAwait(false);

        _intentionalClose = false;
        _setupComplete = false;
        SetState(LiveConnectionState.Connecting, null);

        var apiKey = config.ApiKey.Trim();
        if (apiKey.Length == 0)
        {
            SetState(LiveConnectionState.Failed, "API key is empty.");
            return;
        }
        if (string.IsNullOrWhiteSpace(config.Endpoint))
        {
            SetState(LiveConnectionState.Failed, "Endpoint is empty.");
            return;
        }

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var cts = new CancellationTokenSource();
        var sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        _socket = socket;
        _sessionCts = cts;
        _sendQueue = sendQueue;

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            var url = BuildUrl(config.Endpoint, apiKey);
            await socket.ConnectAsync(new Uri(url), connectTimeout.Token).ConfigureAwait(false);

            var setup = BuildSetupMessage(config.ModelId, config.TargetLanguageCode, config.EchoTargetLanguage);
            await socket.SendAsync(Encoding.UTF8.GetBytes(setup), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ReferenceEquals(_socket, socket))
        {
            SetState(LiveConnectionState.Failed, $"Connection failed: {Simplify(ex)}");
            return;
        }

        _ = Task.Run(() => ReceiveLoopAsync(socket, cts.Token));
        _ = Task.Run(() => SendLoopAsync(socket, sendQueue.Reader, cts.Token));
    }

    /// <summary>Queues one 16 kHz mono PCM16 LE chunk; silently dropped unless the session is Ready.</summary>
    public void SendPcm16Le(byte[] pcm)
    {
        if (pcm.Length == 0 || !_setupComplete || State != LiveConnectionState.Ready) return;
        _sendQueue?.Writer.TryWrite(pcm);
    }

    public async Task CloseAsync()
    {
        _intentionalClose = true;
        var socket = _socket;
        var cts = _sessionCts;
        _socket = null;
        _sessionCts = null;
        _sendQueue?.Writer.TryComplete();
        _sendQueue = null;

        // Cancel first so the receive/send loops exit via cancellation instead of
        // observing a disposed socket (which could surface as a spurious failure).
        cts?.Cancel();

        if (socket != null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client close", closeTimeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }

        socket?.Dispose();
        // The CTS is deliberately not disposed here: the loops may still hold its token,
        // and a timer-less CTS holds no scarce resources.
    }

    /// <summary>
    /// Connects and polls the connection state (events could fire before a subscriber attaches,
    /// so state polling is the reliable signal). Always closes the session afterwards.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(SessionConfig config, int timeoutMs = 25000, CancellationToken cancellationToken = default)
    {
        try
        {
            await ConnectAsync(config).ConfigureAwait(false);
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (State)
                {
                    case LiveConnectionState.Ready:
                        return (true, "OK");
                    case LiveConnectionState.Failed:
                        return (false, FailureMessage ?? "Connection failed.");
                    case LiveConnectionState.Closed:
                        return (false, FailureMessage ?? "Connection closed before setup completed.");
                }
                await Task.Delay(100).ConfigureAwait(false);
            }

            return State == LiveConnectionState.Connecting
                ? (false, "Timed out while connecting — the endpoint looks valid but the network, proxy or firewall may be blocking generativelanguage.googleapis.com.")
                : (false, "Timed out waiting for setup to complete.");
        }
        finally
        {
            await CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(ClientWebSocket socket, ChannelReader<byte[]> reader, CancellationToken token)
    {
        try
        {
            await foreach (var pcm in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open) break;
                var message = BuildRealtimeAudioMessage(pcm);
                await socket.SendAsync(message, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_socket, socket) && !_intentionalClose)
            {
                ErrorOccurred?.Invoke($"Audio send failed: {Simplify(ex)}");
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[32 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        HandleClosed(socket, $"Server closed the connection ({result.CloseStatus}): {result.CloseStatusDescription}");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                // Binary frames also carry UTF-8 JSON on this API.
                HandleMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            }

            HandleClosed(socket, "Connection ended.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HandleClosed(socket, $"Connection lost: {Simplify(ex)}");
        }
    }

    private void HandleClosed(ClientWebSocket socket, string reason)
    {
        if (!ReferenceEquals(_socket, socket) || _intentionalClose) return;
        if (_setupComplete)
        {
            SetState(LiveConnectionState.Closed, reason);
        }
        else
        {
            SetState(LiveConnectionState.Failed, $"Closed before setup completed. {reason}");
        }
    }

    private void HandleMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (TryGet(root, out _, "setupComplete", "setup_complete"))
            {
                _setupComplete = true;
                SetState(LiveConnectionState.Ready, null);
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                Fail(FormatError(error));
                return;
            }

            if (TryGet(root, out var content, "serverContent", "server_content"))
            {
                if (TryGet(content, out var input, "inputTranscription", "input_transcription")
                    && TryGetString(input, "text", out var inputText) && inputText.Length > 0)
                {
                    InputTranscript?.Invoke(inputText);
                }

                if (TryGet(content, out var output, "outputTranscription", "output_transcription")
                    && TryGetString(output, "text", out var outputText) && outputText.Length > 0)
                {
                    OutputTranscript?.Invoke(outputText);
                }

                if (TryGet(content, out var turn, "modelTurn", "model_turn")
                    && turn.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (!TryGet(part, out var inline, "inlineData", "inline_data")) continue;
                        if (!TryGetString(inline, "data", out var data) || data.Length == 0) continue;

                        TryGetString(inline, "mimeType", out var mime);
                        if (string.IsNullOrEmpty(mime)) TryGetString(inline, "mime_type", out mime);

                        try
                        {
                            AudioChunk?.Invoke(Convert.FromBase64String(data), mime);
                        }
                        catch (FormatException)
                        {
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Ignore non-JSON frames.
        }
    }

    private static string FormatError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return $"Server error: {error}";
        TryGetString(error, "message", out var message);
        TryGetString(error, "status", out var status);
        var code = error.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
            ? codeEl.GetRawText()
            : null;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(code)) parts.Add($"code {code}");
        if (!string.IsNullOrEmpty(status)) parts.Add(status);
        if (!string.IsNullOrEmpty(message)) parts.Add(message);
        return parts.Count > 0 ? $"Server error: {string.Join(" — ", parts)}" : "Unknown server error.";
    }

    private void Fail(string message)
    {
        ErrorOccurred?.Invoke(message);
        SetState(LiveConnectionState.Failed, message);
    }

    private void SetState(LiveConnectionState state, string? message)
    {
        lock (_stateLock)
        {
            if (State == state && FailureMessage == message) return;
            State = state;
            FailureMessage = state is LiveConnectionState.Failed or LiveConnectionState.Closed ? message : null;
        }
        StateChanged?.Invoke(state, message);
    }

    private static bool TryGet(JsonElement element, out JsonElement value, string camelCase, string snakeCase)
    {
        if (element.TryGetProperty(camelCase, out value)) return true;
        return element.TryGetProperty(snakeCase, out value);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    private static string Simplify(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return inner.Message;
    }

    // ---- wire-format builders (public static so tests can pin the exact shapes) ----

    public static string BuildSetupMessage(string modelId, string targetLanguageCode, bool echoTargetLanguage)
    {
        var model = modelId.Trim();
        if (model.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            model = model["models/".Length..];
        }

        var target = string.IsNullOrWhiteSpace(targetLanguageCode) ? "zh-Hans" : targetLanguageCode.Trim();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("setup");
            writer.WriteString("model", "models/" + model);
            writer.WriteStartObject("generationConfig");
            writer.WriteStartArray("responseModalities");
            writer.WriteStringValue("AUDIO");
            writer.WriteEndArray();
            writer.WriteStartObject("translationConfig");
            writer.WriteString("targetLanguageCode", target);
            writer.WriteBoolean("echoTargetLanguage", echoTargetLanguage);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartObject("inputAudioTranscription");
            writer.WriteEndObject();
            writer.WriteStartObject("outputAudioTranscription");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static byte[] BuildRealtimeAudioMessage(byte[] pcm16le)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("realtimeInput");
            writer.WriteStartObject("audio");
            writer.WriteBase64String("data", pcm16le);
            writer.WriteString("mimeType", AudioMimeType);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Appends or replaces the key= query parameter; strips stray quotes/whitespace.</summary>
    public static string BuildUrl(string endpoint, string apiKey)
    {
        var cleaned = endpoint.Trim().Trim('"', '\'');
        var key = apiKey.Trim();
        if (Regex.IsMatch(cleaned, @"[?&]key=", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(cleaned, @"([?&]key=)[^&]*", "${1}" + key, RegexOptions.IgnoreCase);
        }
        return cleaned + (cleaned.Contains('?') ? "&" : "?") + "key=" + key;
    }

    public static string RedactKey(string url) =>
        Regex.Replace(url, @"([?&]key=)[^&]*", "${1}***", RegexOptions.IgnoreCase);

    public void Dispose()
    {
        try
        {
            CloseAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
    }
}
