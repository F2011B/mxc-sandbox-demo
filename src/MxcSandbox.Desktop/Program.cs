using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Controls.ApplicationLifetimes;

namespace MxcSandbox.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--console-smoke")
        {
            var profile = new MxcSandbox.Core.SandboxProfile
            {
                Command = OperatingSystem.IsWindows() ? "cmd.exe /D /C exit 0" : "/bin/sh -c 'test -t 0 && test -t 1 && echo TERMINAL_WINDOW_OK; exit'",
                TimeoutSeconds = 10
            };
            var result = new ConsoleLauncher().RunAsync(profile, Console.Write, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("Native terminal launch exit=" + result.ExitCode);
            return result.ExitCode;
        }
        if (args.Length == 2 && args[0] == "--render" || args.Length == 1 && args[0] == "--smoke")
        {
            BuildApp().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var window = new MainWindow();
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (args[0] == "--smoke")
            {
                var check = window.CheckInteractionsAsync();
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!check.IsCompleted && DateTime.UtcNow < deadline)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(10);
                }
                if (!check.IsCompleted) throw new TimeoutException("GUI-Smoke-Test hat das Zeitlimit überschritten.");
                check.GetAwaiter().GetResult();
                window.Close();
                Console.WriteLine("PASS GUI profile/rights/network/run/output/stop");
                return 0;
            }
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Kein Renderbild.");
            frame.Save(args[1], new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            window.Close();
            Console.WriteLine("GUI gerendert: " + args[1]);
            return 0;
        }
        return BuildApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
