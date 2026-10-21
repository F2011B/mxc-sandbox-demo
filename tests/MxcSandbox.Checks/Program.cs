using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MxcSandbox.Core;

var checks = new List<(string Name, Func<Task> Body)>();
var root = Directory.CreateTempSubdirectory("mxc-checks-").FullName;
var ro = Directory.CreateDirectory(Path.Combine(root, "read-only")).FullName;
var rw = Directory.CreateDirectory(Path.Combine(root, "read-write")).FullName;
var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
await File.WriteAllTextAsync(Path.Combine(ro, "input.txt"), "READ_OK");
await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "UNGRANTED_SECRET");
SandboxProfile Profile(string command) => new() { Command = command, Folders = [new(ro, FolderAccess.ReadOnly), new(rw, FolderAccess.ReadWrite)] };
void Check(string name, Action body) => checks.Add((name, () => { body(); return Task.CompletedTask; }));
void CheckAsync(string name, Func<Task> body) => checks.Add((name, body));
void Assert(bool value, string message = "Assertion fehlgeschlagen") { if (!value) throw new Exception(message); }
void Reject(Action body) { try { body(); } catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { return; } throw new Exception("Ungültige Eingabe wurde akzeptiert"); }
async Task<(RunOutcome Outcome, string Output)> Run(SandboxProfile profile, CancellationToken token = default)
{
    var text = new StringBuilder(); var gate = new object();
    var outcome = await new SandboxRunner().RunAsync(profile, s => { lock (gate) text.Append(s); }, token);
    return (outcome, text.ToString());
}
string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
async Task<string> Script(string name, string body)
{
    var path = Path.Combine(ro, name + ".sh"); await File.WriteAllTextAsync(path, body);
    return "/bin/sh " + Quote(path);
}
Check("profile-roundtrip", () => Assert(SandboxProfile.FromJson(Profile("echo ok").ToJson()).Folders.Length == 2));
Check("unknown-profile-fields-refused", () => Reject(() => SandboxProfile.FromJson("{\"Typo\":true}")));
Check("unknown-profile-version-refused", () => Reject(() => (new SandboxProfile { Version = 2 }).Validate()));
Check("overlapping-grants-refused", () => Reject(() => (Profile("echo") with { Folders = [new(root, FolderAccess.ReadOnly), new(rw, FolderAccess.ReadWrite)] }).Validate()));
Check("root-grant-overlap-and-working-directory", () =>
{
    var drive = Path.GetPathRoot(root)!;
    Reject(() => (Profile("echo") with { Folders = [new(drive, FolderAccess.ReadOnly), new(rw, FolderAccess.ReadWrite)] }).Validate());
    (Profile("echo") with { Folders = [new(drive, FolderAccess.ReadOnly)], WorkingDirectory = rw }).Validate();
});
Check("missing-directory-refused", () => Reject(() => (Profile("echo") with { Folders = [new(Path.Combine(root, "missing"), FolderAccess.ReadOnly)] }).Validate()));
Check("invalid-rights-refused", () => Reject(() => (Profile("echo") with { Folders = [new(ro, (FolderAccess)44)] }).Validate()));
Check("timeout-bounds", () => { Reject(() => (Profile("echo") with { TimeoutSeconds = -1 }).Validate()); Reject(() => (Profile("echo") with { TimeoutSeconds = 86401 }).Validate()); });
Check("unlimited-session-timeout-explicit", () => (Profile("echo") with { TimeoutSeconds = 0 }).Validate());
Check("working-directory-must-be-granted", () => { (Profile("echo") with { WorkingDirectory = rw }).Validate(); Reject(() => (Profile("echo") with { WorkingDirectory = outside }).Validate()); });
Check("path-never-adds-current-directory", () =>
{
    var request = RequestFactory.Create(Profile("echo"), rw, null);
    Assert(!request.Environment!["PATH"].EndsWith(Path.PathSeparator) && !request.Environment["PATH"].Contains(new string(Path.PathSeparator, 2)));
});
Check("terminal-filter-preserves-drawing-removes-control-strings", () =>
{
    var filter = new TerminalOutputFilter();
    var parts = new[] { "ä\x1b[31mRED\x1b[0m\x1b", "]52;c;SECRET", "\aOK\x1bPtmux;payload\x1b", "\\END\x1b\x1b]52;hidden\aDONE" };
    var result = string.Concat(parts.Select(part => Encoding.UTF8.GetString(filter.Transform(Encoding.UTF8.GetBytes(part)))));
    Assert(result == "ä\x1b[31mRED\x1b[0mOKENDDONE", result);
});
Check("windows-executor-policy-stays-offline-and-refuses-dacl-fallback", () =>
{
    var request = RequestFactory.Create(Profile("cmd.exe"), rw, null);
    request.Containment = new Microsoft.Mxc.Sdk.V1.Containment.ProcessContainer();
    request.Ui = WindowsExecutorConfiguration.ConsoleUi();
    using var json = System.Text.Json.JsonDocument.Parse(WindowsExecutorConfiguration.Serialize(request));
    Assert(!json.RootElement.GetProperty("fallback").GetProperty("allowDaclMutation").GetBoolean());
    Assert(json.RootElement.GetProperty("network").GetProperty("ingress").GetProperty("hostLoopback").GetString() == "deny");
    Assert(!json.RootElement.GetProperty("process").GetProperty("inheritDefaultEnv").GetBoolean());
    Assert(!json.RootElement.GetProperty("ui").GetProperty("disable").GetBoolean());
    Assert(json.RootElement.GetProperty("ui").GetProperty("clipboard").GetString() == "none");
    Assert(!json.RootElement.GetProperty("ui").GetProperty("injection").GetBoolean());
    Assert(json.RootElement.GetProperty("processContainer").GetProperty("ui").GetProperty("isolation").GetString() == "container");
    request.Ui.Clipboard = Microsoft.Mxc.Sdk.V1.ClipboardPolicy.All;
    Reject(() => WindowsExecutorConfiguration.Serialize(request));
    request.Ui = WindowsExecutorConfiguration.ConsoleUi();
    request.Network!.Egress!.Default = Microsoft.Mxc.Sdk.V1.NetworkAction.Allow;
    Reject(() => WindowsExecutorConfiguration.Serialize(request));
});
Check("windows-installation-write-grants-refused", () =>
{
    WindowsExecutorConfiguration.ValidateInstallationGrants(Profile("cmd.exe"), outside);
    Reject(() => WindowsExecutorConfiguration.ValidateInstallationGrants(Profile("cmd.exe") with { Folders = [new(Path.GetPathRoot(root)!, FolderAccess.ReadWrite)] }, outside));
    Reject(() => WindowsExecutorConfiguration.ValidateInstallationGrants(Profile("cmd.exe") with { Folders = [new(root, FolderAccess.ReadWrite)] }, outside));
    Reject(() => WindowsExecutorConfiguration.ValidateInstallationGrants(Profile("cmd.exe") with { Folders = [new(outside, FolderAccess.ReadWrite)] }, root));
});
Check("exact-target-parser", () => { Assert(Target.Parse("EXAMPLE.com:443") == new Target("example.com", 443)); Assert(Target.Parse("[::1]:1234").Port == 1234); Assert(Target.Parse("example.com:80").Port == 80); });
Check("ambiguous-targets-refused", () => { foreach (var target in new[] { "example.com", "*.example.com:443", "example.com:0", "user@host:443", "host:443/path", "host:443?x", "host:443#x", "host:65536", "host:443\r\nx:x" }) Reject(() => Target.Parse(target)); });
Check("private-and-reserved-addresses-refused", () => { foreach (var ip in new[] { "127.0.0.1", "10.0.0.1", "192.168.1.1", "172.16.0.1", "169.254.169.254", "100.64.0.1", "198.18.0.1", "203.0.113.1", "::1", "fc00::1", "fe80::1", "::ffff:127.0.0.1", "2001:db8::1", "2002:7f00:1::" }) Assert(!FilteringProxy.IsPublic(IPAddress.Parse(ip)), ip); });
Check("public-addresses-accepted", () => { Assert(FilteringProxy.IsPublic(IPAddress.Parse("1.1.1.1"))); Assert(FilteringProxy.IsPublic(IPAddress.Parse("2606:4700:4700::1111"))); });

await using var fixture = new Fixture();
async Task<string> ProxyRequest(FilteringProxy proxy, string request)
{
    using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, new Uri(proxy.Endpoint).Port);
    var stream = client.GetStream(); await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
    using var reader = new StreamReader(stream);
    using var deadline = new CancellationTokenSource(5000);
    return await reader.ReadToEndAsync(deadline.Token);
}
CheckAsync("proxy-allow-http-get", async () =>
{
    await using var proxy = new FilteringProxy(new ProxySettings { Enabled = true, AllowedTargets = [$"127.0.0.1:{fixture.Port}"], AllowPrivateAddresses = true });
    var reply = await ProxyRequest(proxy, $"GET http://127.0.0.1:{fixture.Port}/ HTTP/1.1\r\nHost: forged.invalid\r\n\r\n");
    Assert(reply.Contains("FIXTURE_OK"), reply);
});
CheckAsync("proxy-deny-unlisted-host-and-port", async () =>
{
    await using var proxy = new FilteringProxy(new ProxySettings { Enabled = true, AllowedTargets = ["example.com:443"] });
    foreach (var authority in new[] { $"127.0.0.1:{fixture.Port}", "example.com:80", "evil.example.com:443" })
        Assert((await ProxyRequest(proxy, $"CONNECT {authority} HTTP/1.1\r\n\r\n")).Contains("403 Forbidden"));
});
CheckAsync("proxy-private-addresses-off-by-default", async () =>
{
    await using var proxy = new FilteringProxy(new ProxySettings { Enabled = true, AllowedTargets = [$"localhost:{fixture.Port}"] });
    Assert((await ProxyRequest(proxy, $"CONNECT localhost:{fixture.Port} HTTP/1.1\r\n\r\n")).Contains("403 Forbidden"));
});
CheckAsync("proxy-request-smuggling-refused", async () =>
{
    await using var proxy = new FilteringProxy(new ProxySettings { Enabled = true, AllowedTargets = [$"127.0.0.1:{fixture.Port}"], AllowPrivateAddresses = true });
    foreach (var header in new[] { "Content-Length: 1\r\n", "Transfer-Encoding: chunked\r\n", "Host: a\r\nHost: b\r\n", "Upgrade: websocket\r\n", " Host: bad\r\n", "X: x\nInjected: y\r\n" })
        Assert((await ProxyRequest(proxy, $"GET http://127.0.0.1:{fixture.Port}/ HTTP/1.1\r\n{header}\r\n")).Contains("403 Forbidden"));
});
CheckAsync("proxy-connect-tunnel", async () =>
{
    await using var proxy = new FilteringProxy(new ProxySettings { Enabled = true, AllowedTargets = [$"127.0.0.1:{fixture.Port}"], AllowPrivateAddresses = true });
    using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, new Uri(proxy.Endpoint).Port);
    var stream = client.GetStream();
    await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{fixture.Port} HTTP/1.1\r\n\r\n"));
    var header = new byte[39]; await stream.ReadExactlyAsync(header);
    Assert(Encoding.ASCII.GetString(header).Contains("200 Connection Established"));
    await stream.WriteAsync("GET / HTTP/1.1\r\nHost: fixture\r\n\r\n"u8.ToArray());
    using var reader = new StreamReader(stream); using var deadline = new CancellationTokenSource(5000);
    Assert((await reader.ReadToEndAsync(deadline.Token)).Contains("FIXTURE_OK"));
});
CheckAsync("proxy-disposal-closes-live-client", async () =>
{
    var proxy = new FilteringProxy(new ProxySettings { Enabled = true });
    using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, new Uri(proxy.Endpoint).Port);
    await proxy.DisposeAsync(); var buffer = new byte[1];
    Assert(await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 0);
});

if (!args.Contains("--unit-only") && !OperatingSystem.IsWindows())
{
    Check("native-backend-available", () => Assert(SandboxRunner.PlatformDescription().Contains("bereit")));
    CheckAsync("native-filesystem-read-write-denials", async () =>
    {
        var command = await Script("files", $"set -eu\ncat {Quote(Path.Combine(ro, "input.txt"))}\nprintf WRITE_OK > {Quote(Path.Combine(rw, "output.txt"))}\nif cat {Quote(Path.Combine(outside, "secret.txt"))}; then exit 51; fi\nif echo BAD > {Quote(Path.Combine(ro, "blocked.txt"))}; then exit 52; fi\nif echo BAD > {Quote(Path.Combine(outside, "blocked.txt"))}; then exit 53; fi\necho DENIALS_OK\n");
        var result = await Run(Profile(command)); Assert(result.Outcome.ExitCode == 0 && result.Output.Contains("READ_OK") && result.Output.Contains("DENIALS_OK"), result.Output);
        Assert(await File.ReadAllTextAsync(Path.Combine(rw, "output.txt")) == "WRITE_OK"); Assert(!File.Exists(Path.Combine(ro, "blocked.txt")) && !File.Exists(Path.Combine(outside, "blocked.txt")));
    });
    CheckAsync("native-symlink-escape-denied", async () =>
    {
        File.CreateSymbolicLink(Path.Combine(rw, "escape"), Path.Combine(outside, "secret.txt"));
        var command = await Script("symlink", $"if cat {Quote(Path.Combine(rw, "escape"))}; then exit 54; fi\nif echo BAD > {Quote(Path.Combine(rw, "escape"))}; then exit 55; fi\necho LINK_DENIED\n");
        var result = await Run(Profile(command)); Assert(result.Outcome.ExitCode == 0 && result.Output.Contains("LINK_DENIED"), result.Output);
        Assert(await File.ReadAllTextAsync(Path.Combine(outside, "secret.txt")) == "UNGRANTED_SECRET");
    });
    CheckAsync("native-host-environment-not-inherited", async () =>
    {
        Environment.SetEnvironmentVariable("MXC_TEST_SECRET", "DONT_INHERIT");
        try
        {
            var result = await Run(Profile(await Script("env", "test -z \"$MXC_TEST_SECRET\" && echo ENV_CLEAN\n")));
            Assert(result.Outcome.ExitCode == 0 && result.Output.Contains("ENV_CLEAN"), result.Output);
        }
        finally { Environment.SetEnvironmentVariable("MXC_TEST_SECRET", null); }
    });
    CheckAsync("native-network-blocked", async () =>
    {
        var result = await Run(Profile(await Script("offline", $"if /usr/bin/curl --noproxy '*' --max-time 2 -s http://127.0.0.1:{fixture.Port}/; then exit 56; fi\necho OFFLINE_OK\n")));
        Assert(result.Outcome.ExitCode == 0 && result.Output.Contains("OFFLINE_OK"), result.Output);
    });
    CheckAsync("native-proxy-allowed-denied-and-direct-bypass", async () =>
    {
        var command = await Script("proxy", $"set -eu\n/usr/bin/curl --noproxy '' --max-time 5 -fs http://127.0.0.1:{fixture.Port}/\nif /usr/bin/curl --noproxy '' --max-time 3 -fs http://localhost:{fixture.Port}/; then exit 57; fi\nif /usr/bin/curl --noproxy '*' --max-time 2 -fs http://127.0.0.1:{fixture.Port}/; then exit 58; fi\nif /usr/bin/curl --noproxy '*' --max-time 2 -fs http://1.1.1.1:80/; then exit 59; fi\necho PROXY_CONFINED\n");
        var result = await Run(Profile(command) with { Proxy = new ProxySettings { Enabled = true, AllowedTargets = [$"127.0.0.1:{fixture.Port}"], AllowPrivateAddresses = true } });
        Assert(result.Outcome.ExitCode == 0 && result.Output.Contains("FIXTURE_OK") && result.Output.Contains("PROXY_CONFINED"), result.Output);
    });
    CheckAsync("native-leader-exit-with-open-child-pipe", async () =>
    {
        var timer = Stopwatch.StartNew();
        var result = await Run(Profile(await Script("child-pipe", "sleep 30 2>/dev/null &\necho LEADER_DONE\n")));
        Assert(result.Outcome.ExitCode == 0 && result.Output.Contains("LEADER_DONE") && timer.Elapsed.TotalSeconds < 5, result.Output);
    });
    if (args.Contains("--internet"))
    {
        CheckAsync("native-proxy-real-https", async () =>
        {
            var result = await Run(Profile("/usr/bin/curl --noproxy '' --max-time 10 -fsS https://example.com") with
                { Proxy = new ProxySettings { Enabled = true, AllowedTargets = ["example.com:443"] } });
            Assert(result.Outcome.ExitCode == 0 && result.Output.Contains("Example Domain"), result.Output);
        });
    }
    CheckAsync("console-control-files-stay-denied-under-parent-grant", async () =>
    {
        var directory = ConsoleSessionFiles.Create(Profile("echo")); var protectedFile = Path.Combine(directory, ConsoleSessionFiles.RequestName);
        var command = await Script("control-denial", $"if cat {Quote(protectedFile)}; then exit 61; fi\nif echo BAD > {Quote(protectedFile)}; then exit 62; fi\necho CONTROL_DENIED\n");
        var profile = Profile(command) with { Folders = [new(ro, FolderAccess.ReadOnly), new(rw, FolderAccess.ReadWrite), new(Path.GetDirectoryName(ConsoleSessionFiles.ControlRoot)!, FolderAccess.ReadWrite)] };
        var result = await Run(profile);
        Assert(result.Outcome.ExitCode == 0 && result.Output.Contains("CONTROL_DENIED"), result.Output);
        Assert((await File.ReadAllTextAsync(protectedFile)).StartsWith("{"));
    });
    CheckAsync("native-timeout-kills-process", async () =>
    {
        var timer = Stopwatch.StartNew(); var result = await Run(Profile("/bin/sleep 30") with { TimeoutSeconds = 1 });
        Assert(result.Outcome.TimedOut && timer.Elapsed.TotalSeconds < 5, result.ToString());
    });
    CheckAsync("native-stop-kills-process", async () =>
    {
        using var cancel = new CancellationTokenSource(600); var timer = Stopwatch.StartNew();
        var result = await Run(Profile("/bin/sleep 30"), cancel.Token);
        Assert(result.Outcome.Stopped && timer.Elapsed.TotalSeconds < 5);
    });
}
var failed = 0;
Console.WriteLine($"Host: {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}; MXC SDK 1.0.1; fixtures: {root}");
foreach (var check in checks)
{
    try { await check.Body().WaitAsync(TimeSpan.FromSeconds(30)); Console.WriteLine("PASS " + check.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + check.Name + ": " + ex); }
}
Console.WriteLine($"{checks.Count - failed}/{checks.Count} passed");
return failed == 0 ? 0 : 1;

sealed class Fixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public Fixture() { listener.Start(); loop = Accept(); }
    private async Task Accept()
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Serve(client); } }
        catch (OperationCanceledException) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream(); var data = new byte[1]; var header = new StringBuilder();
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && header.Length < 16_384)
                {
                    var received = await stream.ReadAsync(data, stop.Token);
                    if (received == 0) return;
                    header.Append((char)data[0]);
                }
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 10\r\n\r\nFIXTURE_OK"u8.ToArray(), stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { await stop.CancelAsync(); listener.Stop(); await loop; stop.Dispose(); }
}
