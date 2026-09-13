using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Per-pixel alpha: no opaque window background and no focus or mouse capture.
class OrbWindow : Form
{
    protected string LiveText = "";
    protected float AudioLevel;
    readonly System.Windows.Forms.Timer animation = new() { Interval = 33 };
    readonly System.Diagnostics.Stopwatch clock = new();
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x080800A0; return p; } }
    public OrbWindow()
    {
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        animation.Tick += (_, _) => RenderOrb();
        VisibleChanged += (_, _) => { if (Visible) { clock.Restart(); RenderOrb(); animation.Start(); } else animation.Stop(); };
    }
    protected void PlaceOrb(nint target)
    {
        var screen = Screen.FromHandle(target); var bounds = screen.Bounds;
        uint dpi = 96;
        if (GetDpiForMonitor(MonitorFromWindow(target, 2), 0, out uint monitorDpi, out _) == 0) dpi = monitorDpi;
        int logical = int.TryParse(Environment.GetEnvironmentVariable("VOICE_POC_OVERLAY_SIZE"), out int configured) ? Math.Clamp(configured, 150, 600) : 300;
        int pixels = (int)Math.Round(logical * dpi / 96.0);
        Bounds = new Rectangle(bounds.Left + (bounds.Width - pixels) / 2, bounds.Top + (bounds.Height - pixels) / 2, pixels, pixels);
    }
    internal static Form Preview()
    {
        var target = TextInsertion.Capture(); var orb = new OrbWindow();
        orb.LiveText = Environment.GetEnvironmentVariable("VOICE_POC_PREVIEW_TEXT") ?? ""; orb.AudioLevel = orb.LiveText.Length > 0 ? 1 : 0;
        orb.Shown += async (_, _) =>
        {
            orb.PlaceOrb(target.Window); orb.RenderOrb(); await Task.Delay(1200);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "voice-poc-overlay-test.txt"), $"size={orb.Width}x{orb.Height}; location={orb.Location}; focusUnchanged={TextInsertion.Capture() == target}"); orb.Close();
        };
        return orb;
    }
    void RenderOrb()
    {
        if (Width <= 0 || Height <= 0) return;
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(Width / 300f, Height / 300f);
            double t = clock.Elapsed.TotalSeconds; float breathe = (float)(0.5 + 0.5 * Math.Sin(t * 2));
            float radius = 53 + breathe * 2 + AudioLevel * 12;
            using (var auraPath = new GraphicsPath())
            {
                auraPath.AddEllipse(12, 12, 276, 276);
                using var aura = new PathGradientBrush(auraPath) { CenterColor = Color.FromArgb(100, 100, 130, 255), SurroundColors = new[] { Color.Transparent }, CenterPoint = new PointF(150, 150) };
                g.FillPath(aura, auraPath);
            }
            if (LiveText.Length > 0)
            {
                string text = LiveText.Length > 140 ? "…" + LiveText[^140..] : LiveText;
                using var font = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
                using var background = new SolidBrush(Color.FromArgb(185, 15, 20, 30));
                using var ink = new SolidBrush(Color.White);
                var rect = new RectangleF(8, 235, 284, 62); g.FillRectangle(background, rect);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
                g.DrawString(text, font, ink, rect, format);
            }
            for (int i = 0; i < 120; i++)
            {
                double angle = i * Math.PI * 2 / 120 + t * .12;
                float wave = (float)(.5 + .5 * Math.Sin(i * .41 + t * 2.1));
                float length = 8 + wave * 12 + AudioLevel * (20 + wave * 35);
                PointF a = new(150 + (float)Math.Cos(angle) * (radius + 7), 150 + (float)Math.Sin(angle) * (radius + 7));
                PointF b = new(150 + (float)Math.Cos(angle) * (radius + 7 + length), 150 + (float)Math.Sin(angle) * (radius + 7 + length));
                using var gradient = new LinearGradientBrush(a, b, Color.FromArgb(220, 90 + (int)(wave * 120), 185, 255), Color.FromArgb(0, 220, 90, 255));
                using var pen = new Pen(gradient, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round }; g.DrawLine(pen, a, b);
            }
            using var orbPath = new GraphicsPath(); var points = new PointF[180];
            for (int i = 0; i < points.Length; i++)
            {
                double a = i * Math.PI * 2 / points.Length;
                float r = radius + (float)(Math.Sin(a * 3 + t * 2) * 2 + Math.Sin(a * 5 - t * 1.6));
                points[i] = new(150 + (float)Math.Cos(a) * r, 150 + (float)Math.Sin(a) * r);
            }
            orbPath.AddClosedCurve(points);
            using var orb = new PathGradientBrush(orbPath) { CenterPoint = new PointF(133, 130), CenterColor = Color.FromArgb(235, 220, 249, 255), SurroundColors = new[] { Color.FromArgb(110, 150, 45, 230) } };
            g.FillPath(orb, orbPath); using var edge = new Pen(Color.FromArgb(190, 135, 225, 255), 1.3f); g.DrawPath(edge, orbPath);
        }
        if (Environment.GetEnvironmentVariable("VOICE_POC_RENDER_CHECK") == "1") bitmap.Save(Path.Combine(Path.GetTempPath(), "voice-poc-orb.png"), ImageFormat.Png);
        nint screenDc = GetDC(0), memory = CreateCompatibleDC(screenDc), handle = 0, previous = 0;
        try
        {
            handle = bitmap.GetHbitmap(Color.FromArgb(0)); previous = SelectObject(memory, handle);
            var position = new Point(Left, Top); var size = new Size(Width, Height); var source = new Point();
            var blend = new Blend { operation = 0, alpha = 255, format = 1 };
            if (!UpdateLayeredWindow(Handle, screenDc, ref position, ref size, memory, ref source, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { if (previous != 0) SelectObject(memory, previous); if (handle != 0) DeleteObject(handle); if (memory != 0) DeleteDC(memory); ReleaseDC(0, screenDc); }
    }
    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte operation, flags, alpha, format; }
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern nint GetDC(nint window);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(nint window, nint dst, ref Point position, ref Size size, nint src, ref Point origin, uint key, ref Blend blend, uint flags);
}
