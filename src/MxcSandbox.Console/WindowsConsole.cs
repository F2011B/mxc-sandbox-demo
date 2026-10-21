using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Mxc.Sdk.V1;
using MxcSandbox.Core;

namespace MxcSandbox.ConsoleHost;

internal static class WindowsConsole
{
    public static async Task<RunOutcome> RunAsync(SandboxProfile profile, string directory, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (Console.IsInputRedirected || Console.IsOutputRedirected) throw new IOException("Eine echte Windows-Konsole ist erforderlich.");
        var scratch = Directory.CreateTempSubdirectory("mxc-shell-").FullName;
        var request = RequestFactory.Create(profile, scratch, null);
        var guest = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "guest-console"));
        WindowsExecutorConfiguration.ValidateInstallationGrants(profile, Path.GetDirectoryName(guest)!);
        var bootstrap = Path.Combine(guest, "MxcSandbox.WindowsShell.exe");
        if (!File.Exists(bootstrap)) throw new FileNotFoundException("Windows-Shell-Bootstrapper fehlt.", bootstrap);
        request.Filesystem!.ReadonlyPaths.Add(guest);
        var guiRoot = Path.GetDirectoryName(guest)!;
        if (File.Exists(Path.Combine(guiRoot, "MxcSandbox.Desktop.exe")))
        {
            foreach (var file in Directory.EnumerateFiles(guiRoot)) request.Filesystem.DeniedPaths.Add(file);
            foreach (var folder in Directory.EnumerateDirectories(guiRoot).Where(path => !path.Equals(guest, StringComparison.OrdinalIgnoreCase)))
                request.Filesystem.DeniedPaths.Add(folder);
        }
        request.Environment!["MXC_DEMO_SHELL_COMMAND"] = request.Command;
        request = new ContainerRequest('"' + bootstrap + "\"")
        {
            Containment = request.Containment, Filesystem = request.Filesystem, Environment = request.Environment,
            Network = request.Network, Ui = request.Ui, WorkingDirectory = request.WorkingDirectory, TimeoutMs = request.TimeoutMs
        };
        request.Ui = WindowsExecutorConfiguration.ConsoleUi();
        var probe = MxcContainer.Probe(request);
        if (probe.Error is not null || probe.Tier != IsolationTier.BaseContainer || probe.NeedsDaclAugmentation != false)
            throw new PlatformNotSupportedException("Diese Konsole benötigt einen aktuellen Windows-11-ProcessContainer ohne DACL-Fallback. " + probe.Error);
        var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var executable = Path.Combine(AppContext.BaseDirectory, "native", rid, "wxc-exec.exe");
        var expected = rid == "win-x64" ? "090f5d1f7bb93104ab08dcd4e8034ef954aeefcdaf6658dde5f7dff4cfc12f3a" : "91aa4171e8809f33f19622c381116188eab7d8c8cffad231230a31b58146ee77";
        if (!File.Exists(executable)) throw new FileNotFoundException("Offizieller MXC-Windows-Executor fehlt. Windows-Paket erneut erstellen.", executable);
        await using (var file = File.OpenRead(executable))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("SHA-256-Prüfung des MXC-Executors fehlgeschlagen.");
        }
        var config = Path.Combine(directory, "windows-request.json");
        await File.WriteAllTextAsync(config, WindowsExecutorConfiguration.Serialize(request), token);
        // Native MXC sees genuine console handles and takes its StdioMode::Inherit
        // path. No RPC, pipe emulation, alternate user or uncontained shell.
        using var child = SuspendedConsoleProcess.Start(executable, config);
        var timedOut = false;
        var timer = Stopwatch.StartNew();
        while (!child.HasExited)
        {
            if (token.IsCancellationRequested || profile.TimeoutSeconds > 0 && timer.Elapsed.TotalSeconds >= profile.TimeoutSeconds)
            {
                timedOut = !token.IsCancellationRequested;
                child.Terminate(); break;
            }
            await Task.Delay(100);
        }
        return new RunOutcome(child.ExitCode, timedOut, token.IsCancellationRequested, scratch);
    }
}

/// <summary>The trusted native executor starts suspended; assign a kill-on-close
/// job before it can launch the contained shell, then resume it. The real console
/// is inherited, not a redirected stream or a separately created user session.</summary>
internal sealed class SuspendedConsoleProcess : IDisposable
{
    private IntPtr process;
    private IntPtr job;
    private SuspendedConsoleProcess() { }
    public static SuspendedConsoleProcess Start(string executable, string config)
    {
        var child = new SuspendedConsoleProcess();
        try
        {
            child.job = CreateJobObjectW(IntPtr.Zero, null);
            if (child.job == IntPtr.Zero) throw Error("CreateJobObject");
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(child.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Error("SetInformationJobObject");
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
            // Both paths are controlled installation/session paths and may not hold quotes.
            if (executable.Contains('"') || config.Contains('"')) throw new ArgumentException("Ungültiger Programmpfad.");
            var line = new StringBuilder('"' + executable + "\" \"" + config + '"');
            if (!CreateProcessW(executable, line, IntPtr.Zero, IntPtr.Zero, false, 0x00000004 | 0x00000200, IntPtr.Zero,
                Path.GetDirectoryName(executable), ref startup, out var info)) throw Error("CreateProcess");
            child.process = info.Process;
            try
            {
                if (!AssignProcessToJobObject(child.job, child.process)) { TerminateProcess(child.process, 125); throw Error("AssignProcessToJobObject"); }
                if (ResumeThread(info.Thread) == uint.MaxValue) { TerminateProcess(child.process, 125); throw Error("ResumeThread"); }
            }
            finally { CloseHandle(info.Thread); }
            return child;
        }
        catch { child.Dispose(); throw; }
    }
    public bool HasExited => WaitForSingleObject(process, 0) switch { 0 => true, 258 => false, _ => throw Error("WaitForSingleObject") };
    public int ExitCode
    {
        get
        {
            if (!GetExitCodeProcess(process, out var code)) throw Error("GetExitCodeProcess");
            return unchecked((int)code);
        }
    }
    public void Terminate()
    {
        if (!TerminateJobObject(job, 125)) throw Error("TerminateJobObject");
        if (WaitForSingleObject(process, 5000) != 0) throw new IOException("MXC-Executor konnte nicht beendet werden.");
    }
    public void Dispose()
    {
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
    }
    private static IOException Error(string action) => new(action + " fehlgeschlagen: Win32 " + Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size; public string? Reserved; public string? Desktop; public string? Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcess, PerJob; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInfo information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
