using Microsoft.Mxc.Sdk.V1;

namespace MxcSandbox.Core;

public sealed class InteractiveRunner
{
    public async Task<RunOutcome> RunAsync(SandboxProfile profile, Stream input, Stream output,
        Func<MxcPtySize> terminalSize, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows verwendet den nativen MXC-Executor mit vererbter Konsole.");
        profile.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var scratch = Directory.CreateTempSubdirectory("mxc-shell-").FullName;
        File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var proxy = profile.Proxy.Enabled ? new FilteringProxy(profile.Proxy) : null;
        var request = RequestFactory.Create(profile, scratch, proxy?.Endpoint);
        request.Containment = OperatingSystem.IsMacOS() ? new Containment.Seatbelt { NestedPty = true } : new Containment.Bubblewrap();
        var initialSize = terminalSize();
        using var process = await Task.Run(() => MxcContainer.SpawnWithPty(request,
            new SpawnWithPtyOptions { Size = initialSize, Telemetry = new TelemetryConfig { Enabled = false } }));
        // Taking closers before readers avoids races with readers reaching EOF/disposal.
        var terminalInput = process.Input;
        var terminalOutput = process.Output;
        using var closer = process.StandardOutputCloser;
        using var lifetime = new CancellationTokenSource();
        using var registration = cancellationToken.Register(process.Kill);
        var reading = Task.Factory.StartNew(() =>
        {
            var filter = new TerminalOutputFilter(); var buffer = new byte[8192];
            int count;
            while ((count = terminalOutput.Read(buffer, 0, buffer.Length)) > 0)
            {
                var safe = filter.Transform(buffer.AsSpan(0, count));
                output.Write(safe, 0, safe.Length); output.Flush();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var writing = Task.Factory.StartNew(() =>
        {
            var buffer = new byte[4096];
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    terminalInput.Write(buffer, 0, count); terminalInput.Flush();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested) { }
            catch (IOException) when (lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested) { }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var resizing = Task.Run(async () =>
        {
            var size = initialSize;
            try
            {
                while (true)
                {
                    await Task.Delay(150, lifetime.Token);
                    var next = terminalSize();
                    if (next != size) { process.Resize(next); size = next; }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        try
        {
            var result = await process.WaitAsync();
            if (await Task.WhenAny(reading, Task.Delay(1000)) != reading) { process.Kill(); closer?.Close(); }
            await reading;
            return new RunOutcome(result.ExitCode, result.TimedOut, cancellationToken.IsCancellationRequested, scratch);
        }
        finally
        {
            await lifetime.CancelAsync();
            // Console stdin may be a synchronous OS read that cannot be cancelled;
            // never block shutdown on it. It belongs to this dedicated helper process.
            try { await resizing; } catch (ObjectDisposedException) { }
            _ = writing.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
