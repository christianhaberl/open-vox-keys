using System.Text.Json;

static class SettingsTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ovk-settings-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new DictationSettings { GatewayApiKey = "test-gateway-secret", FallbackApiKey = "test-file-secret", Language = "de" };
            settings.SaveTo(path);
            string encrypted = File.ReadAllText(path);
            if (encrypted.Contains("test-gateway-secret") || encrypted.Contains("test-file-secret") || !encrypted.Contains("dpapi:")) throw new Exception("Settings expose plaintext keys");
            var loaded = DictationSettings.LoadFrom(path);
            if (loaded.GatewayApiKey != settings.GatewayApiKey || loaded.FallbackApiKey != settings.FallbackApiKey || loaded.Language != "de") throw new Exception("Settings roundtrip failed");
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
            loaded = DictationSettings.LoadFrom(path);
            if (loaded.GatewayApiKey != settings.GatewayApiKey || File.ReadAllText(path).Contains("test-gateway-secret")) throw new Exception("Plaintext migration failed");
            const string corrupt = "{\"GatewayApiKey\":\"dpapi:invalid\"}";
            File.WriteAllText(path, corrupt);
            bool rejected = false;
            try { DictationSettings.LoadFrom(path); } catch (FormatException) { rejected = true; }
            if (!rejected || File.ReadAllText(path) != corrupt) throw new Exception("Corrupt settings were overwritten");
            settings.GatewayUrl = "file:///private";
            rejected = false;
            try { settings.SaveTo(path); } catch { rejected = true; }
            if (!rejected || File.ReadAllText(path) != corrupt) throw new Exception("Invalid endpoint was saved");
        }
        finally { Directory.Delete(directory, true); }
    }
}
