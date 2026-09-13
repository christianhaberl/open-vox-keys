using System.Text.Json;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

sealed class DictationSettings
{
    public string GatewayUrl { get; set; } = "";
    [PasswordPropertyText(true)]
    public string GatewayApiKey { get; set; } = "";
    public string Language { get; set; } = "en";
    [Description("-1 = Windows default microphone; see the tray microphone list for other device numbers.")]
    public int MicrophoneDevice { get; set; } = 0;
    public bool StartWithWindows { get; set; } = false;
    public double PauseSeconds { get; set; } = .5;
    public double DecisionDelaySeconds { get; set; } = 1.2;
    public double MaximumSectionSeconds { get; set; } = 20;
    public double BatchTimeoutSeconds { get; set; } = 8;
    public double FinishTimeoutSeconds { get; set; } = 30;
    public bool StreamFallback { get; set; } = true;
    public string FallbackUrl { get; set; } = "";
    public string FallbackModel { get; set; } = "parakeet-tdt-0.6b-v3";
    [PasswordPropertyText(true)]
    public string FallbackApiKey { get; set; } = "";
    public double FallbackTimeoutSeconds { get; set; } = 90;
    public bool KeepFallbackWarm { get; set; } = false;
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVoxKeys", "settings.json");
    public static DictationSettings Load() => LoadFrom(FilePath);
    internal static DictationSettings LoadFrom(string path)
    {
        if (!File.Exists(path)) return new();
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        bool migrate = false;
        foreach (var name in new[] { nameof(GatewayApiKey), nameof(FallbackApiKey) })
        {
            string value = node[name]?.GetValue<string>() ?? "";
            if (value.StartsWith("dpapi:")) node[name] = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), null, DataProtectionScope.CurrentUser));
            else if (value.Length > 0) migrate = true;
        }
        var settings = node.Deserialize<DictationSettings>() ?? new(); settings.Validate();
        if (migrate) settings.SaveTo(path);
        return settings;
    }
    internal void SaveTo(string path)
    {
        Validate();
        var node = JsonSerializer.SerializeToNode(this)!.AsObject();
        foreach (var name in new[] { nameof(GatewayApiKey), nameof(FallbackApiKey) })
        {
            string value = node[name]!.GetValue<string>();
            node[name] = value.Length == 0 ? "" : "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Validate()
    {
        foreach (var u in new[] { GatewayUrl, FallbackUrl }) if (u.Length > 0 && (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new Exception("Endpoints require http:// or https://.");
        if (PauseSeconds is < .1 or > 3 || DecisionDelaySeconds is < 0 or > 5 || MaximumSectionSeconds is < 3 or > 60 || BatchTimeoutSeconds is < 1 or > 120 || FinishTimeoutSeconds is < 5 or > 180 || FallbackTimeoutSeconds is < 5 or > 300) throw new Exception("A timeout or segmentation value is outside the allowed range.");
    }
    public void Save()
    {
        SaveTo(FilePath);
        using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (StartWithWindows) run.SetValue("OpenVoxKeys", "\"" + Application.ExecutablePath + "\""); else run.DeleteValue("OpenVoxKeys", false);
    }
    public static bool Edit(IWin32Window owner, DictationSettings settings)
    {
        using var form = new Form { Text = "Open Vox Keys · Settings", Width = 600, Height = 620, StartPosition = FormStartPosition.CenterScreen };
        var clone = JsonSerializer.Deserialize<DictationSettings>(JsonSerializer.Serialize(settings))!;
        var grid = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = clone, HelpVisible = true };
        var label = new Label { Dock = DockStyle.Top, Height = 75, Padding = new Padding(8), Text = "GatewayUrl: optional streaming gateway base URL.\nFallbackUrl: full file-transcription URL (local, network or cloud).\nLeave GatewayUrl empty to use file transcription directly. Changes apply to the next dictation." };
        var save = new Button { Text = "Save", Dock = DockStyle.Bottom, Height = 36 };
        save.Click += (_, _) => { try { clone.Save(); form.DialogResult = DialogResult.OK; } catch (Exception e) { MessageBox.Show(form, e.Message); } };
        form.Controls.Add(grid); form.Controls.Add(label); form.Controls.Add(save);
        return form.ShowDialog(owner) == DialogResult.OK;
    }
}
