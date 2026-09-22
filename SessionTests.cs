using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Loopback protocol tests. No microphone access, key events or real model requests.
static class SessionTests
{
    sealed class Server : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly Task loop;
        readonly Func<NetworkStream, string, Task> handler;
        readonly List<Task> clients = new();
        public string Url { get; }
        public Server(Func<NetworkStream, string, Task> handler) { this.handler = handler; listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; loop = Accept(); }
        async Task Accept()
        {
            try { while (!stop.IsCancellationRequested) { var c = await listener.AcceptTcpClientAsync(stop.Token); clients.Add(Handle(c)); } }
            catch (OperationCanceledException) { }
        }
        async Task Handle(TcpClient client) { using (client) { var stream = client.GetStream(); using var data = new MemoryStream(); var b = new byte[1]; while (data.Length < 16384) { if (await stream.ReadAsync(b, stop.Token) == 0) throw new IOException("Incomplete header"); data.WriteByte(b[0]); if (data.Length >= 4 && Encoding.ASCII.GetString(data.GetBuffer(), (int)data.Length - 4, 4) == "\r\n\r\n") break; } await handler(stream, Encoding.ASCII.GetString(data.ToArray())); } }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); try { await loop; } catch (Exception) { } try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { } stop.Dispose(); }
    }
    static async Task<WebSocket> Upgrade(NetworkStream stream, string headers)
    {
        var line = headers.Split("\r\n").Single(x => x.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
        string key = line[(line.IndexOf(':') + 1)..].Trim(); string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"));
        return WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
    }
    static Task Send(WebSocket ws, object obj) => ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj))), WebSocketMessageType.Text, true, CancellationToken.None);
    static readonly byte[] Pcm = Enumerable.Range(0, 3200).Select(i => (byte)(i % 251)).ToArray();
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Case(string mode)
    {
        int requests = 0; bool audioIntact = false;
        await using var fallback = new Server(async (stream, headers) =>
        {
            Interlocked.Increment(ref requests);
            int length = int.Parse(headers.Split("\r\n").Single(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
            byte[] body = new byte[length]; await stream.ReadExactlyAsync(body);
            audioIntact = body.AsSpan().IndexOf(Pcm) >= 0;
            byte[] answer = Encoding.UTF8.GetBytes(mode == "both-empty" ? "{\"text\":\"\"}" : "{\"text\":\"Recovered complete.\"}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {answer.Length}\r\nConnection: close\r\n\r\n")); await stream.WriteAsync(answer);
        });
        var gotStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = new Server(async (stream, headers) =>
        {
            using var ws = await Upgrade(stream, headers); var bytes = new byte[8192];
            await ws.ReceiveAsync(new ArraySegment<byte>(bytes), CancellationToken.None); gotStart.TrySetResult();
            await Send(ws, new { type = "delta", text = "Partial preview." });
            if (mode == "early") { await Send(ws, new { type = "result", text = "Unsafe early.", source = "whisper", samples = Pcm.Length / 2 }); await Task.Delay(100); return; }
            while (true) { var m = await ws.ReceiveAsync(new ArraySegment<byte>(bytes), CancellationToken.None); if (m.MessageType == WebSocketMessageType.Close) return; if (m.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(bytes, 0, m.Count) == "stop") break; }
            if (mode == "cancel") { await Task.Delay(1500); return; }
            if (mode == "disconnect") return;
            if (mode == "error") { await Send(ws, new { type = "error", text = "Incomplete backend" }); return; }
            await Send(ws, new { type = "result", text = mode is "empty" or "both-empty" or "quiet" ? "  " : "Complete primary.", source = "whisper", samples = mode == "wrong-size" ? 1 : Pcm.Length / 2 });
        });
        var settings = new DictationSettings { GatewayUrl = mode == "direct" ? "" : gateway.Url, FallbackUrl = fallback.Url + "/v1/audio/transcriptions", FinishTimeoutSeconds = 5 };
        using var session = new LiveSession(settings, _ => { }, _ => { });
        session.Feed(mode == "silence" ? new byte[Pcm.Length] : mode == "quiet" ? Enumerable.Range(0, Pcm.Length).Select(i => (byte)(i % 2 == 0 ? 4 : 0)).ToArray() : Pcm); if (mode != "direct") await gotStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (mode == "early") await Task.Delay(200);
        var finish = session.Finish();
        if (mode == "cancel")
        {
            await Task.Delay(100); session.Cancel();
            try { await finish; throw new Exception("Cancellation returned text"); } catch (OperationCanceledException) { }
            Assert(requests == 0, "Cancellation must not invoke fallback");
        }
        else if (mode is "both-empty" or "silence" or "quiet")
        {
            bool failed = false;
            try { await finish; } catch (IOException) { failed = true; }
            Assert(failed, "Empty/silent dictation must fail explicitly");
            Assert(requests == (mode is "silence" or "quiet" ? 0 : 1), "Unexpected retries for empty/silent dictation");
        }
        else
        {
            string text = await finish.WaitAsync(TimeSpan.FromSeconds(10));
            if (mode == "valid") { Assert(text == "Complete primary." && requests == 0, "Primary result selection failed"); }
            else { Assert(text == "Recovered complete." && requests == 1 && audioIntact, "Fallback must receive full original audio exactly once: " + mode); }
        }
        session.Dispose(); session.Dispose(); session.Feed(Pcm); // Late audio callback must be harmless.
    }
    internal static void Run()
    {
        var results = new List<object>();
        foreach (string mode in new[] { "valid", "wrong-size", "early", "disconnect", "error", "cancel", "direct", "empty", "both-empty", "silence", "quiet" })
        {
            try { Case(mode).GetAwaiter().GetResult(); results.Add(new { test = mode, passed = true }); }
            catch (Exception e) { results.Add(new { test = mode, passed = false, error = e.ToString() }); Environment.ExitCode = 1; }
        }
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVoxKeys"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session-tests.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
