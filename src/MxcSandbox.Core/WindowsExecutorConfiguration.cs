using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;

namespace MxcSandbox.Core;

/// <summary>Exact stable contract for the official Windows executor. It runs with
/// inherited console handles, unlike the SDK's captured/PTY launch operations.</summary>
public static class WindowsExecutorConfiguration
{
    // Console and .NET startup need Win32k; every cross-container UI capability stays denied.
    public static UiPolicy ConsoleUi() => new() { Disable = false, Clipboard = ClipboardPolicy.None, AllowInputInjection = false };

    public static void ValidateInstallationGrants(SandboxProfile profile, string installationDirectory)
    {
        var installation = SandboxProfile.CanonicalDirectory(installationDirectory);
        foreach (var grant in profile.Folders.Where(grant => grant.Access == FolderAccess.ReadWrite))
        {
            var path = SandboxProfile.CanonicalDirectory(grant.Path);
            static bool Within(string child, string parent) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (Within(installation, path) || Within(path, installation))
                throw new ArgumentException("Der Sandbox-Installationsordner darf nicht schreibend freigegeben werden. Einen separaten Projektordner auswählen.");
        }
    }

    public static string Serialize(ContainerRequest request)
    {
        if (request.Containment is not Containment.ProcessContainer || request.Environment is null || request.InheritDefaultEnvironment ||
            request.Network?.RuntimeConfig?.NetworkProxy is not null || request.Network?.Egress?.Default != NetworkAction.Deny ||
            request.Network?.Ingress?.Default != NetworkAction.Deny || request.Network?.Ingress?.HostLoopback != NetworkAction.Deny ||
            request.Network.Egress.Allow is { Count: > 0 } || request.Network.Egress.Deny is { Count: > 0 } ||
            request.Ui is not { Disable: false, Clipboard: ClipboardPolicy.None, AllowInputInjection: false })
            throw new ArgumentException("Der Windows-Konsolenpfad akzeptiert nur die explizite Offline-Policy ohne geerbte Umgebung.");
        return JsonSerializer.Serialize(new
        {
            version = "1.0.0", containment = "processcontainer",
            process = new { commandLine = request.Command, cwd = request.WorkingDirectory,
                env = request.Environment.Select(item => item.Key + "=" + item.Value).ToArray(), inheritDefaultEnv = false, timeout = request.TimeoutMs ?? 0 },
            filesystem = new { readonlyPaths = request.Filesystem?.ReadonlyPaths.ToArray() ?? [], readwritePaths = request.Filesystem?.ReadwritePaths.ToArray() ?? [], deniedPaths = request.Filesystem?.DeniedPaths.ToArray() ?? [] },
            fallback = new { allowDaclMutation = false },
            network = new { egress = new { @default = "deny" }, ingress = new { @default = "deny", hostLoopback = "deny" } },
            processContainer = new { ui = new { isolation = "container", desktopSystemControl = false, systemSettings = "none", ime = false } },
            ui = new { disable = false, clipboard = "none", injection = false }, telemetry = new { enabled = false }
        });
    }
}
