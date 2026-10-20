using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

// Microphone capture that always delivers 16 kHz mono PCM16.
// WASAPI shared mode delivers the first buffer after ~10-30 ms; WaveIn (MME) needs 70-120 ms and stays as the fallback.
sealed class Capture : IDisposable
{
    public event Action<byte[]>? Data;
    public event Action<Exception?>? Stopped;
    public string Api { get; private set; } = "";
    public string FallbackReason { get; private set; } = "";
    static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    IWaveIn? input;
    WdlResampler? resampler;
    int channels, bytesPerSample, rate;
    bool isFloat;

    public void Start(int waveInDevice, bool preferWasapi)
    {
        if (preferWasapi)
        {
            try { StartWasapi(waveInDevice); return; }
            catch (Exception e) { input?.Dispose(); input = null; FallbackReason = e.GetType().Name + ": " + e.Message; lock (deviceLock) cached = null; }
        }
        StartWaveIn(waveInDevice);
    }
    public void Stop() => input?.StopRecording();

    // First WASAPI use in a process costs ~350 ms (COM enumerator, audio client, JIT). Pay it at app start without opening the microphone.
    internal static void Warm(int device)
    {
        try { var endpoint = CachedDevice(device); if (endpoint == null) return; using var client = endpoint.AudioClient; _ = client.MixFormat; var r = new WdlResampler(); r.SetMode(true, 2, false); r.SetFilterParms(); r.SetFeedMode(true); r.SetRates(48000, 16000); }
        catch (Exception) { }
    }

    // Records 3 s with each API and writes start latency, sample count and level to %LOCALAPPDATA%\OpenVoxKeys\capture-test.txt.
    internal static void Test(int device)
    {
        var lines = new List<string>(); Warm(device);
        foreach (bool wasapi in new[] { true, false, true, false, true })
        {
            using var capture = new Capture(); var clock = System.Diagnostics.Stopwatch.StartNew(); long first = -1, samples = 0; int peak = 0;
            var done = new ManualResetEventSlim();
            capture.Data += b => { if (first < 0) first = clock.ElapsedMilliseconds; samples += b.Length / 2; for (int i = 0; i + 1 < b.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(b, i))); };
            capture.Stopped += _ => done.Set();
            try { capture.Start(device, wasapi); Thread.Sleep(3000); capture.Stop(); done.Wait(3000); lines.Add($"requested={(wasapi ? "wasapi" : "wavein")} api={capture.Api} first_audio_ms={first} samples={samples} expected~48000 peak={peak}"); }
            catch (Exception e) { lines.Add($"requested={(wasapi ? "wasapi" : "wavein")} error={e.Message}"); Environment.ExitCode = 1; }
        }
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVoxKeys"); Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "capture-test.txt"), lines);
    }
    public void Dispose() { input?.Dispose(); input = null; }

    // WaveIn numbers devices and truncates names to 31 characters; match the WASAPI endpoint by that prefix.
    // Enumerator and endpoint are kept between dictations; re-creating them costs ~200 ms after a long idle.
    // They are COM objects bound to the creating apartment: create and use them on the UI thread only.
    static readonly object deviceLock = new();
    static MMDeviceEnumerator? sharedEnumerator;
    static (int index, MMDevice device)? cached;
    static MMDevice? CachedDevice(int waveInDevice)
    {
        lock (deviceLock)
        {
            if (cached is { } c && c.index == waveInDevice && c.device.State == DeviceState.Active) return c.device;
            var device = FindDevice(waveInDevice); cached = device == null ? null : (waveInDevice, device); return device;
        }
    }
    internal static MMDevice? FindDevice(int waveInDevice)
    {
        var enumerator = sharedEnumerator ??= new MMDeviceEnumerator();
        if (waveInDevice < 0) return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        if (waveInDevice >= WaveIn.DeviceCount) return null;
        string name = WaveIn.GetCapabilities(waveInDevice).ProductName.Trim();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.FriendlyName.StartsWith(name, StringComparison.OrdinalIgnoreCase));
    }

    void StartWasapi(int waveInDevice)
    {
        var device = CachedDevice(waveInDevice) ?? throw new InvalidOperationException("No matching WASAPI capture device.");
        var capture = new WasapiCapture(device, true, 20);
        var format = capture.WaveFormat;
        isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubtype);
        bytesPerSample = format.BitsPerSample / 8; channels = format.Channels; rate = format.SampleRate;
        if (!(isFloat && bytesPerSample == 4) && !(!isFloat && bytesPerSample == 2)) { capture.Dispose(); throw new NotSupportedException("Unsupported WASAPI sample format."); }
        resampler = new WdlResampler(); resampler.SetMode(true, 2, false); resampler.SetFilterParms(); resampler.SetFeedMode(true); resampler.SetRates(rate, 16000);
        capture.DataAvailable += (_, e) => { if (e.BytesRecorded > 0) Data?.Invoke(Convert(e.Buffer, e.BytesRecorded)); };
        capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
        input = capture; Api = "wasapi";
        capture.StartRecording();
    }

    void StartWaveIn(int waveInDevice)
    {
        var wave = new WaveInEvent { DeviceNumber = waveInDevice, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 64, NumberOfBuffers = 4 };
        wave.DataAvailable += (_, e) => { if (e.BytesRecorded > 0) Data?.Invoke(e.Buffer[..e.BytesRecorded]); };
        wave.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
        input = wave; Api = "wavein";
        wave.StartRecording();
    }

    float Sample(byte[] buffer, int frame, int channel)
    {
        int at = (frame * channels + channel) * bytesPerSample;
        return isFloat ? BitConverter.ToSingle(buffer, at) : BitConverter.ToInt16(buffer, at) / 32768f;
    }

    // Down-mix: a microphone on one input of a stereo interface must not lose 6 dB by averaging with a silent channel.
    byte[] Convert(byte[] buffer, int bytes)
    {
        int frames = bytes / (bytesPerSample * channels);
        int pick = -1;
        if (channels > 1)
        {
            var energy = new double[channels];
            for (int f = 0; f < frames; f++) for (int c = 0; c < channels; c++) { float s = Sample(buffer, f, c); energy[c] += s * s; }
            int loudest = Array.IndexOf(energy, energy.Max()); double rest = energy.Sum() - energy[loudest];
            if (energy[loudest] > 4 * Math.Max(rest, 1e-12)) pick = loudest;
        }
        int needed = resampler!.ResamplePrepare(frames, 1, out float[] inBuffer, out int inOffset);
        for (int f = 0; f < Math.Min(frames, needed); f++)
        {
            float s = 0;
            if (pick >= 0) s = Sample(buffer, f, pick);
            else { for (int c = 0; c < channels; c++) s += Sample(buffer, f, c); s /= channels; }
            inBuffer[inOffset + f] = s;
        }
        int maxOut = (int)((long)frames * 16000 / rate) + 32;
        var output = new float[maxOut];
        int produced = resampler.ResampleOut(output, 0, Math.Min(frames, needed), maxOut, 1);
        var pcm = new byte[produced * 2];
        for (int i = 0; i < produced; i++) BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)Math.Clamp((int)Math.Round(output[i] * 32767f), short.MinValue, short.MaxValue));
        return pcm;
    }
}
