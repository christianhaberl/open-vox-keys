static class SettingsRecovery
{
    internal static string Preserve(string path)
    {
        string backup = path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
        // Preserve the original bytes, including encrypted keys; never deserialize secrets here.
        File.Copy(path, backup, false);
        return backup;
    }
}
