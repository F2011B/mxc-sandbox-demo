using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Mxc.Sdk.V1;

namespace MxcSandbox.Core;

[JsonConverter(typeof(JsonStringEnumConverter<FolderAccess>))]
public enum FolderAccess { ReadOnly, ReadWrite }
public sealed record FolderGrant(string Path, FolderAccess Access);
public sealed record ProxySettings
{
    public bool Enabled { get; init; }
    public int ListenPort { get; init; }
    public string[] AllowedTargets { get; init; } = ["example.com:443"];
    public bool AllowPrivateAddresses { get; init; }
}
public sealed record SandboxProfile
{
    public int Version { get; init; } = 1;
    public string Command { get; init; } = OperatingSystem.IsWindows() ? "cmd.exe /D /Q" : "/bin/bash --noprofile --norc -i";
    public int TimeoutSeconds { get; init; } = 0;
    public string? WorkingDirectory { get; init; }
    public FolderGrant[] Folders { get; init; } = [];
    public ProxySettings Proxy { get; init; } = new();

    public void Validate()
    {
        if (Version != 1) throw new ArgumentException("Unbekannte Profilversion.");
        if (string.IsNullOrWhiteSpace(Command) || Command.Length > 16_384 || Command.Contains('\0'))
            throw new ArgumentException("Ein gültiger Befehl wird benötigt (maximal 16 KiB).");
        if (TimeoutSeconds is < 0 or > 86_400) throw new ArgumentException("Laufzeit muss 0 (ohne Limit) oder 1–86400 Sekunden sein.");
        if (Folders is null || Proxy is null || Proxy.AllowedTargets is null) throw new ArgumentException("Ungültiges Profil.");
        if (Proxy.ListenPort is < 0 or > 65535) throw new ArgumentException("Proxy-Port muss 0 (automatisch) oder 1–65535 sein.");
        var paths = new List<string>();
        foreach (var grant in Folders)
        {
            if (grant is null || !Enum.IsDefined(grant.Access)) throw new ArgumentException("Ungültige Ordnerrechte.");
            var path = CanonicalDirectory(grant.Path);
            if (paths.Any(other => Within(path, other) || Within(other, path)))
                throw new ArgumentException("Überlappende Ordnerfreigaben sind in der Demo nicht erlaubt.");
            paths.Add(path);
        }
        if (WorkingDirectory is not null)
        {
            var cwd = CanonicalDirectory(WorkingDirectory);
            if (!paths.Any(root => Within(cwd, root))) throw new ArgumentException("Arbeitsverzeichnis muss in einem freigegebenen Ordner liegen.");
        }
        if (Proxy.Enabled)
        {
            if (Proxy.AllowedTargets.Length == 0) throw new ArgumentException("Mindestens ein Proxy-Ziel host:port wird benötigt.");
            foreach (var target in Proxy.AllowedTargets) Target.Parse(target);
        }
    }

    public static string CanonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new ArgumentException($"Ordner existiert nicht oder ist nicht absolut: {path}");
        // Resolve every ancestor as well as the leaf; reject overlaps through symlink aliases.
        var full = System.IO.Path.GetFullPath(path);
        var current = System.IO.Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var info = new DirectoryInfo(System.IO.Path.Combine(current, part));
            current = info.ResolveLinkTarget(true)?.FullName ?? info.FullName;
        }
        return System.IO.Path.TrimEndingDirectorySeparator(current);
    }

    private static bool Within(string path, string root) => path.Equals(root, PathComparison) ||
        path.StartsWith(System.IO.Path.EndsInDirectorySeparator(root) ? root : root + System.IO.Path.DirectorySeparatorChar, PathComparison);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public string ToJson() { Validate(); return JsonSerializer.Serialize(this, JsonOptions); }
    public static SandboxProfile FromJson(string text)
    {
        if (text.Length > 262_144) throw new ArgumentException("Profil ist zu groß.");
        var profile = JsonSerializer.Deserialize<SandboxProfile>(text, JsonOptions) ?? throw new ArgumentException("Leeres Profil.");
        profile.Validate();
        return profile;
    }
}

public sealed record Target(string Host, int Port)
{
    public static Target Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace) || text.IndexOfAny(['/', '\\', '?', '#', '@', '%', '*']) >= 0 ||
            !Uri.TryCreate("http://" + text, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535 ||
            uri.GetComponents(UriComponents.StrongPort, UriFormat.Unescaped).Length == 0 || !HasExplicitPort(text))
            throw new ArgumentException($"Ungültiges Ziel '{text}'. Exakt host:port angeben, z.B. example.com:443.");
        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new ArgumentException("Ungültiger Hostname.");
        return new Target(host, uri.Port);
    }
    private static bool HasExplicitPort(string text) => int.TryParse(text[(text.LastIndexOf(':') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

public static class RequestFactory
{
    public static ContainerRequest Create(SandboxProfile profile, string scratch, string? proxyEndpoint)
    {
        profile.Validate();
        var fs = new FilesystemPolicy();
        fs.DeniedPaths.Add(ConsoleSessionFiles.ControlRoot);
        fs.DeniedPaths.Add(AppContext.BaseDirectory);
        fs.ReadwritePaths.Add(SandboxProfile.CanonicalDirectory(scratch));
        foreach (var folder in profile.Folders)
            (folder.Access == FolderAccess.ReadOnly ? fs.ReadonlyPaths : fs.ReadwritePaths).Add(SandboxProfile.CanonicalDirectory(folder.Path));
        if (profile.Proxy.Enabled != (proxyEndpoint is not null)) throw new ArgumentException("Proxy und Sandbox-Konfiguration stimmen nicht überein.");
        // The Windows identity-less loopback proxy opens other host services. Refuse that weaker mode.
        if (OperatingSystem.IsWindows() && profile.Proxy.Enabled)
            throw new PlatformNotSupportedException("Proxy auf Windows benötigt einen identitätsgebundenen Proxy-Peer. Diese Demo verweigert den schwächeren Loopback-Modus.");
        var env = new Dictionary<string, string>
        {
            ["HOME"] = scratch, ["TMPDIR"] = scratch, ["TMP"] = scratch, ["TEMP"] = scratch,
            ["PATH"] = OperatingSystem.IsWindows() ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32") : "/usr/bin:/bin:/usr/sbin:/sbin",
            ["LANG"] = "en_US.UTF-8"
        };
        // Add only explicitly read-granted installation bin directories, after system tools.
        var bins = profile.Folders.Where(g => g.Access == FolderAccess.ReadOnly)
            .SelectMany(g => new[] { SandboxProfile.CanonicalDirectory(g.Path), System.IO.Path.Combine(SandboxProfile.CanonicalDirectory(g.Path), "bin") }).Where(Directory.Exists);
        var additionalBins = bins.Where(path => !path.Contains(System.IO.Path.PathSeparator)).ToArray();
        if (additionalBins.Length != 0) env["PATH"] += System.IO.Path.PathSeparator + string.Join(System.IO.Path.PathSeparator, additionalBins);
        env["TERM"] = "xterm-256color";
        env["PS1"] = @"[MXC] \w \$ ";
        env["PROMPT_COMMAND"] = @"PS1='[MXC] \w \$ '";
        env["BASH_SILENCE_DEPRECATION_WARNING"] = "1";
        if (OperatingSystem.IsWindows())
        {
            env["PROMPT"] = "[MXC] $P$G";
            env["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            env["ComSpec"] = System.IO.Path.Combine(env["SystemRoot"], "System32", "cmd.exe");
            env["USERPROFILE"] = scratch;
            env["APPDATA"] = Directory.CreateDirectory(System.IO.Path.Combine(scratch, "AppData", "Roaming")).FullName;
            env["LOCALAPPDATA"] = Directory.CreateDirectory(System.IO.Path.Combine(scratch, "AppData", "Local")).FullName;
        }
        return new ContainerRequest(profile.Command)
        {
            Containment = OperatingSystem.IsMacOS() ? new Containment.Seatbelt { NestedPty = false } : OperatingSystem.IsLinux() ? new Containment.Bubblewrap() : new Containment.ProcessContainer(),
            Filesystem = fs, WorkingDirectory = profile.WorkingDirectory is null ? scratch : SandboxProfile.CanonicalDirectory(profile.WorkingDirectory), Environment = env, InheritDefaultEnvironment = false,
            TimeoutMs = checked((uint)profile.TimeoutSeconds * 1000), Ui = new UiPolicy { Disable = true },
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
                Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
                RuntimeConfig = proxyEndpoint is null ? null : new NetworkRuntimeConfig { NetworkProxy = proxyEndpoint }
            }
        };
    }
}
