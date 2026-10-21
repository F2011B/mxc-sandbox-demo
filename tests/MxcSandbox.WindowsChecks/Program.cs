using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;
using MxcSandbox.Core;

if (!OperatingSystem.IsWindows()) return 2;
var report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "mxc-windows-check.json");
if (args.Contains("--standard-user")) return StandardUserLauncher.Run(report);
var results = new List<object>();
var root = Directory.CreateTempSubdirectory("mxc-win-check-").FullName;
var ro = Directory.CreateDirectory(Path.Combine(root, "read only")).FullName;
var rw = Directory.CreateDirectory(Path.Combine(root, "read write")).FullName;
var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
var artifactRoot = Path.GetDirectoryName(report)!;
Directory.CreateDirectory(artifactRoot);
var evidence = new Dictionary<string, object?>
{
    ["os"] = Environment.OSVersion.ToString(), ["user"] = WindowsIdentity.GetCurrent().Name,
    ["userSid"] = WindowsIdentity.GetCurrent().User!.Value,
    ["elevated"] = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
    ["fixtures"] = root, ["checks"] = results, ["transport"] = "SSH Session 0 with new private test-only window station/desktop; genuine CREATE_NEW_CONSOLE and Win32 console input/buffer APIs; no visible desktop login"
};
async Task Save() => await File.WriteAllTextAsync(report, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
void Assert(bool value, string why) { if (!value) throw new Exception(why); }
async Task Check(string name, Func<Task> body)
{
    var timer = Stopwatch.StartNew();
    try { await body().WaitAsync(TimeSpan.FromSeconds(35)); results.Add(new { name, passed = true, elapsedMs = timer.ElapsedMilliseconds }); }
    catch (Exception ex) { results.Add(new { name, passed = false, error = ex.ToString(), elapsedMs = timer.ElapsedMilliseconds }); throw; }
    finally { await Save(); }
}
async Task WaitFor(Func<bool> condition, string why, int seconds = 8)
{
    var timer = Stopwatch.StartNew();
    while (true)
    {
        try { if (condition()) return; }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { } // cmd may still own its redirection handle.
        if (timer.Elapsed.TotalSeconds >= seconds) throw new TimeoutException(why);
        await Task.Delay(50);
    }
}
string Quote(string value) => "\"" + value + "\"";
string Proof(string name) => Path.Combine(rw, name);
SandboxProfile Profile() => new() { TimeoutSeconds = 28, WorkingDirectory = rw, Folders = [new(ro, FolderAccess.ReadOnly), new(rw, FolderAccess.ReadWrite)] };
Task<TestSession> Open(string name, SandboxProfile? profile = null) => TestSession.Open(profile ?? Profile(), Path.Combine(artifactRoot, name + ".console.txt"));
async Task Ready(TestSession session, string name)
{
    var path = Proof(name); session.Send("echo READY>" + Quote(path) + "\r");
    await WaitFor(() => File.Exists(path) && File.ReadAllText(path).Contains("READY"), "Shell did not consume console input");
}
string Acl(string path) => new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
try
{
    await File.WriteAllTextAsync(Path.Combine(ro, "input.txt"), "READ_OK");
    await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "UNGRANTED_SECRET");
    var aclBefore = new[] { Acl(ro), Acl(rw), Acl(outside), Acl(Path.GetPathRoot(root)!) };
    await Check("standard-user-and-native-base-container", () =>
    {
        Assert(evidence["elevated"] is false, "Run without elevation or pass --standard-user");
        var request = RequestFactory.Create(Profile(), rw, null); request.Ui = WindowsExecutorConfiguration.ConsoleUi();
        var probe = MxcContainer.Probe(request); evidence["probe"] = probe;
        Assert(probe.Error is null && probe.Tier == IsolationTier.BaseContainer && probe.NeedsDaclAugmentation == false, "Native BaseContainer without DACL fallback required");
        return Task.CompletedTask;
    });
    await Check("installed-desktop-console-entrypoint", async () =>
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "MxcSandbox.Desktop.exe")) { UseShellExecute = false };
        info.ArgumentList.Add("--console-smoke");
        using var process = Process.Start(info) ?? throw new Exception("Installed Desktop did not start");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert(process.ExitCode == 0, "Installed GUI console entrypoint failed: " + process.ExitCode);
    });
    await Check("real-console-input-working-directory-and-exit", async () =>
    {
        await using var session = await Open("input-exit");
        await Ready(session, "ready-input");
        session.Send("cd>" + Quote(Proof("cwd")) + "\rexit 7\r");
        var result = await session.Finish();
        Assert(result.ExitCode == 7 && !result.Stopped && !result.TimedOut, "Exit code 7 not propagated");
        Assert((await File.ReadAllTextAsync(Proof("cwd"))).Trim().Equals(rw, StringComparison.OrdinalIgnoreCase), "Working directory differs");
    });
    await Check("filesystem-read-write-and-denials", async () =>
    {
        var batch = $"@echo off\r\ntype {Quote(Path.Combine(ro, "input.txt"))}>{Quote(Proof("read-proof"))}\r\nif errorlevel 1 exit /b 51\r\necho WRITE_OK>{Quote(Proof("write-proof"))}\r\nif errorlevel 1 exit /b 52\r\ntype {Quote(Path.Combine(outside, "secret.txt"))}>{Quote(Proof("outside-read"))} 2>nul\r\nif not errorlevel 1 exit /b 53\r\necho BAD>{Quote(Path.Combine(ro, "blocked"))} 2>nul\r\nif not errorlevel 1 exit /b 54\r\necho BAD>{Quote(Path.Combine(outside, "blocked"))} 2>nul\r\nif not errorlevel 1 exit /b 55\r\nexit /b 0\r\n";
        var file = Path.Combine(ro, "rights.cmd"); await File.WriteAllTextAsync(file, batch, Encoding.ASCII);
        await using var session = await Open("filesystem");
        await Ready(session, "ready-files");
        session.Send("call " + Quote(file) + "\rexit %errorlevel%\r");
        Assert((await session.Finish()).ExitCode == 0, "File-right check failed");
        Assert((await File.ReadAllTextAsync(Proof("read-proof"))).Contains("READ_OK") && (await File.ReadAllTextAsync(Proof("write-proof"))).Contains("WRITE_OK"), "Granted access failed");
        Assert(!File.Exists(Path.Combine(ro, "blocked")) && !File.Exists(Path.Combine(outside, "blocked")), "Denied write succeeded");
        Assert(!File.Exists(Proof("outside-read")) || !(await File.ReadAllTextAsync(Proof("outside-read"))).Contains("UNGRANTED_SECRET"), "Denied file was readable");
    });
    await Check("host-environment-not-inherited", async () =>
    {
        Environment.SetEnvironmentVariable("MXC_CHECK_SECRET", "HOST_SENTINEL_NOT_A_CREDENTIAL");
        try
        {
            await using var session = await Open("environment"); await Ready(session, "ready-env");
            session.Send("if defined MXC_CHECK_SECRET (exit 61) else (exit 0)\r");
            Assert((await session.Finish()).ExitCode == 0, "Host variable leaked");
        }
        finally { Environment.SetEnvironmentVariable("MXC_CHECK_SECRET", null); }
    });
    await Check("direct-loopback-network-denied-with-live-host-fixture", async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(20000);
        var server = Task.Run(async () => { try { while (!deadline.IsCancellationRequested) { using var client = await listener.AcceptTcpClientAsync(deadline.Token); var stream = client.GetStream(); await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nHOST_OK"u8.ToArray(), deadline.Token); } } catch (OperationCanceledException) { } });
        try
        {
            using (var host = new TcpClient()) { await host.ConnectAsync(IPAddress.Loopback, port); using var reader = new StreamReader(host.GetStream()); Assert((await reader.ReadToEndAsync()).Contains("HOST_OK"), "Host network fixture unavailable"); }
            await using var session = await Open("network"); await Ready(session, "ready-net");
            session.Send("curl.exe --version>" + Quote(Proof("curl-version")) + "\rcurl.exe --noproxy * --connect-timeout 2 --max-time 3 http://127.0.0.1:" + port + "/>" + Quote(Proof("network-response")) + " 2>" + Quote(Proof("network-error")) + "\r");
            session.Send("if errorlevel 1 (exit 0) else (exit 62)\r");
            Assert((await session.Finish()).ExitCode == 0, "Offline sandbox reached host TCP service");
            Assert(File.Exists(Proof("curl-version")) && (await File.ReadAllTextAsync(Proof("curl-version"))).Contains("curl"), "curl could not run; no network proof");
            Assert(!(await File.ReadAllTextAsync(Proof("network-response"))).Contains("HOST_OK"), "Host response crossed offline boundary");
        }
        finally { deadline.Cancel(); listener.Stop(); await server; }
    });
    var ompDirectory = Environment.GetEnvironmentVariable("MXC_CHECK_OMP_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OmpSandboxTest", "18.8.9");
    await Check("omp-version-and-real-interactive-provider-picker", async () =>
    {
        Assert(File.Exists(Path.Combine(ompDirectory, "omp.exe")), "OMP missing; set MXC_CHECK_OMP_DIRECTORY to its installation");
        var profile = Profile() with { Folders = [new(ro, FolderAccess.ReadOnly), new(rw, FolderAccess.ReadWrite), new(ompDirectory, FolderAccess.ReadOnly)] };
        await using var session = await Open("omp-tui", profile); await Ready(session, "ready-omp");
        session.Send("omp --version>" + Quote(Proof("omp-version")) + "\r");
        await WaitFor(() => File.Exists(Proof("omp-version")) && File.ReadAllText(Proof("omp-version")).Contains("omp/"), "OMP version failed");
        session.Send("omp\r");
        await session.WaitForProcess("omp");
        await WaitFor(() => session.Console.Screen().Contains("no-model"), "OMP TUI did not render", 10);
        session.Send("\r");
        await WaitFor(() => session.Console.Screen().Contains("exit setup"), "OMP first-run picker did not open", 10);
        string Selected() => session.Console.Screen().Split('\n').LastOrDefault(line => line.Contains('❯')) ?? "";
        var before = Selected(); Assert(before.Length != 0, "No provider selected"); session.Console.ArrowDown();
        await WaitFor(() => Selected().Length > 0 && Selected() != before, "OMP did not receive ArrowDown", 3);
        await File.WriteAllTextAsync(Path.Combine(artifactRoot, "omp-tui-before-stop.txt"), session.Console.Screen());
        var ids = session.Console.Processes(); Assert((await session.Stop()).Stopped, "OMP Stop failed");
        await WaitFor(() => ids.Where(id => id != Environment.ProcessId).All(id => !TestSession.Alive(id)), "OMP survived Stop", 5);
    });
    await Check("ctrl-c-aborts-live-child-and-keeps-shell", async () =>
    {
        await using var session = await Open("ctrl-c"); await Ready(session, "ready-ctrl");
        var existing = session.Console.Processes().ToHashSet();
        session.Send("timeout.exe /t 60 /nobreak\r"); var pid = await session.WaitForProcess("timeout", exclude: existing);
        await Task.Delay(250); // The native process is live; let its CRT finish installing handlers.
        session.Console.CtrlC(); await WaitFor(() => !TestSession.Alive(pid), "Ctrl+C did not terminate waiting application", 5);
        session.Send("echo AFTER_CTRL_C>" + Quote(Proof("ctrl-c")) + "\rexit 9\r");
        Assert((await session.Finish()).ExitCode == 9 && (await File.ReadAllTextAsync(Proof("ctrl-c"))).Contains("AFTER_CTRL_C"), "Shell did not survive Ctrl+C");
    });
    await Check("stop-terminates-live-shell-and-child", async () =>
    {
        await using var session = await Open("stop", Profile() with { TimeoutSeconds = 0 }); await Ready(session, "ready-stop");
        var existing = session.Console.Processes().ToHashSet();
        session.Send("timeout.exe /t 60 /nobreak\r"); var pid = await session.WaitForProcess("timeout", exclude: existing);
        var result = await session.Stop(); Assert(result.Stopped && !result.TimedOut, "Stop result incorrect");
        await WaitFor(() => !TestSession.Alive(pid), "Child survived Stop", 5);
    });
    await Check("timeout-terminates-live-shell", async () =>
    {
        await using var session = await Open("timeout", Profile() with { TimeoutSeconds = 4 });
        await Ready(session, "ready-timeout"); var pids = session.Console.Processes();
        var result = await session.Finish(8); Assert(result.TimedOut && !result.Stopped, "Timeout result incorrect");
        await WaitFor(() => pids.Where(id => id != Environment.ProcessId).All(id => !TestSession.Alive(id)), "Sandbox process survived timeout", 5);
    });
    await Check("host-acls-unchanged", () =>
    {
        Assert(aclBefore.SequenceEqual(new[] { Acl(ro), Acl(rw), Acl(outside), Acl(Path.GetPathRoot(root)!) }), "Host ACL changed");
        return Task.CompletedTask;
    });
    await using (var ompFile = File.OpenRead(Path.Combine(ompDirectory, "omp.exe")))
        evidence["omp"] = new { directory = ompDirectory, sha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(ompFile)) };
    evidence["passed"] = true;
}
catch (Exception ex) { evidence["passed"] = false; evidence["error"] = ex.ToString(); }
await Save();
return evidence["passed"] is true ? 0 : 1;
