using System.Diagnostics;
using System.Text.Json;
using MxcSandbox.Core;
using MxcSandbox.Desktop;

internal sealed class TestSession : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task<RunOutcome> running;
    private readonly string screenPath;
    public ConsoleDriver Console { get; private set; } = null!;
    public string Directory { get; private set; } = "";
    private TestSession(SandboxProfile profile, string screenPath)
    {
        this.screenPath = screenPath;
        running = new ConsoleLauncher().RunAsync(profile, _ => { }, stop.Token);
    }
    public static async Task<TestSession> Open(SandboxProfile profile, string screenPath)
    {
        var before = System.IO.Directory.GetDirectories(ConsoleSessionFiles.ControlRoot).ToHashSet();
        var session = new TestSession(profile, screenPath);
        try
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                if (session.running.IsCompleted) { await session.running; throw new Exception("Shell ended before console attach"); }
                if (timer.Elapsed.TotalSeconds > 12) throw new TimeoutException("No console helper");
                var path = System.IO.Directory.GetDirectories(ConsoleSessionFiles.ControlRoot).FirstOrDefault(path => !before.Contains(path) && ConsoleSessionFiles.Read(path).OwnerPid == Environment.ProcessId);
                if (path is not null && File.Exists(Path.Combine(path, "host.json")))
                {
                    var host = JsonSerializer.Deserialize<ConsoleSessionRequest>(await File.ReadAllTextAsync(Path.Combine(path, "host.json")))!;
                    session.Directory = path;
                    session.Console = new ConsoleDriver(host.OwnerPid);
                    break;
                }
                await Task.Delay(40);
            }
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }
    public void Send(string text) => Console.Send(text);
    public Task<RunOutcome> Finish(int seconds = 10) => running.WaitAsync(TimeSpan.FromSeconds(seconds));
    public async Task<RunOutcome> Stop()
    {
        stop.Cancel(); return await Finish();
    }
    public static bool Alive(uint id)
    {
        try { using var p = Process.GetProcessById((int)id); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }
    public async Task<uint> WaitForProcess(string name, int seconds = 5, IReadOnlySet<uint>? exclude = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            foreach (var id in Console.Processes())
                try { using var p = Process.GetProcessById((int)id); if (exclude?.Contains(id) != true && p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) && !p.HasExited) return id; }
                catch (ArgumentException) { }
            if (running.IsCompleted) { await running; throw new Exception("Session exited while waiting for " + name); }
            await Task.Delay(40);
        }
        throw new TimeoutException("Process not running in real console: " + name + "\n" + Console.Screen());
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            stop.Cancel();
            try { await Finish(6); } catch { }
            if (Console is not null) await File.WriteAllTextAsync(screenPath, Console.Screen());
        }
        finally { Console?.Dispose(); stop.Dispose(); }
    }
}
