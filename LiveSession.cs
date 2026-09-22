using System.Net.WebSockets;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;

// Retain original PCM until one complete transcript is selected. Never insert partials.
sealed class LiveSession : IDisposable
{
    readonly DictationSettings settings;
    readonly Action<string> preview, progress;
    readonly CancellationTokenSource cancel = new();
    readonly Channel<byte[]> frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(500) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    readonly MemoryStream audio = new();
    readonly object audioLock = new();
    readonly ClientWebSocket socket = new();
    readonly Task<string> remote;
    string liveText = "";
    int sectionCount;
    long sampleCount;
    int samplePeak;
    double squareSum;
    public string AudioSummary { get { lock (audioLock) return $"samples={sampleCount} peak={samplePeak} rms={Math.Sqrt(squareSum / Math.Max(1, sampleCount)):F2}"; } }
    bool stopped;
    bool disposed;
    public string Source { get; private set; } = "";
    public LiveSession(DictationSettings settings, Action<string> preview, Action<string> progress) { this.settings = settings; this.preview = preview; this.progress = progress; remote = string.IsNullOrWhiteSpace(settings.GatewayUrl) ? Task.FromResult("") : Remote(); }
    public void Feed(byte[] bytes)
    {
        lock (audioLock)
        {
            if (stopped) return;
            audio.Write(bytes);
            for (int i = 0; i + 1 < bytes.Length; i += 2) { int sample = BitConverter.ToInt16(bytes, i); samplePeak = Math.Max(samplePeak, Math.Abs(sample)); squareSum += (double)sample * sample; sampleCount++; }
        }
        if (settings.GatewayUrl.Length > 0 && !frames.Writer.TryWrite(bytes))
        {
            // Backpressure fails remote path; original PCM remains available for fallback.
            socket.Abort(); frames.Writer.TryComplete();
        }
    }
    async Task<string> Remote()
    {
        if (string.IsNullOrWhiteSpace(settings.GatewayUrl)) throw new Exception("No streaming gateway configured.");
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token); connect.CancelAfter(TimeSpan.FromSeconds(3));
        var uri = new UriBuilder(settings.GatewayUrl.TrimEnd('/') + "/ws"); uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
        if (settings.GatewayApiKey.Length > 0) socket.Options.SetRequestHeader("Authorization", "Bearer " + settings.GatewayApiKey);
        await socket.ConnectAsync(uri.Uri, connect.Token);
        var start = new { type = "start", language = settings.Language, segmentation = new { pause = settings.PauseSeconds, wait = settings.DecisionDelaySeconds, maximum = settings.MaximumSectionSeconds, signals = new[] { "vad", "punct", "length" }, combine = "and" }, batch_timeout_s = settings.BatchTimeoutSeconds, finish_timeout_s = settings.FinishTimeoutSeconds, stream_fallback = settings.StreamFallback };
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(start)), WebSocketMessageType.Text, true, cancel.Token);
        var sender = SendFrames();
        try
        {
            var buffer = new byte[16384];
            while (!cancel.IsCancellationRequested)
            {
                using var message = new MemoryStream(); WebSocketReceiveResult received;
                do { received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel.Token); if (received.MessageType == WebSocketMessageType.Close) throw new IOException("Gateway connection closed."); message.Write(buffer, 0, received.Count); if (message.Length > 1024 * 1024) throw new IOException("Gateway message too large."); } while (!received.EndOfMessage);
                using var doc = JsonDocument.Parse(message.ToArray()); var e = doc.RootElement;
                switch (e.GetProperty("type").GetString())
                {
                    case "delta": liveText += e.GetProperty("text").GetString(); preview(liveText); break;
                    case "segment": sectionCount++; progress($"{sectionCount} sections · recording"); break;
                    case "warning": progress(e.GetProperty("text").GetString() ?? ""); break;
                    case "error": throw new IOException(e.GetProperty("text").GetString());
                    case "result":
                        lock (audioLock) { if (!stopped) throw new IOException("Gateway completed before recording stopped."); }
                        long expected; lock (audioLock) expected = audio.Length / 2;
                        if (!e.TryGetProperty("samples", out var sampleCount) || !sampleCount.TryGetInt64(out long actual) || actual != expected) throw new IOException("Gateway result does not cover the complete recording.");
                        Source = e.GetProperty("source").GetString() ?? "gateway";
                        string finalText = e.GetProperty("text").GetString()?.Trim() ?? "";
                        if (finalText.Length == 0) throw new IOException("Gateway returned an empty transcript.");
                        return finalText;
                }
            }
            throw new OperationCanceledException();
        }
        finally { socket.Abort(); frames.Writer.TryComplete(); try { await sender; } catch (Exception) { } }
    }
    async Task SendFrames()
    {
        try
        {
            await foreach (var data in frames.Reader.ReadAllAsync(cancel.Token)) await socket.SendAsync(data, WebSocketMessageType.Binary, true, cancel.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("stop"), WebSocketMessageType.Text, true, cancel.Token);
        }
        catch { socket.Abort(); throw; }
    }
    public async Task<string> Finish()
    {
        lock (audioLock) stopped = true;
        frames.Writer.TryComplete();
        cancel.Token.ThrowIfCancellationRequested();
        lock (audioLock)
        {
            if (sampleCount == 0) throw new IOException("No microphone samples received. Check the selected microphone and Windows microphone permissions.");
            if (samplePeak == 0) throw new IOException("The microphone delivered digital silence. Check hardware mute, input gain and the selected audio input. No fallback can recover missing audio.");
        }
        if (string.IsNullOrWhiteSpace(settings.GatewayUrl)) return await FileTranscription();
        try { return await remote.WaitAsync(TimeSpan.FromSeconds(settings.FinishTimeoutSeconds + 5), cancel.Token); }
        catch (Exception) when (!cancel.IsCancellationRequested)
        {
            socket.Abort();
            if (string.IsNullOrWhiteSpace(settings.FallbackUrl)) throw new IOException("Gateway returned no complete result and no file ASR is configured.");
            progress("Using file ASR …");
            return await FileTranscription();
        }
    }
    async Task<string> FileTranscription()
    {
        cancel.Token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.FallbackUrl)) throw new IOException("Configure a streaming gateway or file transcription URL in Settings.");
        byte[] pcm; lock (audioLock) pcm = audio.ToArray();
        using var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(wav), new WaveFormat(16000, 16, 1))) writer.Write(pcm, 0, pcm.Length);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.FallbackTimeoutSeconds) };
        if (settings.FallbackApiKey.Length > 0) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.FallbackApiKey);
        using var body = new MultipartFormDataContent(); var part = new ByteArrayContent(wav.ToArray()); part.Headers.ContentType = new("audio/wav"); body.Add(part, "file", "dictation.wav"); body.Add(new StringContent(settings.FallbackModel), "model"); body.Add(new StringContent(settings.Language), "language"); body.Add(new StringContent("json"), "response_format");
        using var response = await http.PostAsync(settings.FallbackUrl, body, cancel.Token); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel.Token)); Source = settings.GatewayUrl.Length == 0 ? "file-asr" : "file-fallback";
        string text = doc.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
        if (text.Length == 0) throw new IOException("The ASR provider returned no text. Check microphone input and level; the recording is retained for recovery.");
        return text;
    }
    public string SaveRecovery()
    {
        byte[] pcm; lock (audioLock) pcm = audio.ToArray();
        if (pcm.Length == 0) return "";
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVoxKeys", "Recovery"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1)); writer.Write(pcm, 0, pcm.Length); return path;
    }
    public void Cancel() { cancel.Cancel(); socket.Abort(); frames.Writer.TryComplete(); }
    public void Dispose()
    {
        lock (audioLock) { if (disposed) return; disposed = true; stopped = true; }
        Cancel(); socket.Dispose(); lock (audioLock) { audio.SetLength(0); audio.Dispose(); }
        _ = remote.ContinueWith(t => { _ = t.Exception; cancel.Dispose(); }, TaskScheduler.Default);
    }
}
