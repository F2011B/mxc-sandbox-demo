using Microsoft.Mxc.Sdk.V1;

namespace MxcSandbox.Core;

public sealed record RunOutcome(int ExitCode, bool TimedOut, bool Stopped, string ScratchDirectory);

public sealed class SandboxRunner
{
    public static string PlatformDescription()
    {
        var support = MxcPlatform.GetPlatformSupport();
        return support.IsSupported ? "MXC bereit · " + string.Join(", ", support.AvailableMethods) : "MXC nicht verfügbar: " + support.Reason;
    }

    public async Task<RunOutcome> RunAsync(SandboxProfile profile, Action<string> output, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var scratch = Directory.CreateTempSubdirectory("mxc-demo-").FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        output("Temporärer Arbeitsordner (Lesen/Schreiben): " + scratch + "\n");
        // Kept for inspection, never recursively deleted: the workload controls its contents.
        await using var proxy = profile.Proxy.Enabled ? new FilteringProxy(profile.Proxy, line => output(line + "\n")) : null;
        var request = RequestFactory.Create(profile, scratch, proxy?.Endpoint);
        if (proxy is not null) output("Proxy: " + proxy.Endpoint + " · nur freigegebene Ziele\n");
        if (OperatingSystem.IsWindows())
        {
            var probe = MxcContainer.Probe(request);
            if (probe.Error is not null || probe.NeedsDaclAugmentation != false)
                throw new PlatformNotSupportedException("Windows benötigt einen passenden ProcessContainer ohne Änderung der Host-Dateirechte: " + probe.Error);
        }
        using var process = await Task.Run(() => MxcContainer.Spawn(request, new SpawnOptions { Telemetry = new TelemetryConfig { Enabled = false } }));
        foreach (var warning in process.Warnings) output("MXC-Hinweis: " + warning + "\n");
        // Cancellation of an SDK await does NOT terminate the workload. Kill the live handle.
        using var registration = cancellationToken.Register(() => process.Kill());
        process.StandardInput?.Dispose();
        var stdout = PumpAsync(process.StandardOutput, output);
        var stderr = PumpAsync(process.StandardError, text => output("[stderr] " + text));
        var result = await process.WaitAsync();
        // Descendants may retain stdout handles after the leader exits. Bound draining too.
        var drains = Task.WhenAll(stdout, stderr);
        if (await Task.WhenAny(drains, Task.Delay(1500)) != drains)
        {
            process.Kill();
            process.StandardOutputCloser?.Close();
            process.StandardErrorCloser?.Close();
        }
        await drains;
        return new RunOutcome(result.ExitCode, result.TimedOut, cancellationToken.IsCancellationRequested, scratch);
    }

    private static Task PumpAsync(Stream? stream, Action<string> output) => Task.Run(async () =>
    {
        if (stream is null) return;
        using var reader = new StreamReader(stream);
        var buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer)) != 0) output(new string(buffer, 0, read));
    });
}
