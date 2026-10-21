using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MxcSandbox.Core;
using Microsoft.Mxc.Sdk.V1;

if (OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Für Windows interaktiv scripts/verify-windows.ps1 aus einer echten Konsole ausführen."); return 2;
}
var root = Directory.CreateTempSubdirectory("mxc-console-check-").FullName;
var ro = Directory.CreateDirectory(Path.Combine(root, "read")).FullName;
var rw = Directory.CreateDirectory(Path.Combine(root, "write")).FullName;
var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
await File.WriteAllTextAsync(Path.Combine(ro, "input"), "readable");
await File.WriteAllTextAsync(Path.Combine(outside, "secret"), "not-readable");
var helper = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("src/MxcSandbox.Console/bin/Release/net10.0/MxcSandbox.Console");
var passed = 0;
async Task Check(string name, Func<Task> body) { await body().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS " + name); passed++; }
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
SandboxProfile Profile() => new() { Folders = [new(ro, FolderAccess.ReadOnly), new(rw, FolderAccess.ReadWrite)], WorkingDirectory = rw, TimeoutSeconds = 12 };

await Check("pty-is-a-real-terminal-and-enforces-file-permissions", async () =>
{
    var commands = $"test -t 0 && test -t 1 && printf tty-ok > {Quote(Path.Combine(rw, "tty"))}\n" +
        $"cat {Quote(Path.Combine(ro, "input"))}\n" +
        $"if cat {Quote(Path.Combine(outside, "secret"))}; then echo BAD; fi\n" +
        $"if echo bad > {Quote(Path.Combine(ro, "blocked"))}; then echo BAD; fi\n" +
        "printf '\\033[31mCOLOR\\033[0m\\n'\nexit 7\n";
    using var input = new MemoryStream(Encoding.UTF8.GetBytes(commands)); using var output = new MemoryStream();
    var result = await new InteractiveRunner().RunAsync(Profile(), input, output, () => new MxcPtySize(32, 100));
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert(result.ExitCode == 7, text); Assert(await File.ReadAllTextAsync(Path.Combine(rw, "tty")) == "tty-ok");
    Assert(!File.Exists(Path.Combine(ro, "blocked"))); Assert(text.Contains("readable") && !text.Contains("not-readable"), text);
    Assert(text.Contains("\x1b[31mCOLOR"), "Colors were lost");
});
await Check("pty-proxy-enforced-and-direct-network-denied", async () =>
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; using var deadline = new CancellationTokenSource(10000);
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
        while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 8\r\n\r\nPROXY_OK"u8.ToArray(), deadline.Token);
    });
    try
    {
        var proof = Path.Combine(rw, "proxy-proof"); var bypass = Path.Combine(rw, "bypass");
        var command = $"curl --noproxy '' --max-time 4 -fs http://127.0.0.1:{port}/ > {Quote(proof)}\n" +
            $"if curl --noproxy '*' --max-time 2 -fs http://127.0.0.1:{port}/; then echo BAD > {Quote(bypass)}; fi\nexit\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(command)); using var output = new MemoryStream();
        var result = await new InteractiveRunner().RunAsync(Profile() with { Proxy = new ProxySettings { Enabled = true, AllowedTargets = [$"127.0.0.1:{port}"], AllowPrivateAddresses = true } }, input, output, () => MxcPtySize.Default);
        Assert(result.ExitCode == 0 && await File.ReadAllTextAsync(proof) == "PROXY_OK" && !File.Exists(bypass), Encoding.UTF8.GetString(output.ToArray()));
        await server;
    }
    finally { deadline.Cancel(); listener.Stop(); }
});
await Check("pty-stop-kills-live-shell", async () =>
{
    using var input = new MemoryStream(); using var output = new MemoryStream(); using var stop = new CancellationTokenSource(400);
    var result = await new InteractiveRunner().RunAsync(Profile(), input, output, () => MxcPtySize.Default, stop.Token);
    Assert(result.Stopped);
});
await Check("pty-timeout-kills-live-shell", async () =>
{
    using var input = new MemoryStream(); using var output = new MemoryStream();
    var result = await new InteractiveRunner().RunAsync(Profile() with { TimeoutSeconds = 1 }, input, output, () => MxcPtySize.Default);
    Assert(result.TimedOut);
});
await Check("pty-window-resize-reaches-child", async () =>
{
    var sizeFile = Path.Combine(rw, "size"); var calls = 0;
    using var input = new MemoryStream(Encoding.UTF8.GetBytes($"sleep 0.4; stty size > {Quote(sizeFile)}; exit\n"));
    using var output = new MemoryStream();
    var result = await new InteractiveRunner().RunAsync(Profile(), input, output,
        () => Interlocked.Increment(ref calls) == 1 ? new MxcPtySize(24, 80) : new MxcPtySize(40, 120));
    Assert(result.ExitCode == 0 && (await File.ReadAllTextAsync(sizeFile)).Trim() == "40 120");
});
if (args.Length > 1)
{
    await Check("omp-starts-from-interactive-shell-path", async () =>
    {
        var install = Path.GetFullPath(args[1]); var version = Path.Combine(rw, "omp-version");
        var profile = Profile() with { Folders = [new(ro, FolderAccess.ReadOnly), new(rw, FolderAccess.ReadWrite), new(install, FolderAccess.ReadOnly)] };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes($"omp --version > {Quote(version)}; exit\n")); using var output = new MemoryStream();
        var result = await new InteractiveRunner().RunAsync(profile, input, output, () => MxcPtySize.Default);
        Assert(result.ExitCode == 0 && (await File.ReadAllTextAsync(version)).Contains("omp/"), Encoding.UTF8.GetString(output.ToArray()));
    });
}
await Check("session-owner-and-one-time-files", () =>
{
    var dir = ConsoleSessionFiles.Create(Profile()); var request = ConsoleSessionFiles.Read(dir); Assert(ConsoleSessionFiles.OwnerAlive(request));
    Assert(!ConsoleSessionFiles.OwnerAlive(request with { OwnerStartTicks = 1 }));
    if (!OperatingSystem.IsWindows()) Assert((File.GetUnixFileMode(dir) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0);
    return Task.CompletedTask;
});
// Start the managed script harness after native SDK cases; keep native and managed child reaping separate.
await Check("console-helper-real-host-tty-input-ctrl-c-and-exit", async () =>
{
    var directory = ConsoleSessionFiles.Create(Profile());
    var info = new ProcessStartInfo("/usr/bin/script") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    info.ArgumentList.Add("-q");
    if (OperatingSystem.IsMacOS())
    {
        info.ArgumentList.Add("/dev/null"); info.ArgumentList.Add(helper); info.ArgumentList.Add("--session"); info.ArgumentList.Add(directory);
    }
    else
    {
        info.ArgumentList.Add("-e"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(Quote(helper) + " --session " + Quote(directory)); info.ArgumentList.Add("/dev/null");
    }
    using var process = Process.Start(info) ?? throw new Exception("script failed");
    var text = new StringBuilder(); var gate = new object();
    var reader = Task.Run(async () => { var buffer = new char[1024]; int n; while ((n = await process.StandardOutput.ReadAsync(buffer)) > 0) { lock (gate) text.Append(buffer, 0, n); } });
    var error = process.StandardError.ReadToEndAsync();
    try
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            lock (gate) { if (text.ToString().Contains("[MXC]")) break; }
            if (process.HasExited || deadline.Elapsed.TotalSeconds > 10) { Console.Error.WriteLine("No shell prompt: " + text + (error.IsCompleted ? error.Result : "") + (process.HasExited ? " exit=" + process.ExitCode : "")); throw new Exception("No shell prompt"); }
            await Task.Delay(30);
        }
        await process.StandardInput.WriteLineAsync("sleep 30"); await process.StandardInput.FlushAsync();
        await Task.Delay(300); await process.StandardInput.WriteAsync("\x03"); await process.StandardInput.FlushAsync(); await Task.Delay(150);
        await process.StandardInput.WriteLineAsync($"test -t 0 && printf host-tty-ok > {Quote(Path.Combine(rw, "host-tty"))}");
        await process.StandardInput.WriteLineAsync("exit 9"); await process.StandardInput.FlushAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); await reader; }
        catch { Console.Error.WriteLine("HOST TTY OUTPUT: " + text); throw; }
        var result = JsonSerializer.Deserialize<ConsoleSessionResult>(await File.ReadAllTextAsync(Path.Combine(directory, "result.json")));
        Assert(result?.ExitCode == 9, text.ToString() + await error); Assert(await File.ReadAllTextAsync(Path.Combine(rw, "host-tty")) == "host-tty-ok");
    }
    finally { if (!process.HasExited) process.Kill(true); }
});
await Check("console-stop-marker-terminates-active-host-session", async () =>
{
    var directory = ConsoleSessionFiles.Create(Profile() with { TimeoutSeconds = 0 });
    var info = new ProcessStartInfo("/usr/bin/script") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    info.ArgumentList.Add("-q");
    if (OperatingSystem.IsMacOS()) { info.ArgumentList.Add("/dev/null"); info.ArgumentList.Add(helper); info.ArgumentList.Add("--session"); info.ArgumentList.Add(directory); }
    else { info.ArgumentList.Add("-e"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(Quote(helper) + " --session " + Quote(directory)); info.ArgumentList.Add("/dev/null"); }
    using var process = Process.Start(info) ?? throw new Exception("script failed");
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var reader = Task.Run(async () =>
    {
        var text = new StringBuilder(); var buffer = new char[1024]; int count;
        while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0) { text.Append(buffer, 0, count); if (text.ToString().Contains("[MXC]")) ready.TrySetResult(); }
    });
    var errors = process.StandardError.ReadToEndAsync();
    try
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await File.WriteAllTextAsync(Path.Combine(directory, "stop"), "stop");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); await reader;
        var result = JsonSerializer.Deserialize<ConsoleSessionResult>(await File.ReadAllTextAsync(Path.Combine(directory, "result.json")));
        Assert(result?.Stopped == true && result.Error is null, await errors);
    }
    finally { if (!process.HasExited) process.Kill(true); }
});
if (args.Length > 1)
{
    await Check("omp-interactive-tui-receives-keys-and-stops", async () =>
    {
        var profile = Profile() with { TimeoutSeconds = 18, Folders = [new(ro, FolderAccess.ReadOnly), new(rw, FolderAccess.ReadWrite), new(Path.GetFullPath(args[1]), FolderAccess.ReadOnly)] };
        var directory = ConsoleSessionFiles.Create(profile);
        var info = new ProcessStartInfo("/usr/bin/script") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-q");
        if (OperatingSystem.IsMacOS()) { info.ArgumentList.Add("/dev/null"); info.ArgumentList.Add(helper); info.ArgumentList.Add("--session"); info.ArgumentList.Add(directory); }
        else { info.ArgumentList.Add("-e"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(Quote(helper) + " --session " + Quote(directory)); info.ArgumentList.Add("/dev/null"); }
        using var process = Process.Start(info) ?? throw new Exception("script failed");
        var text = new StringBuilder(); var gate = new object();
        var reader = Task.Run(async () => { var buffer = new char[2048]; int count; while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0) { lock (gate) text.Append(buffer, 0, count); } });
        var errors = process.StandardError.ReadToEndAsync();
        string Snapshot() { lock (gate) return text.ToString(); }
        try
        {
            var timer = Stopwatch.StartNew();
            while (!Snapshot().Contains("[MXC]")) { if (process.HasExited || timer.Elapsed.TotalSeconds > 5) throw new Exception("No shell: " + Snapshot()); await Task.Delay(30); }
            // No model prompt or API credentials. Empty HOME and offline network stay in effect.
            await process.StandardInput.WriteLineAsync("omp --no-session --no-extensions --no-skills --no-rules --no-lsp --no-title --no-tools; printf 'OMP_DONE:%s\\n' \"$?\"");
            await process.StandardInput.FlushAsync(); await Task.Delay(3000);
            var before = Snapshot();
            Assert(!process.HasExited && before.Contains("no-model") && !System.Text.RegularExpressions.Regex.IsMatch(before, @"OMP_DONE:\d+"), "OMP did not show its interactive prompt; see omp-tui.txt");
            // Enter skips the first-run animation. Do not sign in or send a prompt.
            await process.StandardInput.WriteAsync("\r"); await process.StandardInput.FlushAsync();
            timer.Restart();
            while (!Snapshot().Contains("exit setup"))
            {
                if (process.HasExited || timer.Elapsed.TotalSeconds > 7) throw new Exception("No first-run provider picker; see omp-tui.txt");
                await Task.Delay(50);
            }
            string Selected() => System.Text.RegularExpressions.Regex.Replace(Snapshot(), @"\x1b\[[0-?]*[ -/]*[@-~]", "")
                .Split(['\r', '\n']).LastOrDefault(line => line.Contains('❯')) ?? "";
            var initial = Selected(); Assert(initial.Length > 0, "No selected provider");
            await process.StandardInput.WriteAsync("\x1b[B"); await process.StandardInput.FlushAsync();
            timer.Restart();
            while (Selected() == initial || Selected().Length == 0)
            {
                if (process.HasExited || timer.Elapsed.TotalSeconds > 2) throw new Exception("OMP did not receive ArrowDown; see omp-tui.txt");
                await Task.Delay(40);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "stop"), "stop");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); await reader;
            var result = JsonSerializer.Deserialize<ConsoleSessionResult>(await File.ReadAllTextAsync(Path.Combine(directory, "result.json")));
            Assert(result?.Stopped == true && result.Error is null, "OMP stop failed: " + await errors);
        }
        finally { await File.WriteAllTextAsync(Path.Combine(root, "omp-tui.txt"), Snapshot()); if (!process.HasExited) process.Kill(true); }
    });
}
Console.WriteLine($"{passed}/{passed} console checks passed; fixtures: {root}");
return 0;
