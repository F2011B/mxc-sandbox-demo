using System.Diagnostics;
using System.Runtime.InteropServices;

// This small bootstrapper runs INSIDE ProcessContainer, never on the host.
if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected) return 125;
var command = Environment.GetEnvironmentVariable("MXC_DEMO_SHELL_COMMAND");
if (string.IsNullOrWhiteSpace(command)) return 125;
Environment.SetEnvironmentVariable("MXC_DEMO_SHELL_COMMAND", null);
// The executor starts as a new process group with Ctrl+C ignored. Re-enable
// it here so the user's shell/OMP receives Ctrl+C, while the executor survives.
Console.CancelKeyPress += (_, e) => e.Cancel = true;
if (!Native.SetConsoleCtrlHandler(IntPtr.Zero, false)) return 125;
try
{
    var shell = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
    {
        Arguments = "/D /S /C \"" + command + "\"", UseShellExecute = false, CreateNoWindow = false
    };
    using var child = Process.Start(shell) ?? throw new IOException("Shell konnte nicht gestartet werden.");
    child.WaitForExit(); return child.ExitCode;
}
catch (Exception ex) { Console.Error.WriteLine("Sandbox-Shell: " + ex.Message); return 125; }

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
