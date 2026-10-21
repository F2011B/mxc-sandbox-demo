using Avalonia.Controls;
using Avalonia.Interactivity;
using MxcSandbox.Core;

namespace MxcSandbox.Desktop;

// Invoked only by the explicit --smoke diagnostic entrypoint, with a headless
// Avalonia platform and real MXC processes. Native OS dialogs need manual QA.
public sealed partial class MainWindow
{
    internal async Task CheckInteractionsAsync()
    {
        // GUI state test uses real captured MXC children; terminal I/O is tested separately.
        runSession = new SandboxRunner().RunAsync;
        var fixture = Directory.CreateTempSubdirectory("mxc-ui-check-").FullName;
        var profile = new SandboxProfile
        {
            Command = OperatingSystem.IsWindows() ? "cmd.exe /c echo UI_MXC_OK" : "/bin/echo UI_MXC_OK",
            Folders = [new(fixture, FolderAccess.ReadOnly)],
            Proxy = new ProxySettings { Enabled = !OperatingSystem.IsWindows(), AllowedTargets = ["example.com:443"] }
        };
        ApplyProfile(SandboxProfile.FromJson(profile.ToJson()));
        if (folders.Count != 1 || folders[0].Grant.Access != FolderAccess.ReadOnly || targets.IsEnabled == OperatingSystem.IsWindows())
            throw new Exception("GUI hat Profil oder Rechte nicht übernommen.");
        folders[0].Access.SelectedIndex = 1;
        proxyEnabled.IsChecked = false;
        if (Capture().Folders[0].Access != FolderAccess.ReadWrite || targets.IsEnabled)
            throw new Exception("GUI-Rechte/Netzwerk wurden nicht aktualisiert.");
        run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (run.IsEnabled || !stop.IsEnabled || editors.IsEnabled || running is null)
            throw new Exception("GUI-Startzustand ist inkonsistent.");
        await running;
        FlushLog();
        if (!run.IsEnabled || stop.IsEnabled || !editors.IsEnabled || !(log.Text ?? "").Contains("UI_MXC_OK"))
            throw new Exception("GUI-Ausführung/Ausgabe fehlgeschlagen: " + status.Text);
        if (!OperatingSystem.IsWindows())
        {
            command.Text = "/bin/sleep 30";
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(250);
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await running!;
            if (status.Text != "Beendet.") throw new Exception("GUI-Stop fehlgeschlagen: " + status.Text);
        }
    }
}
