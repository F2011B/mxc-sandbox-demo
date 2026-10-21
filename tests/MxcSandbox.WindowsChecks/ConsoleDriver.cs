using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class ConsoleDriver : IDisposable
{
    private readonly IntPtr output;
    private readonly IntPtr input;
    public ConsoleDriver(int pid)
    {
        FreeConsole();
        if (!AttachConsole((uint)pid)) throw Error("AttachConsole");
        output = CreateFileW("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        input = CreateFileW("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (output == new IntPtr(-1) || input == new IntPtr(-1)) throw Error("Open console buffers");
        if (!GetConsoleMode(input, out _) || !GetConsoleMode(output, out _)) throw Error("GetConsoleMode");
        SetConsoleCtrlHandler(IntPtr.Zero, true); // Only the test controller ignores the generated Ctrl+C.
    }
    public string Screen()
    {
        // TUI programs can replace the active screen buffer. Reopen CONOUT$
        // for each snapshot rather than retaining a handle to the old buffer.
        var screen = CreateFileW("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (screen == new IntPtr(-1)) throw Error("Open active screen");
        try
        {
            if (!GetConsoleScreenBufferInfo(screen, out var info)) throw Error("GetConsoleScreenBufferInfo");
            var count = Math.Min((int)info.Size.X * info.Size.Y, 100000);
            var text = new StringBuilder(count);
            if (!ReadConsoleOutputCharacterW(screen, text, (uint)count, new Coord(), out _)) throw Error("ReadConsoleOutputCharacter");
            var raw = text.ToString(); var width = (int)info.Size.X;
            return string.Join("\n", Enumerable.Range(0, (raw.Length + width - 1) / width).Select(row => raw.Substring(row * width, Math.Min(width, raw.Length - row * width)).TrimEnd())).TrimEnd();
        }
        finally { CloseHandle(screen); }
    }
    public void Send(string text)
    {
        var records = text.SelectMany(value => new[] { new InputRecord { Type = 1, Down = 1, Repeat = 1, Key = value == '\r' ? (ushort)13 : (ushort)0, Character = value }, new InputRecord { Type = 1, Down = 0, Repeat = 1, Key = value == '\r' ? (ushort)13 : (ushort)0, Character = value } }).ToArray();
        if (!WriteConsoleInputW(input, records, (uint)records.Length, out var written) || written != records.Length) throw Error("WriteConsoleInput");
    }
    public uint[] Processes()
    {
        var ids = new uint[64]; var count = GetConsoleProcessList(ids, (uint)ids.Length);
        if (count == 0 || count > ids.Length) throw Error("GetConsoleProcessList");
        return ids.Take((int)count).ToArray();
    }
    public void ArrowDown()
    {
        var records = new[] { new InputRecord { Type = 1, Down = 1, Repeat = 1, Key = 0x28, Scan = 0x50, Control = 0x100 }, new InputRecord { Type = 1, Repeat = 1, Key = 0x28, Scan = 0x50, Control = 0x100 } };
        if (!WriteConsoleInputW(input, records, 2, out var count) || count != 2) throw Error("ArrowDown");
    }
    public void CtrlC() { if (!GenerateConsoleCtrlEvent(0, 0)) throw Error("GenerateConsoleCtrlEvent"); }
    public void Dispose() { CloseHandle(output); CloseHandle(input); FreeConsole(); }
    private static Win32Exception Error(string name) => new(Marshal.GetLastWin32Error(), name);
    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferInfo { public Coord Size, Cursor; public ushort Attributes; public Rect Window; public Coord Maximum; }
    [StructLayout(LayoutKind.Explicit, Size = 20)] private struct InputRecord { [FieldOffset(0)] public ushort Type; [FieldOffset(4)] public int Down; [FieldOffset(8)] public ushort Repeat; [FieldOffset(10)] public ushort Key; [FieldOffset(12)] public ushort Scan; [FieldOffset(14)] public char Character; [FieldOffset(16)] public uint Control; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out BufferInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ReadConsoleOutputCharacterW(IntPtr handle, StringBuilder text, uint count, Coord position, out uint read);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WriteConsoleInputW(IntPtr handle, InputRecord[] records, uint count, out uint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint kind, uint group);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
