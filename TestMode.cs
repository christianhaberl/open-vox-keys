using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

sealed class TestMode : Form
{
    readonly string[] sentences = {
  "After work on Friday, I thought I would take the tram to the supermarket.",
  "I need a bag of bread rolls, a few tomatoes, and two bottles of mineral water.",
  "At the checkout, I noticed that my wallet was still on the windowsill in the office.",
  "Please move our appointment from next Thursday to Friday at two thirty in the afternoon.",
  "The amount is one thousand two hundred and thirty-four euros and fifty-six cents.",
  "I need three screws, two cables, and one adapter, but no new monitor.",
  "Have you read the message already, or should I send it again?",
  "This does not always work, especially when someone is speaking in the background.",
  "We did not cancel the order; we only changed the delivery address.",
  "Anna meets Robert at Vienna Central Station before travelling to Salzburg.",
  "The planned application should support Windows, Linux, and macOS, with local and remote models.",
  "Please send me the link to the GitHub repository and the current API documentation.",
  "I would like an appointment next Thursday at four. Actually, make that five.",
  "Although the connection briefly dropped, the entire recording was saved.",
  "That was not the original plan, but in hindsight it was the right decision.",
  "Today I would like to plan our trip. First, we take the tram to the station. There, we buy tickets and meet the others. If it rains, we visit the museum. If the sun shines, we walk along the river. For the evening, I will reserve a table for six people. Two of them are vegetarian. Please remind me tomorrow to confirm the reservation.",
  "For our next project, we need a clear process. First, we collect the requirements. Then we build a small prototype and test it with several people. We care about recognition quality and waiting time. A fast response is not helpful if important words are missing. On the other hand, a good transcription should not take too long. Finally, we compare the results and decide which version to use every day."
};
    readonly TextBox reference = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    readonly TextBox language = new() { Width = 50, Text = "en" };
    readonly TextBox endpoint = new() { Width = 310 };
    readonly TextBox models = new() { Width = 290 };
    readonly TextBox key = new() { Width = 160, UseSystemPasswordChar = true, PlaceholderText = "API key (memory only)" };
    readonly ComboBox protocol = new() { Width = 155, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox warm = new() { Text = "Warm-up per model", Checked = true, AutoSize = true };
    readonly Button record = new() { Text = "Record", AutoSize = true };
    readonly Button run = new() { Text = "Compare models", AutoSize = true, Enabled = false };
    readonly Button next = new() { Text = "Next text", AutoSize = true };
    readonly Button cancel = new() { Text = "Cancel", AutoSize = true };
    readonly Label status = new() { AutoSize = true, Text = "Record → stop → compare models. Audio is saved locally." };
    readonly TextBox results = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both };
    readonly string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVoxKeys", "Tests", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
    readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    string? audio; bool recording, busy; int index; Stopwatch clock = new(); CancellationTokenSource? cts;
    public TestMode()
    {
        Text = "Open Vox Keys · Model comparison"; Width = 1100; Height = 780; StartPosition = FormStartPosition.CenterScreen;
        endpoint.Text = Environment.GetEnvironmentVariable("VOICE_POC_ENDPOINT") ?? "";
        models.Text = Environment.GetEnvironmentVariable("VOICE_POC_MODEL") ?? "parakeet-tdt-0.6b-v3";
        protocol.Items.AddRange(new object[] { "llama-swap inference", "OpenAI compatible" }); protocol.SelectedIndex = 0;
        var layout = new TableLayoutPanel() { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new(SizeType.Absolute, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 160)); layout.RowStyles.Add(new(SizeType.Absolute, 45)); layout.RowStyles.Add(new(SizeType.Absolute, 50)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        var settings = new FlowLayoutPanel() { Dock = DockStyle.Fill, AutoScroll = true };
        settings.Controls.AddRange(new Control[] { new Label() { Text = "Endpoint / base URL", AutoSize = true }, endpoint, protocol, key, new Label() { Text = "Models (comma-separated)", AutoSize = true }, models, warm, new Label() { Text = "Language", AutoSize = true }, language });
        var buttons = new FlowLayoutPanel() { Dock = DockStyle.Fill }; buttons.Controls.AddRange(new Control[] { record, run, next, cancel });
        layout.Controls.Add(settings, 0, 0); layout.Controls.Add(reference, 0, 1); layout.Controls.Add(buttons, 0, 2); layout.Controls.Add(status, 0, 3); layout.Controls.Add(results, 0, 4); Controls.Add(layout);
        reference.Text = sentences[0];
        next.Click += (_, _) => { index = (index + 1) % sentences.Length; reference.Text = sentences[index]; audio = null; run.Enabled = false; status.Text = $"Text {index + 1}/{sentences.Length} · Edit the reference to match what you actually said."; };
        record.Click += (_, _) => { try { if (recording) StopRecording(); else StartRecording(); } catch (Exception ex) { CloseMic(); recording = false; SetBusy(false); status.Text = "Recording error: " + ex.Message; } };
        run.Click += async (_, _) => await Compare(); cancel.Click += (_, _) => { cts?.Cancel(); if (recording) { CloseMic(); recording = false; audio = null; SetBusy(false); status.Text = "Recording cancelled."; } };
        timer.Tick += (_, _) => { if (recording) { status.Text = $"Recording · {clock.Elapsed.TotalSeconds:F1} s · Press Stop to finish · 10-minute limit"; if (clock.Elapsed.TotalMinutes >= 10) record.PerformClick(); } }; timer.Start();
        FormClosing += (_, e) => { if (busy) { cts?.Cancel(); e.Cancel = true; status.Text = "Cancelling; close the window when finished."; } else CloseMic(); };
        FormClosed += (_, _) => timer.Dispose();
    }
    void Mci(string command) { var b = new StringBuilder(256); uint error = Native.mciSendStringW(command, b, b.Capacity, 0); if (error != 0) { Native.mciGetErrorStringW(error, b, b.Capacity); throw new Exception(b.ToString()); } }
    void CloseMic() => Native.mciSendStringW("close ovktest", null, 0, 0);
    void SetBusy(bool value) { busy = value; language.Enabled = endpoint.Enabled = models.Enabled = protocol.Enabled = key.Enabled = warm.Enabled = next.Enabled = reference.Enabled = !value && !recording; record.Enabled = !value; record.Text = recording ? "Stop recording" : "Record"; run.Enabled = !value && !recording && audio != null; }
    void StartRecording() { Directory.CreateDirectory(folder); audio = null; var candidate = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".wav"); Mci("open new type waveaudio alias ovktest"); Mci("set ovktest time format milliseconds"); Mci("set ovktest channels 1 samplespersec 16000 bitspersample 16 alignment 2 bytespersec 32000"); Mci("record ovktest"); audio = candidate; recording = true; clock.Restart(); SetBusy(false); }
    void StopRecording() { Mci("stop ovktest"); Mci("save ovktest \"" + audio + "\""); CloseMic(); recording = false; clock.Stop(); if (new FileInfo(audio!).Length <= 44) { audio = null; throw new Exception("No audio data."); } File.WriteAllText(Path.ChangeExtension(audio!, ".reference.txt"), reference.Text); SetBusy(false); status.Text = $"Recording saved ({clock.Elapsed.TotalSeconds:F1} s). Compare sends it to the configured endpoint."; }
    async Task Compare()
    {
        if (audio == null) return;
        if (!Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) { status.Text = "Enter a valid HTTP(S) base URL."; return; }
        var selected = models.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray(); if (selected.Length == 0) { status.Text = "Enter at least one model."; return; }
        SetBusy(true); cts = new(); var token = cts.Token; var rows = new List<object>(); string truth = reference.Text; var bytes = File.ReadAllBytes(audio); bool openAI = protocol.SelectedIndex == 1;
        try
        {
            using var http = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
            if (key.Text.Length > 0) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Text.Trim());
            async Task<string> Call(string model)
            {
                using var form = new MultipartFormDataContent(); var part = new ByteArrayContent(bytes); part.Headers.ContentType = new("audio/wav"); form.Add(part, "file", "dictation.wav"); form.Add(new StringContent(language.Text.Trim()), "language"); form.Add(new StringContent("json"), "response_format");
                if (openAI) form.Add(new StringContent(model), "model");
                var path = openAI ? "/audio/transcriptions" : "/upstream/" + Uri.EscapeDataString(model) + "/inference";
                using var response = await http.PostAsync(uri.ToString().TrimEnd('/') + path, form, token); response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); return json.RootElement.GetProperty("text").GetString() ?? "";
            }
            foreach (var model in selected)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    double? preparation = null;
                    if (warm.Checked) { status.Text = model + " · Warm-up (same recording, not scored)"; var prep = Stopwatch.StartNew(); await Call(model); preparation = prep.Elapsed.TotalSeconds; }
                    status.Text = model + " · Measured request (serial)"; var watch = Stopwatch.StartNew(); string text = await Call(model); watch.Stop();
                    rows.Add(new { model, text, seconds = watch.Elapsed.TotalSeconds, warmupSeconds = preparation, status = "ok" });
                    results.AppendText($"{model} · {watch.Elapsed.TotalSeconds:F2} s HTTP end-to-end · {(preparation.HasValue ? "after warm-up" : "without warm-up")}\r\n{text}\r\n\r\n");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { rows.Add(new { model, status = "error", error = ex.Message }); results.AppendText(model + " · ERROR: " + ex.Message + "\r\n\r\n"); }
            }
            status.Text = "Done · Results and WAV: " + folder;
        }
        catch (OperationCanceledException) { status.Text = "Comparison cancelled. Completed results are retained."; }
        catch (Exception ex) { status.Text = "Error: " + ex.Message; }
        finally
        {
            try { File.WriteAllText(Path.Combine(folder, Path.GetFileNameWithoutExtension(audio) + "-" + DateTime.Now.ToString("HHmmssfff") + ".json"), JsonSerializer.Serialize(new { reference = truth, audio = Path.GetFileName(audio), mode = "whole-recording-serial", results = rows }, new JsonSerializerOptions() { WriteIndented = true })); } catch (Exception ex) { results.AppendText("Save failed: " + ex.Message); }
            cts.Dispose(); cts = null; SetBusy(false);
        }
    }
}
