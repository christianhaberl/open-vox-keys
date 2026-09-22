using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

static class Setup
{
    static readonly string InstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenVoxKeys");
    static readonly string ProductExe = Path.Combine(InstallPath, "OpenVoxKeys.exe");

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--verify-package"))
        {
            try { VerifyPackage(); } catch { Environment.ExitCode = 1; }
            return;
        }
        bool uninstall = args.Contains("--uninstall") || args.Contains("--uninstall-worker");
        // Run removal outside the installed directory so the installer can remove itself.
        if (args.Contains("--uninstall"))
        {
            string copy = Path.Combine(Path.GetTempPath(), "OpenVoxKeys-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(Environment.ProcessPath!, copy);
            Process.Start(new ProcessStartInfo(copy, "--uninstall-worker") { UseShellExecute = true });
            return;
        }
        ApplicationConfiguration.Initialize();
        using var form = new Form { Text = uninstall ? "Uninstall Open Vox Keys" : "Install Open Vox Keys", ClientSize = new Size(600, 290), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
        using var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("SetupIcon")!;
        form.Icon = new Icon(icon);
        var heading = new Label { Left = 24, Top = 20, Width = 550, Height = 38, Font = new Font(SystemFonts.DefaultFont.FontFamily, 17), Text = "Open Vox Keys" };
        var info = new Label { Left = 24, Top = 68, Width = 550, Height = 90, Text = uninstall
            ? "Remove the application and its startup entry.\nYour settings, API keys, and recordings will be retained.\nQuit Open Vox Keys from its tray menu before continuing."
            : "Install for your Windows user. No administrator or separate .NET runtime is needed.\n\n" + InstallPath };
        var startup = new CheckBox { Left = 24, Top = 160, Width = 540, Text = "Start Open Vox Keys when I sign in to Windows", Checked = true, Visible = !uninstall };
        var status = new Label { Left = 24, Top = 194, Width = 550, Height = 42, AutoEllipsis = true, Text = uninstall ? "" : "MIT license. ASR endpoint required; model weights are not included." };
        var action = new Button { Left = 410, Top = 246, Width = 165, Height = 32, Text = uninstall ? "Uninstall" : "Install" };
        bool working = false, finished = false;
        form.Controls.AddRange(new Control[] { heading, info, startup, status, action });
        form.FormClosing += (_, e) => { if (working) e.Cancel = true; };
        action.Click += async (_, _) =>
        {
            if (finished) { form.Close(); return; }
            working = true; action.Enabled = startup.Enabled = false;
            bool autostart = startup.Checked;
            status.Text = uninstall ? "Removing application…" : "Installing…";
            try
            {
                await Task.Run(() => { if (uninstall) Remove(); else Install(autostart); });
                finished = true;
                status.Text = uninstall ? "Removed. Your personal data was retained." : "Installed. Open Vox Keys is starting; configure your provider in Settings.";
                if (!uninstall) Process.Start(new ProcessStartInfo(ProductExe) { UseShellExecute = true, WorkingDirectory = InstallPath });
                action.Text = "Close";
            }
            catch (Exception e) { status.Text = uninstall ? "Removal could not complete." : "Installation could not complete."; MessageBox.Show(form, e.Message, "Open Vox Keys Setup", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { working = false; action.Enabled = true; startup.Enabled = !finished; }
        };
        Application.Run(form);
    }

    static string Extract()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenVoxKeys-Package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("Package.zip") ?? throw new IOException("Installer payload missing.");
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            archive.ExtractToDirectory(directory);
            if (!File.Exists(Path.Combine(directory, "OpenVoxKeys.exe")) || !File.Exists(Path.Combine(directory, "tools", "Install.ps1"))) throw new IOException("Incomplete installer payload.");
            return directory;
        }
        catch { Directory.Delete(directory, true); throw; }
    }
    static void RunScript(string script, params string[] switches)
    {
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(switches)) start.ArgumentList.Add(a);
        using var p = Process.Start(start) ?? throw new IOException("Could not start the installation helper.");
        var output = p.StandardOutput.ReadToEndAsync(); var errors = p.StandardError.ReadToEndAsync();
        // The helper owns rollback. Killing it during a slow file/registry operation
        // can strand the reserved installation directory. Let the transaction finish.
        p.WaitForExit();
        Task.WaitAll(output, errors);
        if (p.ExitCode != 0) throw new IOException(errors.Result.Trim().Length > 0 ? errors.Result.Trim() : output.Result.Trim());
    }
    static void Install(bool autostart)
    {
        string source = Extract();
        try
        {
            RunScript(Path.Combine(source, "tools", "Install.ps1"),
                autostart ? "-Autostart" : "-DisableAutostart", "-SetupExecutable", Environment.ProcessPath!);
        }
        finally { Directory.Delete(source, true); }
    }
    static void Remove()
    {
        RunScript(Path.Combine(InstallPath, "tools", "Install.ps1"), "-Uninstall");
    }
    static void VerifyPackage()
    {
        string source = Extract();
        try
        {
            foreach (string flag in new[] { "--selftest", "--test-session" })
            {
                using var p = Process.Start(new ProcessStartInfo(Path.Combine(source, "OpenVoxKeys.exe"), flag) { UseShellExecute = false })!;
                if (!p.WaitForExit(60000)) { p.Kill(); throw new IOException("Payload test timed out."); }
                if (p.ExitCode != 0) throw new IOException("Payload test failed: " + flag);
            }
        }
        finally { Directory.Delete(source, true); }
    }
}
