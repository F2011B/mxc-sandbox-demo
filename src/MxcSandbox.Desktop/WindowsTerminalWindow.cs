using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MxcSandbox.Desktop;

[SupportedOSPlatform("windows")]
internal static class WindowsTerminalWindow
{
    public static void Open(string helper, string directory)
    {
        if (helper.Contains('"') || directory.Contains('"')) throw new ArgumentException("Ungültiger Konsolenpfad.");
        var command = new StringBuilder('"' + helper + "\" --session \"" + directory + '"');
        var startup = new Startup { Size = (uint)Marshal.SizeOf<Startup>(), Title = "MXC Sandbox" };
        // Always a separate console, even if the GUI was launched from a terminal.
        if (!CreateProcessW(helper, command, IntPtr.Zero, IntPtr.Zero, false, 0x10, IntPtr.Zero,
            Path.GetDirectoryName(helper), ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Sandbox-Konsole konnte nicht geöffnet werden.");
        CloseHandle(process.Thread); CloseHandle(process.Process);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startup
    {
        public uint Size; public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Information { public IntPtr Process, Thread; public uint Pid, Tid; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref Startup startup, out Information information);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
