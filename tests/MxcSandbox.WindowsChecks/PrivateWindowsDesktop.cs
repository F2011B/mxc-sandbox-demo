using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
// Test infrastructure for SSH Session 0. New USER objects only; never edits an
// existing desktop/window station or its DACL. Production uses the user desktop.
internal sealed class PrivateWindowsDesktop : IDisposable
{
    private nint station;
    private nint desktop;
    public string StartupName { get; }
    public PrivateWindowsDesktop()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        StartupName = "mxc-" + Guid.NewGuid().ToString("N") + "\\default";
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User!.Value;
        var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;{userSid})(A;;GA;;;AC)(A;;GA;;;S-1-15-2-2)S:(ML;;NW;;;LW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _)) throw Error("Private desktop security");
        var original = GetProcessWindowStation();
        try
        {
            var attributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor, Inherit = 1 };
            station = CreateWindowStationW(StartupName.Split('\\')[0], 0, 0x02000000, ref attributes);
            if (station == 0) throw Error("Create private window station");
            if (!SetProcessWindowStation(station)) throw Error("Select private window station");
            try
            {
                desktop = CreateDesktopExW("default", null, 0, 0, 0x02000000, ref attributes, 4096, 0);
                if (desktop == 0) throw Error("Create private desktop");
            }
            finally { if (!SetProcessWindowStation(original)) throw Error("Restore host window station"); }
        }
        catch { Dispose(); throw; }
        finally { LocalFree(descriptor); }
    }
    public void Dispose()
    {
        if (desktop != 0) { CloseDesktop(desktop); desktop = 0; }
        if (station != 0) { CloseWindowStation(station); station = 0; }
    }
    private static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public uint Length; public nint Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowStationW(string name, uint flags, uint access, ref SecurityAttributes attributes);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDesktopExW(string name, string? device, nint mode, uint flags, uint access, ref SecurityAttributes attributes, uint heapSize, nint reserved);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(nint station);
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern bool CloseWindowStation(nint station);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
