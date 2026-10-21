using System.Text;
using Microsoft.Mxc.Sdk.V1;
using MxcSandbox.Core;
using MxcSandbox.ConsoleHost;

if (args.Length != 2 || args[0] != "--session")
{
    Console.Error.WriteLine("Dieser Konsolenstarter wird von der Sandbox-GUI geöffnet."); return 2;
}
var directory = Path.GetFullPath(args[1]);
using var stop = new CancellationTokenSource();
try
{
    var request = ConsoleSessionFiles.Read(directory);
    using (new FileStream(Path.Combine(directory, "started"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
    if (File.Exists(Path.Combine(directory, "stop")) || !ConsoleSessionFiles.OwnerAlive(request)) throw new OperationCanceledException("Start wurde abgebrochen.");
    using (var self = System.Diagnostics.Process.GetCurrentProcess())
        ConsoleSessionFiles.Write(Path.Combine(directory, "host.json"), request with { OwnerPid = self.Id, OwnerStartTicks = ConsoleSessionFiles.StartToken(self) });
    Console.OutputEncoding = new UTF8Encoding(false);
    if (OperatingSystem.IsWindows()) Console.InputEncoding = new UTF8Encoding(false);
    Console.WriteLine("MXC Sandbox-Konsole · exit beendet diese Sitzung");
    Console.WriteLine("Datei- und Netzwerkrechte gelten für die Shell und alle gestarteten Programme.");
    Console.CancelKeyPress += (_, e) => e.Cancel = true;
    var watcher = Task.Run(async () =>
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (File.Exists(Path.Combine(directory, "stop")) || !ConsoleSessionFiles.OwnerAlive(request)) { await stop.CancelAsync(); return; }
                await Task.Delay(150, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    });
    RunOutcome outcome;
    if (OperatingSystem.IsWindows()) outcome = await WindowsConsole.RunAsync(request.Profile, directory, stop.Token);
    else
    {
        using var terminal = new UnixTerminal();
        // Raw descriptor streams avoid System.Console re-enabling cooked/ISIG
        // modes on its first read and swallowing Ctrl+C meant for the sandbox.
        using var input = UnixTerminal.OpenInput(stop.Token); using var output = UnixTerminal.OpenOutput();
        outcome = await new InteractiveRunner().RunAsync(request.Profile, input, output, UnixTerminal.Size, stop.Token);
    }
    ConsoleSessionFiles.Write(Path.Combine(directory, "result.json"), new ConsoleSessionResult(outcome.ExitCode, outcome.TimedOut, outcome.Stopped));
    await stop.CancelAsync(); await watcher;
    return outcome.ExitCode;
}
catch (OperationCanceledException)
{
    if (Directory.Exists(directory)) ConsoleSessionFiles.Write(Path.Combine(directory, "result.json"), new ConsoleSessionResult(130, false, true));
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine("\nSandbox-Konsole konnte nicht ausgeführt werden: " + ex.Message);
    if (Directory.Exists(directory)) ConsoleSessionFiles.Write(Path.Combine(directory, "result.json"), new ConsoleSessionResult(125, false, stop.IsCancellationRequested, ex.Message));
    return 125;
}
finally { await stop.CancelAsync(); }
