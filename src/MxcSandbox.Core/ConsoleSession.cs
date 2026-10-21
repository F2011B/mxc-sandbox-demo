using System.Diagnostics;
using System.Text.Json;

namespace MxcSandbox.Core;

public sealed record ConsoleSessionRequest(SandboxProfile Profile, int OwnerPid, long OwnerStartTicks);
public sealed record ConsoleSessionResult(int ExitCode, bool TimedOut, bool Stopped, string? Error = null);

public static class ConsoleSessionFiles
{
    public static string ControlRoot
    {
        get
        {
            var storage = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(storage)) storage = Path.GetTempPath();
            var root = Path.GetFullPath(Path.Combine(storage, "MxcSandboxDemo", "control"));
            Directory.CreateDirectory(root);
            if (new DirectoryInfo(root).LinkTarget is not null) throw new IOException("Steuerverzeichnis darf kein symbolischer Link sein.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return root;
        }
    }
    public const string RequestName = "session.json";
    public static string Create(SandboxProfile profile)
    {
        profile.Validate();
        var directory = Directory.CreateDirectory(Path.Combine(ControlRoot, Guid.NewGuid().ToString("N"))).FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var owner = Process.GetCurrentProcess();
        Write(Path.Combine(directory, RequestName), new ConsoleSessionRequest(profile, owner.Id, StartToken(owner)));
        return directory;
    }
    public static void Write<T>(string path, T value)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
    }
    public static ConsoleSessionRequest Read(string directory)
    {
        var text = File.ReadAllText(Path.Combine(directory, RequestName));
        if (text.Length > 262_144) throw new ArgumentException("Konsolenprofil ist zu groß.");
        var request = JsonSerializer.Deserialize<ConsoleSessionRequest>(text) ?? throw new ArgumentException("Leeres Konsolenprofil.");
        request.Profile.Validate();
        if (request.OwnerPid <= 0 || request.OwnerStartTicks <= 0) throw new ArgumentException("Ungültiger Konsolenbesitzer.");
        return request;
    }
    public static long StartToken(Process process)
    {
        if (!OperatingSystem.IsLinux()) return process.StartTime.ToUniversalTime().Ticks;
        // /proc starttime is stable; DateTime-based Linux boot-time estimates may jitter.
        var stat = File.ReadAllText($"/proc/{process.Id}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture);
    }
    public static bool OwnerAlive(ConsoleSessionRequest request)
    {
        try
        {
            using var owner = Process.GetProcessById(request.OwnerPid);
            return !owner.HasExited && StartToken(owner) == request.OwnerStartTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
