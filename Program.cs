using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using NAudio.Wave;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--test-session")) { SessionTests.Run(); return; }
        if (args.Contains("--selftest")) { ChordState.Test(); TextInsertion.Validate(); SettingsTests.Run(); AppLog.Test(); if (AppBrand.Icon.Width <= 0) throw new Exception("Icon missing"); return; }
        ApplicationConfiguration.Initialize();
        if (args.Contains("--test-mode")) { Application.Run(new TestMode()); return; }
        if (args.Contains("--test-insertion")) { Application.Run(TextInsertion.TestWindow()); return; }
        if (args.Contains("--preview-overlay")) { Application.Run(OrbWindow.Preview()); return; }
        using var mutex = new Mutex(true, "Local\\VoicePoc", out bool owner);
        if (!owner) return;
        Application.Run(new Dictation(args));
    }
}
sealed class Dictation : OrbWindow
{
    readonly Label status = new() { Dock = DockStyle.Top, Height = 58, Text = "Hold Ctrl+Win to speak", Padding = new Padding(10) };
    readonly Form details = new() { Text = "Open Vox Keys · Last dictation", Width = 600, Height = 320, StartPosition = FormStartPosition.CenterScreen };
    readonly TextBox output = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    readonly NotifyIcon tray = new() { Icon = AppBrand.Icon, Visible = true, Text = "Open Vox Keys · Ctrl+Win" };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 25 };
    readonly ChordState chordState = new();
    readonly string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoicePoc.log");
    TextInsertion.Target insertionTarget; bool insertionCancelled;
    bool recording, busy, cancelled; DateTime started;
    DictationSettings settings = new(); LiveSession? session; WaveInEvent? microphone;
    TaskCompletionSource? captureStopped;
    public Dictation(string[] args)
    {
        Text = "Open Vox Keys"; Icon = AppBrand.Icon; details.Icon = AppBrand.Icon;
        try { settings = DictationSettings.Load(); }
        catch (Exception)
        {
            string backup;
            try { backup = SettingsRecovery.Preserve(DictationSettings.FilePath); }
            catch (Exception)
            {
                MessageBox.Show("Settings could not be loaded or backed up. Open Vox Keys will exit without replacing them. Check access to " + DictationSettings.FilePath, "Open Vox Keys · Settings error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Shown += (_, _) => Close();
                return;
            }
            MessageBox.Show("Settings could not be loaded. The original file is preserved at:\n" + backup + "\n\nConfigure your endpoints again in Settings. API keys protected for another Windows user or PC must be entered again.", "Open Vox Keys · Settings recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        details.Controls.Add(output); details.Controls.Add(status);
        details.FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; details.Hide(); } };
        var buttons = new FlowLayoutPanel() { Dock = DockStyle.Bottom, Height = 38 };
        var copy = new Button() { Text = "Copy" }; copy.Click += (_, _) => { if (output.Text.Length > 0) Clipboard.SetText(output.Text); }; buttons.Controls.Add(copy);
        var hide = new Button() { Text = "Hide" }; hide.Click += (_, _) => details.Hide(); buttons.Controls.Add(hide);
        var cancel = new Button() { Text = "Cancel" }; cancel.Click += (_, _) => Cancel(); buttons.Controls.Add(cancel); details.Controls.Add(buttons);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, async (_, _) => { if (recording || busy) return; timer.Stop(); try { if (DictationSettings.Edit(details, settings)) { settings = DictationSettings.Load(); await WarmFallback(); } } finally { chordState.Update(false, false); timer.Start(); } });
        menu.Items.Add("Microphones", null, (_, _) =>
        {
            string list = "-1: Windows default microphone\n";
            for (int i = 0; i < WaveIn.DeviceCount; i++) list += $"{i}: {WaveIn.GetCapabilities(i).ProductName}\n";
            MessageBox.Show(details, list + "\nSelect a device in Settings → MicrophoneDevice.", "Open Vox Keys · Microphones");
        });
        menu.Items.Add("Check connection", null, async (_, _) => { if (!recording && !busy) await CheckConnection(); });
        menu.Items.Add("Test mode", null, (_, _) => { if (recording || busy) return; timer.Stop(); try { using var test = new TestMode(); test.ShowDialog(); } finally { chordState.Update(false, false); timer.Start(); } });
        menu.Items.Add("Last dictation", null, (_, _) => details.Show()); menu.Items.Add("Quit", null, (_, _) => Close()); tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => details.Show(); tray.BalloonTipClicked += (_, _) => details.Show();
        timer.Tick += (_, _) =>
        {
            bool ctrl = Native.GetAsyncKeyState(0x11) < 0;
            bool win = Native.GetAsyncKeyState(0x5B) < 0 || Native.GetAsyncKeyState(0x5C) < 0;
            if (recording || busy) { if (TextInsertion.Capture() != insertionTarget || TextInsertion.OtherInputDown()) insertionCancelled = true; }
            int action = chordState.Update(ctrl, win);
            if (action == 1 && !busy && !recording) Start();
            if (action == -1) Stop();
            if ((recording || busy) && Native.GetAsyncKeyState(0x1B) < 0) Cancel();
            if (recording && DateTime.UtcNow - started > TimeSpan.FromMinutes(10)) Stop();
        }; timer.Start();
        Shown += async (_, _) =>
        {
            Log("ready hotkey=Ctrl+Win mode=hold streaming=true"); Hide();
            if (args.Length == 2 && args[0] == "--transcribe-file")
            {
                busy = true; timer.Stop();
                try
                {
                    using var fixture = new LiveSession(settings, _ => { }, _ => { });
                    using var reader = new WaveFileReader(args[1]);
                    if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1 || reader.WaveFormat.BitsPerSample != 16) throw new Exception("Fixture must be 16 kHz mono PCM16");
                    var bytes = new byte[2048]; int n; var sw = Stopwatch.StartNew(); long sent = 0;
                    while ((n = reader.Read(bytes, 0, bytes.Length)) > 0) { fixture.Feed(bytes[..n]); sent += n; int delay = (int)(sent / 32.0 - sw.Elapsed.TotalMilliseconds); if (delay > 0) await Task.Delay(delay); }
                    var tail = Stopwatch.StartNew(); string text = await fixture.Finish();
                    var file = Environment.GetEnvironmentVariable("OVK_FIXTURE_RESULT"); if (file != null) File.WriteAllText(file, text);
                    Log("fixture-ok source=" + fixture.Source + " chars=" + text.Length + " tail_s=" + tail.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception e) { Log("fixture-error " + e.Message); Environment.ExitCode = 1; }
                busy = false; Close();
            }
            else { await WarmFallback(); if (settings.GatewayUrl.Length == 0 && settings.FallbackUrl.Length == 0) { timer.Stop(); try { if (DictationSettings.Edit(details, settings)) settings = DictationSettings.Load(); } finally { timer.Start(); } } }
        };
    }
    void Ui(Action f) { if (!IsDisposed && IsHandleCreated) try { BeginInvoke(f); } catch (InvalidOperationException) { } }
    void Log(string s) => AppLog.Write(log, s);
    async Task WarmFallback()
    {
        if (settings.FallbackUrl.Length == 0 || !new Uri(settings.FallbackUrl).IsLoopback) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            if (settings.FallbackApiKey.Length > 0) http.DefaultRequestHeaders.Authorization = new("Bearer", settings.FallbackApiKey);
            var url = new Uri(new Uri(settings.FallbackUrl), "/warm");
            using var body = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { keep_warm = settings.KeepFallbackWarm }), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(url, body); Log("fallback-warm-policy status=" + (int)response.StatusCode);
        }
        catch (Exception) { Log("fallback-warm-policy unavailable"); }
    }
    async Task CheckConnection()
    {
        if (settings.GatewayUrl.Length == 0) { status.Text = settings.FallbackUrl.Length == 0 ? "No endpoint configured. Open Settings." : "File ASR configured. Availability is checked when you dictate; no audio was sent."; Notice(); return; }
        try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; if (settings.GatewayApiKey.Length > 0) http.DefaultRequestHeaders.Authorization = new("Bearer", settings.GatewayApiKey); using var r = await http.GetAsync(settings.GatewayUrl.TrimEnd('/') + "/health"); r.EnsureSuccessStatusCode(); status.Text = "Gateway available · " + await r.Content.ReadAsStringAsync(); }
        catch (Exception) { status.Text = "Gateway unavailable. File ASR: " + (settings.FallbackUrl.Length > 0 ? "configured" : "not configured"); }
        Notice();
    }
    void Start()
    {
        if (recording || busy) return;
        cancelled = false; insertionCancelled = false; insertionTarget = TextInsertion.Capture(); output.Clear(); LiveText = ""; PlaceOrb(insertionTarget.Window); Show(); status.Text = "● Recording — release Ctrl+Win";
        try
        {
            var active = new LiveSession(settings, text => Ui(() => { if (recording && !cancelled) LiveText = text; }), text => Ui(() => { if (!cancelled) { status.Text = text; if (busy) Notice(); } })); session = active;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); captureStopped = done;
            var input = new WaveInEvent { DeviceNumber = settings.MicrophoneDevice, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 64, NumberOfBuffers = 4 }; microphone = input;
            input.DataAvailable += (_, e) => { byte[] bytes = e.Buffer[..e.BytesRecorded]; active.Feed(bytes); double sum = 0; for (int i = 0; i + 1 < bytes.Length; i += 2) { double x = BitConverter.ToInt16(bytes, i) / 32768.0; sum += x * x; } float level = (float)Math.Min(1, Math.Sqrt(sum / Math.Max(1, bytes.Length / 2)) * 7); Ui(() => AudioLevel = level); };
            input.RecordingStopped += (_, e) =>
            {
                if (e.Exception == null) { done.TrySetResult(); return; }
                done.TrySetException(e.Exception);
                Ui(() =>
                {
                    // A device failure must end capture even while the user holds the hotkey.
                    // Ignore callbacks belonging to a cancelled, closed, or replaced capture.
                    if (recording && !cancelled && ReferenceEquals(microphone, input)) Stop();
                });
            };
            recording = true; started = DateTime.UtcNow; input.StartRecording(); Log("recording-start streaming=true");
        }
        catch (Exception e) { recording = false; microphone?.Dispose(); microphone = null; session?.Dispose(); session = null; status.Text = "Microphone error: " + e.Message; Log("recording-error " + e.Message); Hide(); Notice(); }
    }
    async void Stop()
    {
        if (!recording) return;
        recording = false; busy = true; Hide(); status.Text = "Finishing the last section …"; var watch = Stopwatch.StartNew();
        var active = session; var input = microphone; var done = captureStopped;
        try
        {
            input?.StopRecording(); if (done != null) await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
            input?.Dispose(); microphone = null;
            Log("capture " + active!.AudioSummary + " device=" + settings.MicrophoneDevice);
            string text = await active.Finish(); if (cancelled) return;
            output.Text = text; status.Text = text.Length == 0 ? "No speech detected." : $"Done · {watch.Elapsed.TotalSeconds:F1} s · {active.Source}";
            Log("result source=" + active.Source + " release_s=" + watch.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " chars=" + text.Length);
            if (text.Length > 0)
            {
                for (int n = 0; n < 50 && TextInsertion.ModifiersDown() && !cancelled && !insertionCancelled; n++) await Task.Delay(20);
                await Task.Delay(100);
                if (!cancelled && !insertionCancelled && TextInsertion.TryInsert(insertionTarget, text))
                {
                    status.Text = $"Inserted · {watch.Elapsed.TotalSeconds:F1} s · {active.Source}"; Log("unicode-input-submitted chars=" + text.Length); Hide();
                }
                else if (!cancelled) { status.Text = "Text ready — focus or input changed. Use Copy to insert it."; Log("insertion-skipped"); Notice(); }
            }
        }
        catch (Exception e) { if (!cancelled) { status.Text = "Error — " + e.Message; try { string saved = active?.SaveRecovery() ?? ""; if (saved.Length > 0) status.Text += " · Recording saved in OpenVoxKeys/Recovery."; } catch (Exception) { status.Text += " · Could not save the recording."; } Log("error " + e.Message); Notice(); details.Show(); } }
        finally { input?.Dispose(); active?.Dispose(); session = null; microphone = null; busy = false; }
    }
    void Notice() => tray.ShowBalloonTip(4000, "Open Vox Keys", status.Text, ToolTipIcon.Info);
    void Cancel() { Hide(); cancelled = true; session?.Cancel(); if (recording) { Stop(); } status.Text = "Cancelled"; Log("cancelled"); }
    protected override void OnFormClosed(FormClosedEventArgs e) { timer.Stop(); cancelled = true; session?.Cancel(); microphone?.StopRecording(); tray.Dispose(); details.Dispose(); base.OnFormClosed(e); }
}
// Passive observation only: no hooks, suppression or synthetic keyboard input.
sealed class ChordState
{
    bool armed = true, active;
    internal int Update(bool ctrl, bool win)
    {
        if (!ctrl && !win) { armed = true; if (active) { active = false; return -1; } return 0; }
        if (ctrl && win && armed) { armed = false; active = true; return 1; }
        return 0;
    }
    internal static void Test()
    {
        foreach (bool ctrlFirst in new[] { false, true }) foreach (bool ctrlReleasedFirst in new[] { false, true })
            {
                var s = new ChordState();
                if (s.Update(false, false) != 0 || s.Update(ctrlFirst, !ctrlFirst) != 0 || s.Update(true, true) != 1 || s.Update(true, true) != 0 || s.Update(!ctrlReleasedFirst, ctrlReleasedFirst) != 0 || s.Update(false, false) != -1 || s.Update(false, false) != 0 || s.Update(true, true) != 1) throw new Exception("Chord state test failed");
            }
    }
}
static class Native
{
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] internal static extern uint mciSendStringW(string cmd, StringBuilder? output, int n, nint callback);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] internal static extern bool mciGetErrorStringW(uint error, StringBuilder output, int n);
}
