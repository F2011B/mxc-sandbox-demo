using System.Collections.Concurrent;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MxcSandbox.Core;

namespace MxcSandbox.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly List<FolderRow> folders = [];
    private readonly StackPanel folderPanel = new() { Spacing = 8 };
    private readonly TextBox workingDirectory = new() { PlaceholderText = "Leer = eigener temporärer Ordner" };
    private Func<SandboxProfile, Action<string>, CancellationToken, Task<RunOutcome>> runSession = new ConsoleLauncher().RunAsync;
    private readonly TextBox command = new() { Text = new SandboxProfile().Command, AcceptsReturn = true, MinHeight = 72, PlaceholderText = "Befehl mit Argumenten" };
    private readonly NumericUpDown timeout = new() { Minimum = 0, Maximum = 86400, Value = 0, Width = 130 };
    private readonly CheckBox proxyEnabled = new() { Content = "Nur über den Filter-Proxy verbinden" };
    private readonly TextBox targets = new() { Text = "example.com:443\napi.github.com:443", AcceptsReturn = true, MinHeight = 95, PlaceholderText = "example.com:443 · ein Ziel pro Zeile" };
    private readonly NumericUpDown proxyPort = new() { Minimum = 0, Maximum = 65535, Value = 0, Width = 150 };
    private readonly CheckBox privateTargets = new() { Content = "Private / LAN-Adressen erlauben" };
    private readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 150, FontFamily = FontFamily.Parse("Menlo,Consolas,monospace"), FontSize = 12 };
    private readonly TextBlock status = new() { Text = "Bereit", Foreground = Brush.Parse("#25604E"), TextWrapping = TextWrapping.Wrap };
    private readonly Button run = new() { Content = "Sandbox starten", Background = Brush.Parse("#5146D9"), Foreground = Brushes.White };
    private readonly Button stop = new() { Content = "Beenden", IsEnabled = false };
    private readonly StackPanel editors = new() { Spacing = 18 };
    private readonly ConcurrentQueue<string> pending = new();
    private readonly DispatcherTimer logTimer;
    private CancellationTokenSource? cancellation;
    private Task? running;
    private bool closing;
    private int queuedCharacters;
    private const int LogLimit = 262_144;

    public MainWindow()
    {
        Title = "MXC Sandbox · Konsole";
        Width = 1060; Height = 870; MinWidth = 780; MinHeight = 650;
        Background = Brush.Parse("#F3F5FA");
        var platform = OperatingSystem.IsMacOS() ? "macOS · Seatbelt" : OperatingSystem.IsLinux() ? "Linux · Bubblewrap" : "Windows · ProcessContainer";
        var heading = new StackPanel { Spacing = 6, Children =
        {
            new TextBlock { Text = "MXC Sandbox", FontSize = 30, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#182139") },
            new TextBlock { Text = "Ordner freigeben. Netzwerk begrenzen. Eigene Konsole öffnen.", FontSize = 15, Foreground = Brush.Parse("#59677E") }
        }};
        var badge = new TextBlock { Text = platform + "  /  .NET 10", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#5146D9") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        header.Children.Add(heading); Grid.SetColumn(badge, 1); header.Children.Add(badge);
        var add = new Button { Content = "+ Ordner auswählen" };
        add.Click += async (_, _) => await GuardAsync(AddFolderAsync);
        var addOmp = new Button { Content = "OMP freigeben" };
        addOmp.Click += async (_, _) => await GuardAsync(() =>
        {
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var search = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat(new[]
            {
                "/opt/homebrew/bin", "/usr/local/bin", Path.Combine(user, ".local", "bin"), Path.Combine(user, ".bun", "bin"),
                Path.Combine(user, "scoop", "shims"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "omp"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links")
            });
            var executable = search.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "omp.exe" : "omp")).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("OMP wurde nicht gefunden. Seine Installation über Ordner auswählen mit Nur lesen freigeben.");
            var info = new FileInfo(executable); var actual = info.ResolveLinkTarget(true) ?? info;
            var install = Path.GetDirectoryName(actual.FullName) ?? throw new IOException("OMP-Installationsordner fehlt.");
            if (Path.GetFileName(install) == "bin") install = Path.GetDirectoryName(install)!;
            var grant = new FolderGrant(install, FolderAccess.ReadOnly);
            (Capture() with { Folders = folders.Select(row => row.Grant).Append(grant).ToArray() }).Validate();
            folders.Add(new FolderRow(install, FolderAccess.ReadOnly)); RefreshFolders();
            status.Text = "OMP freigegeben. Nach Starten kannst du in der Konsole omp eingeben.";
            return Task.CompletedTask;
        });
        var foldersContent = new StackPanel { Spacing = 12, Children =
        {
            Label("Ordnerrechte", 19),
            Hint("Freigaben gelten für den gesamten Unterordnerbaum. Ohne Freigabe bleiben persönliche Dateien gesperrt."),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, addOmp } }, folderPanel,
            Hint("Zusätzlich: eigener temporärer Arbeitsordner mit Schreibrechten. System- und Laufzeitdateien stellt MXC bereit.")
        }};
        var netContent = new StackPanel { Spacing = 10, Children =
        {
            Label("Netzwerk", 19), proxyEnabled,
            Hint("Ausgeschaltet: Netzwerk blockiert. Eingeschaltet: ausschließlich diese Ziele und Ports."),
            targets,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Label("Lokaler Proxy-Port", 13), proxyPort, Hint("0 = automatisch") } },
            privateTargets, Hint("Exakte Ziele host:port, keine Wildcards. HTTPS über CONNECT; HTTP unterstützt GET/HEAD. CONNECT begrenzt das Ziel, nicht den Inhalt.")
        }};
        proxyEnabled.IsEnabled = !OperatingSystem.IsWindows();
        targets.IsEnabled = proxyPort.IsEnabled = privateTargets.IsEnabled = false;
        proxyEnabled.IsCheckedChanged += (_, _) => targets.IsEnabled = proxyPort.IsEnabled = privateTargets.IsEnabled = proxyEnabled.IsChecked == true;
        var policyColumns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 18 };
        policyColumns.Children.Add(Card(foldersContent));
        var netCard = Card(netContent); Grid.SetColumn(netCard, 1); policyColumns.Children.Add(netCard);
        editors.Children.Add(policyColumns);
        var save = new Button { Content = "Profil speichern" };
        var load = new Button { Content = "Profil laden" };
        save.Click += async (_, _) => await GuardAsync(SaveAsync);
        load.Click += async (_, _) => await GuardAsync(LoadAsync);
        editors.Children.Add(Card(new StackPanel { Spacing = 10, Children =
        {
            Label("Startprogramm der Konsole", 19), command,
            Hint("Standard: Bash auf macOS/Linux, cmd.exe auf Windows. Darin kannst du OMP und andere Programme starten."),
            Label("Arbeitsverzeichnis (muss freigegeben sein)", 13), workingDirectory,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Label("Zeitlimit in Sekunden (0 = ohne)", 13), timeout, save, load } }
        }}));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { run, stop } };
        run.Click += (_, _) => { running = RunAsync(); };
        stop.Click += (_, _) => cancellation?.Cancel();
        var note = OperatingSystem.IsMacOS()
            ? "Demo mit nativer MXC-Isolation. Seatbelt teilt die Benutzeridentität; keine vollständige Abschottung von Prozessmetadaten. Nur mit unkritischen Testdaten verwenden."
            : "Demo mit nativer MXC-Isolation. Backend-Voraussetzungen werden beim Start geprüft. Windows-Proxy ist in dieser Demo nicht freigeschaltet.";
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(28), Children =
        {
            header, Hint(note), editors
        }};
        log.MinHeight = 45; log.MaxHeight = 70;
        var footer = new StackPanel { Margin = new Thickness(28, 10, 28, 16), Spacing = 8, Children = { actions, status, log } };
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(footer, 1); layout.Children.Add(footer); Content = layout;
        logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        logTimer.Tick += (_, _) => FlushLog(); logTimer.Start();
        Opened += async (_, _) =>
        {
            try { status.Text = await Task.Run(SandboxRunner.PlatformDescription); }
            catch (Exception ex) { status.Text = "MXC konnte nicht geladen werden: " + ex.Message; }
        };
        Closing += async (_, e) =>
        {
            if (running is { IsCompleted: false })
            {
                e.Cancel = true;
                if (closing) return;
                closing = true; cancellation?.Cancel(); await running; Close();
            }
        };
        Closed += (_, _) => logTimer.Stop();
        RefreshFolders();
    }

    private async Task AddFolderAsync()
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Ordner für die Sandbox freigeben", AllowMultiple = true });
        foreach (var item in selected)
        {
            var path = item.TryGetLocalPath() ?? throw new ArgumentException("Nur lokale Ordner werden unterstützt.");
            var candidate = Capture() with { Folders = folders.Select(f => f.Grant).Append(new FolderGrant(path, FolderAccess.ReadOnly)).ToArray() };
            candidate.Validate();
            folders.Add(new FolderRow(path, FolderAccess.ReadOnly));
        }
        RefreshFolders();
    }
    private void RefreshFolders()
    {
        folderPanel.Children.Clear();
        if (folders.Count == 0) folderPanel.Children.Add(Hint("Noch keine Ordner freigegeben."));
        foreach (var row in folders)
        {
            var remove = new Button { Content = "×", Padding = new Thickness(10, 4) };
            remove.Click += (_, _) => { folders.Remove(row); RefreshFolders(); };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            var fields = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = row.Path, TextWrapping = TextWrapping.Wrap, FontSize = 12 }, row.Access } };
            grid.Children.Add(fields); Grid.SetColumn(remove, 1); grid.Children.Add(remove); folderPanel.Children.Add(grid);
        }
    }
    private SandboxProfile Capture() => new()
    {
        Command = command.Text ?? "", TimeoutSeconds = (int)(timeout.Value ?? 0), WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory.Text) ? null : workingDirectory.Text, Folders = folders.Select(f => f.Grant).ToArray(),
        Proxy = new ProxySettings { Enabled = proxyEnabled.IsChecked == true, ListenPort = (int)(proxyPort.Value ?? 0), AllowPrivateAddresses = privateTargets.IsChecked == true,
            AllowedTargets = (targets.Text ?? "").Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) }
    };
    private async Task SaveAsync()
    {
        var text = Capture().ToJson();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Sandbox-Profil speichern", SuggestedFileName = "sandbox-profile.json", DefaultExtension = "json" });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
        await using var writer = new StreamWriter(stream); await writer.WriteAsync(text); status.Text = "Profil gespeichert.";
    }
    private async Task LoadAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Sandbox-Profil laden", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("JSON-Profil") { Patterns = ["*.json"] }] });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        var buffer = new char[262_145]; var read = await reader.ReadBlockAsync(buffer);
        var profile = SandboxProfile.FromJson(new string(buffer, 0, read));
        ApplyProfile(profile);
        status.Text = "Profil geladen. Start erfolgt erst per Klick.";
    }
    private void ApplyProfile(SandboxProfile profile)
    {
        profile.Validate();
        if (OperatingSystem.IsWindows() && profile.Proxy.Enabled) throw new PlatformNotSupportedException("Dieses Profil benötigt einen Proxy. Der Windows-Durchstich unterstützt derzeit nur Offline-Sitzungen.");
        command.Text = profile.Command; timeout.Value = profile.TimeoutSeconds; workingDirectory.Text = profile.WorkingDirectory;
        proxyEnabled.IsChecked = profile.Proxy.Enabled; proxyPort.Value = profile.Proxy.ListenPort;
        privateTargets.IsChecked = profile.Proxy.AllowPrivateAddresses; targets.Text = string.Join("\n", profile.Proxy.AllowedTargets);
        folders.Clear(); folders.AddRange(profile.Folders.Select(f => new FolderRow(f.Path, f.Access))); RefreshFolders();
    }
    private async Task RunAsync()
    {
        if (!run.IsEnabled) return;
        try
        {
            var profile = Capture(); profile.Validate();
            run.IsEnabled = false; editors.IsEnabled = false; stop.IsEnabled = true; log.Text = "";
            cancellation = new CancellationTokenSource(); status.Text = "Sandbox-Konsole geöffnet …";
            var outcome = await runSession(profile, EnqueueLog, cancellation.Token);
            status.Text = outcome.Stopped ? "Beendet." : outcome.TimedOut ? "Zeitlimit erreicht; Prozess beendet." : $"Abgeschlossen · Exit-Code {outcome.ExitCode}";
        }
        catch (Exception ex) { status.Text = "Start/Ausführung fehlgeschlagen: " + ex.Message; }
        finally
        {
            FlushLog(); cancellation?.Dispose(); cancellation = null;
            run.IsEnabled = true; editors.IsEnabled = true; stop.IsEnabled = false;
        }
    }
    private void EnqueueLog(string text)
    {
        if (Interlocked.Add(ref queuedCharacters, text.Length) > LogLimit)
        { Interlocked.Add(ref queuedCharacters, -text.Length); return; }
        pending.Enqueue(text);
    }
    private void FlushLog()
    {
        if (pending.IsEmpty) return;
        var text = new StringBuilder(log.Text);
        var count = 0;
        while (count++ < 256 && pending.TryDequeue(out var item)) { Interlocked.Add(ref queuedCharacters, -item.Length); text.Append(item); }
        if (text.Length > LogLimit) text.Remove(0, text.Length - LogLimit);
        log.Text = text.ToString(); log.CaretIndex = log.Text.Length;
    }
    private async Task GuardAsync(Func<Task> action) { try { await action(); } catch (Exception ex) { status.Text = ex.Message; } }
    private static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 12, Foreground = Brush.Parse("#59677E"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Border Card(Control child) => new() { Child = child, Padding = new Thickness(18), Background = Brushes.White, CornerRadius = new CornerRadius(12), BorderBrush = Brush.Parse("#E0E5EF"), BorderThickness = new Thickness(1) };
    private sealed class FolderRow
    {
        public string Path { get; }
        public ComboBox Access { get; }
        public FolderGrant Grant => new(Path, Access.SelectedIndex == 1 ? FolderAccess.ReadWrite : FolderAccess.ReadOnly);
        public FolderRow(string path, FolderAccess access)
        {
            Path = path; Access = new ComboBox { ItemsSource = new[] { "Nur lesen", "Lesen und schreiben" }, SelectedIndex = access == FolderAccess.ReadWrite ? 1 : 0, MinWidth = 190 };
        }
    }
}
