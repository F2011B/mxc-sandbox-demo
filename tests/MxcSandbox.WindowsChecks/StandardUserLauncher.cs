using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Test infrastructure only. Same Windows account/SID, filtered privileges and
// Medium integrity. The application never needs this launcher or elevation.
internal static class StandardUserLauncher
{
    public static int Run(string report)
    {
        IntPtr level = IntPtr.Zero, token = IntPtr.Zero, sid = IntPtr.Zero;
        try
        {
            if (!SaferCreateLevel(1, 0x20000, 1, out level, IntPtr.Zero) || !SaferComputeTokenFromLevel(level, IntPtr.Zero, out token, 0, IntPtr.Zero)) throw Error("SAFER normal-user token");
            if (!ConvertStringSidToSidW("S-1-16-8192", out sid)) throw Error("Medium integrity SID");
            var label = new MandatoryLabel { Label = new SidAndAttributes { Sid = sid, Attributes = 0x20 } };
            if (!SetTokenInformation(token, 25, ref label, (uint)Marshal.SizeOf<MandatoryLabel>() + GetLengthSid(sid))) throw Error("Set Medium integrity");
            var executable = Environment.ProcessPath!;
            if (executable.Contains('"') || report.Contains('"')) throw new ArgumentException("Invalid test path");
            var command = new StringBuilder('"' + executable + "\" \"" + report + "\" --standard-worker");
            using var desktop = new PrivateWindowsDesktop();
            var startup = new Startup { Size = (uint)Marshal.SizeOf<Startup>(), Desktop = desktop.StartupName };
            if (!CreateProcessAsUserW(token, executable, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var process)) throw Error("CreateProcessAsUser (same-user filtered test)");
            CloseHandle(process.Thread);
            try
            {
                if (WaitForSingleObject(process.Process, 180000) != 0) { TerminateProcess(process.Process, 125); throw new TimeoutException("Standard-user checks exceeded 180 seconds"); }
                if (!GetExitCodeProcess(process.Process, out var code)) throw Error("GetExitCodeProcess");
                return unchecked((int)code);
            }
            finally { CloseHandle(process.Process); }
        }
        finally { if (sid != IntPtr.Zero) LocalFree(sid); if (token != IntPtr.Zero) CloseHandle(token); if (level != IntPtr.Zero) SaferCloseLevel(level); }
    }
    private static Win32Exception Error(string name) => new(Marshal.GetLastWin32Error(), name);
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct MandatoryLabel { public SidAndAttributes Label; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup { public uint Size; public string? Reserved, Desktop, Title; public uint X,Y,Width,Height,XChars,YChars,Fill,Flags; public ushort Show,ReservedSize; public IntPtr ReservedBytes, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct Information { public IntPtr Process,Thread; public uint Pid,Tid; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SaferCreateLevel(uint scope, uint level, uint flags, out IntPtr handle, IntPtr reserved);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SaferComputeTokenFromLevel(IntPtr level, IntPtr token, out IntPtr result, uint flags, IntPtr reserved);
    [DllImport("advapi32.dll")] private static extern bool SaferCloseLevel(IntPtr level);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSidToSidW(string text, out IntPtr sid);
    [DllImport("advapi32.dll")] private static extern uint GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, uint kind, ref MandatoryLabel value, uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUserW(IntPtr token, string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref Startup startup, out Information process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr handle, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
}
