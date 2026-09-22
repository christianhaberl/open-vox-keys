// Diagnostics must not prevent recording, insertion, cancellation, or error recovery.
static class AppLog
{
    internal static void Write(string path, string message)
    {
        try { File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    internal static void Test()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ovk-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "state.log");
            Write(path, "first");
            if (!File.ReadAllText(path).Contains("first")) throw new Exception("Logging failed to write normally");
            using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Write(path, "locked");
            Write(Path.Combine(directory, "missing", "state.log"), "unavailable");
            Write(directory, "not-a-file");
            Write(path, "after");
            if (!File.ReadAllText(path).Contains("after")) throw new Exception("Logging did not recover after an unavailable file");
        }
        finally { Directory.Delete(directory, true); }
    }
}
