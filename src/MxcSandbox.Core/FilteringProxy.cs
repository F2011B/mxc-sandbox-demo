using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MxcSandbox.Core;

/// <summary>A per-run HTTP/CONNECT proxy. It authorizes an exact destination before DNS,
/// validates the resolved address, then connects to that IP to avoid DNS rebinding.</summary>
public sealed class FilteringProxy : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<long, Task> clients = new();
    private readonly SemaphoreSlim slots = new(32);
    private readonly HashSet<Target> allowed;
    private readonly bool allowPrivate;
    private readonly Action<string>? log;
    private readonly Task accepting;
    private long sequence;
    public string Endpoint { get; }

    public FilteringProxy(ProxySettings settings, Action<string>? log = null)
    {
        if (!settings.Enabled) throw new ArgumentException("Proxy ist deaktiviert.");
        allowed = settings.AllowedTargets.Select(Target.Parse).ToHashSet();
        if (allowed.Count == 0) throw new ArgumentException("Leere Zielliste.");
        allowPrivate = settings.AllowPrivateAddresses;
        this.log = log;
        listener = new TcpListener(IPAddress.Loopback, settings.ListenPort);
        listener.Start(32);
        Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        accepting = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref sequence);
                var task = HandleAsync(client);
                clients[id] = task;
                _ = task.ContinueWith(_ => { clients.TryRemove(id, out var ignored); slots.Release(); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            var token = deadline.Token;
            var stream = client.GetStream();
            var established = false;
            try
            {
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var header = await ReadHeaderAsync(stream, headerTimeout.Token);
                var lines = header.Split("\r\n");
                var request = lines[0].Split(' ');
                if (request.Length != 3 || request[2] is not ("HTTP/1.0" or "HTTP/1.1")) throw new ProxyDenied("Ungültige Request-Zeile.");
                var headers = new List<(string Name, string Value)>();
                foreach (var line in lines.Skip(1).Where(x => x.Length != 0))
                {
                    var colon = line.IndexOf(':');
                    if (colon < 1 || line[..colon].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ProxyDenied("Ungültiger Header.");
                    headers.Add((line[..colon], line[(colon + 1)..].Trim()));
                }
                if (headers.Count(x => x.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) > 1 ||
                    headers.Any(x => x.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || x.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || x.Name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)))
                    throw new ProxyDenied("Request-Bodies und Upgrade werden nicht unterstützt.");
                var tunnel = request[0] == "CONNECT";
                Uri? uri = null;
                Target target;
                if (tunnel) target = Target.Parse(request[1]);
                else
                {
                    if (request[0] is not ("GET" or "HEAD") || !Uri.TryCreate(request[1], UriKind.Absolute, out uri) || uri.Scheme != "http" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                        throw new ProxyDenied("HTTP unterstützt GET/HEAD; HTTPS verwendet CONNECT.");
                    target = Target.Parse($"{(uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost)}:{uri.Port}");
                }
                if (!allowed.Contains(target)) throw new ProxyDenied($"Ziel nicht freigegeben: {target}");
                var addresses = IPAddress.TryParse(target.Host, out var address) ? [address] : await Dns.GetHostAddressesAsync(target.Host, token);
                var candidates = addresses.Where(ip => allowPrivate || IsPublic(ip)).ToArray();
                if (candidates.Length == 0) throw new ProxyDenied("Private, lokale oder reservierte Zieladresse gesperrt.");
                using var upstream = new TcpClient();
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                // Pin the checked address; never resolve the hostname again during connect.
                await upstream.ConnectAsync(candidates[0], target.Port, connectTimeout.Token);
                var remote = upstream.GetStream();
                log?.Invoke($"Proxy erlaubt: {target}");
                if (tunnel)
                {
                    await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
                    established = true;
                    var outbound = stream.CopyToAsync(remote, token);
                    var inbound = remote.CopyToAsync(stream, token);
                    await Task.WhenAny(outbound, inbound);
                    deadline.Cancel();
                    try { await Task.WhenAll(outbound, inbound); } catch (OperationCanceledException) { }
                }
                else
                {
                    var forwarded = new StringBuilder($"{request[0]} {uri!.PathAndQuery} HTTP/1.1\r\nHost: {target}\r\nConnection: close\r\n");
                    // Only forward ordinary end-to-end headers. No authentication or hop-by-hop data.
                    string[] safeHeaders = ["Accept", "Accept-Encoding", "Accept-Language", "User-Agent", "If-None-Match", "If-Modified-Since", "Range"];
                    foreach (var item in headers.Where(h => safeHeaders.Contains(h.Name, StringComparer.OrdinalIgnoreCase)))
                        forwarded.Append(item.Name).Append(": ").Append(item.Value).Append("\r\n");
                    forwarded.Append("\r\n");
                    await remote.WriteAsync(Encoding.ASCII.GetBytes(forwarded.ToString()), token);
                    established = true;
                    await remote.CopyToAsync(stream, token);
                }
            }
            catch (Exception ex) when (ex is ProxyDenied or ArgumentException or SocketException or IOException or OperationCanceledException)
            {
                if (!stop.IsCancellationRequested) log?.Invoke("Proxy verweigert/fehlgeschlagen: " + ex.Message);
                if (!established && !stop.IsCancellationRequested)
                {
                    try { await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), stop.Token); }
                    catch (Exception sendError) when (sendError is IOException or SocketException or OperationCanceledException) { }
                }
            }
        }
    }

    private static async Task<string> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[16_384];
        var size = 0;
        while (size < buffer.Length)
        {
            if (await stream.ReadAsync(buffer.AsMemory(size, 1), token) == 0) throw new ProxyDenied("Unvollständiger Request.");
            var b = buffer[size++];
            if ((b < 32 && b is not (9 or 10 or 13)) || b > 126) throw new ProxyDenied("Nicht-ASCII/Steuerzeichen im Header.");
            if (size >= 4 && buffer.AsSpan(size - 4, 4).SequenceEqual("\r\n\r\n"u8))
            {
                var result = Encoding.ASCII.GetString(buffer, 0, size);
                if (result.Replace("\r\n", "").IndexOfAny(['\r', '\n']) >= 0) throw new ProxyDenied("Ungültige Zeilenenden.");
                return result;
            }
        }
        throw new ProxyDenied("Header überschreitet 16 KiB.");
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublic(address.MapToIPv4());
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16)
        {
            // Global unicast only; exclude transition mechanisms and documentation ranges.
            return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x01 &&
                (bytes[2] == 0 || (bytes[2] == 0x0d && bytes[3] == 0xb8))) && !(bytes[0] == 0x20 && bytes[1] == 0x02);
        }
        var a = bytes[0]; var b = bytes[1]; var c = bytes[2];
        return !(a is 0 or 10 or 127 || a >= 224 || a == 100 && b is >= 64 and <= 127 ||
            a == 169 && b == 254 || a == 172 && b is >= 16 and <= 31 || a == 192 && (b == 168 || b == 0 || b == 2) ||
            a == 198 && (b is 18 or 19 || b == 51 && c == 100) || a == 203 && b == 0 && c == 113);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        await accepting;
        await Task.WhenAll(clients.Values.ToArray());
        stop.Dispose();
        // Continuations release slots asynchronously; leave the small semaphore for GC.
    }
    private sealed class ProxyDenied(string message) : Exception(message);
}
