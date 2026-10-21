using System.Diagnostics;
using System.Text.Json;
using MxcSandbox.Core;

namespace MxcSandbox.Desktop;

internal sealed class ConsoleLauncher
{
    public async Task<RunOutcome> RunAsync(SandboxProfile profile, Action<string> log, CancellationToken cancellationToken)
    {
        profile.Validate();
        var directory = ConsoleSessionFiles.Create(profile);
        var helper = Path.Combine(AppContext.BaseDirectory, "console", OperatingSystem.IsWindows() ? "MxcSandbox.Console.exe" : "MxcSandbox.Console");
        if (!File.Exists(helper)) throw new FileNotFoundException("Konsolenstarter fehlt. Die Anwendung vollständig bauen oder das App-Paket verwenden.", helper);
        using var registration = cancellationToken.Register(() => File.WriteAllText(Path.Combine(directory, "stop"), "stop"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenTerminal(helper, directory);
            log("Eigene Sandbox-Konsole geöffnet. Mit exit beenden; Stop schließt die Sandbox.\n");

            var waiting = Stopwatch.StartNew();
            while (true)
            {
                var resultPath = Path.Combine(directory, "result.json");
                if (File.Exists(resultPath))
                {
                    var result = JsonSerializer.Deserialize<ConsoleSessionResult>(await File.ReadAllTextAsync(resultPath)) ?? throw new IOException("Ungültiges Konsolenergebnis.");
                    if (result.Error is not null) throw new IOException(result.Error);
                    return new RunOutcome(result.ExitCode, result.TimedOut, result.Stopped, directory);
                }
                var hostPath = Path.Combine(directory, "host.json");
                if (cancellationToken.IsCancellationRequested && !File.Exists(hostPath)) return new RunOutcome(130, false, true, directory);
                if (File.Exists(hostPath))
                {
                    var host = JsonSerializer.Deserialize<ConsoleSessionRequest>(await File.ReadAllTextAsync(hostPath));
                    if (host is not null && !ConsoleSessionFiles.OwnerAlive(host))
                        return new RunOutcome(130, false, true, directory);
                }
                else if (File.Exists(Path.Combine(directory, "bootstrap-error.log")) && new FileInfo(Path.Combine(directory, "bootstrap-error.log")).Length > 0)
                    throw new IOException("Konsolenstarter: " + await File.ReadAllTextAsync(Path.Combine(directory, "bootstrap-error.log")));
                else if (waiting.Elapsed > TimeSpan.FromSeconds(45))
                {
                    var bootstrap = Path.Combine(directory, "bootstrap-error.log");
                    var detail = File.Exists(bootstrap) ? await File.ReadAllTextAsync(bootstrap) : "";
                    throw new TimeoutException("Die Terminal-Anwendung hat den Konsolenstarter nicht gestartet. " + detail);
                }
                await Task.Delay(100);
            }
        }
        finally { File.WriteAllText(Path.Combine(directory, "stop"), "stop"); }
    }

    internal static void OpenTerminal(string helper, string directory)
    {
        var info = new ProcessStartInfo();
        if (OperatingSystem.IsWindows())
        {
            WindowsTerminalWindow.Open(helper, directory);
            return;
        }
        else if (OperatingSystem.IsMacOS())
        {
            var script = Path.Combine(directory, "Sandbox.command");
            var invocation = ShellQuote(helper);
            var runtime = Environment.ProcessPath;
            if (runtime is not null && Path.GetFileNameWithoutExtension(runtime) == "dotnet")
                invocation = ShellQuote(runtime) + " " + ShellQuote(helper + ".dll");
            File.WriteAllText(script, "#!/bin/sh\nexec " + invocation + " --session " + ShellQuote(directory) + " 2>" + ShellQuote(Path.Combine(directory, "bootstrap-error.log")) + "\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            info.FileName = "/usr/bin/open";
            info.ArgumentList.Add("-a"); info.ArgumentList.Add("Terminal"); info.ArgumentList.Add(script);
        }
        else
        {
            var terminal = new[] { "/usr/bin/x-terminal-emulator", "/usr/bin/gnome-terminal", "/usr/bin/konsole", "/usr/bin/xterm" }.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Keine Terminal-Anwendung gefunden.");
            info.FileName = terminal; info.ArgumentList.Add(terminal.EndsWith("gnome-terminal", StringComparison.Ordinal) ? "--" : "-e");
            info.ArgumentList.Add(helper); info.ArgumentList.Add("--session"); info.ArgumentList.Add(directory);
        }
        using var launched = Process.Start(info) ?? throw new IOException("Terminal konnte nicht geöffnet werden.");
    }
    internal static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";
}
