using System.Runtime.InteropServices;

// Only Unicode packets; never synthesize Ctrl/Alt/Shift/Win or a paste shortcut.
static class TextInsertion
{
    internal readonly record struct Target(nint Window, nint Focus);
    [StructLayout(LayoutKind.Sequential)]
    struct GuiInfo
    {
        public uint size, flags; public nint active, focus, capture, menuOwner, moveSize, caret;
        public int left, top, right, bottom;
    }
    [StructLayout(LayoutKind.Sequential)] struct Keyboard { public ushort key, scan; public uint flags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] struct Mouse { public int x, y; public uint data, flags, time; public nuint extra; }
    [StructLayout(LayoutKind.Explicit)] struct Payload { [FieldOffset(0)] public Keyboard keyboard; [FieldOffset(0)] public Mouse mouse; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public Payload value; }
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    internal static Target Capture()
    {
        nint window = GetForegroundWindow(); if (window == 0) return default;
        uint thread = GetWindowThreadProcessId(window, out _); var info = new GuiInfo { size = (uint)Marshal.SizeOf<GuiInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? new(window, info.focus) : default;
    }
    internal static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => Native.GetAsyncKeyState(k) < 0);
    internal static bool OtherInputDown()
    {
        for (int k = 1; k < 0xE0; k++)
        {
            if (k is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) continue;
            if (Native.GetAsyncKeyState(k) < 0) return true;
        }
        return false;
    }
    static Input[] Encode(string text) => text.SelectMany(c => new[]{
  new Input{type=1,value=new Payload{keyboard=new Keyboard{scan=c,flags=4}}},
  new Input{type=1,value=new Payload{keyboard=new Keyboard{scan=c,flags=6}}}
 }).ToArray();
    internal static bool TryInsert(Target target, string text)
    {
        if (target.Window == 0 || target.Focus == 0 || text.Length == 0 || ModifiersDown() || OtherInputDown() || Capture() != target) return false;
        var packets = Encode(text);
        if (SendInput((uint)packets.Length, packets, Marshal.SizeOf<Input>()) != packets.Length)
            throw new InvalidOperationException("Windows did not accept all input. Check the transcript; Copy is still available.");
        return true;
    }
    internal static void Validate()
    {
        if (Marshal.SizeOf<Input>() != (IntPtr.Size == 8 ? 40 : 28)) throw new Exception("INPUT ABI mismatch");
        foreach (var p in Encode("Grüße € 😀\n")) if (p.type != 1 || p.value.keyboard.key != 0 || p.value.keyboard.flags is not (4 or 6)) throw new Exception("Non-Unicode input");
    }
    internal static Form TestWindow()
    {
        Validate(); var f = new Form { Text = "PoC: isolierter Texteingabetest", Width = 650, Height = 180, TopMost = true };
        var box = new TextBox { Multiline = true, Dock = DockStyle.Fill }; f.Controls.Add(box);
        f.Controls.Add(new Label { Text = "Bitte ins leere Feld klicken. Der Test schreibt nur hier einen Beispielsatz.", Dock = DockStyle.Top, Height = 40 });
        f.Activated += (_, _) => box.Focus();
        f.Shown += async (_, _) =>
        {
            bool ok = false; try
            {
                f.Activate(); box.Focus(); await Task.Delay(200);
                for (int n = 0; n < 450; n++) { var t = Capture(); if (t.Window == f.Handle && t.Focus == box.Handle && !ModifiersDown() && !OtherInputDown()) break; await Task.Delay(100); }
                var target = Capture();
                if (target.Window != f.Handle || target.Focus != box.Handle) throw new Exception($"Test window not focused; expected {f.Handle}/{box.Handle}, actual {target.Window}/{target.Focus}; no input sent");
                if (TryInsert(new Target(0, 0), "must not appear")) throw new Exception("Invalid target accepted");
                const string text = "Grüße aus Österreich: € 123 😀";
                if (!TryInsert(target, text)) throw new Exception("Test insertion gated");
                await Task.Delay(150); if (box.Text != text) throw new Exception("Inserted text mismatch");
                ok = true;
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "voice-poc-insertion-test.txt"), e.Message); }
            if (ok) File.WriteAllText(Path.Combine(Path.GetTempPath(), "voice-poc-insertion-test.txt"), "PASS Unicode including umlauts and surrogate pair; invalid target rejected; no modifier events");
            Environment.ExitCode = ok ? 0 : 1; f.Close();
        }; return f;
    }
}
