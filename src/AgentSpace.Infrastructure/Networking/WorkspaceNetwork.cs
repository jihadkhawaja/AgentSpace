using System.Net;
using System.Net.Sockets;

namespace AgentSpace;

internal static class WorkspaceNetwork
{
    public static async Task<NetworkResponse> SendAsync(Access access, NetworkRequest request, int remaining,
        Action? charge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Method);
        if (request.Method.Length is < 3 or > 6) throw new ArgumentException("Unsupported workspace HTTP method.", nameof(request));
        var method = request.Method.ToUpperInvariant();
        if (method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE") ||
            request.Body is not null && method is "GET" or "HEAD")
            throw new ArgumentException("Unsupported workspace HTTP request.", nameof(request));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(access.Limit(l => l.MaxRunTime, TimeSpan.FromMinutes(2)));
        var token = deadline.Token;
        var maximum = Math.Min(remaining, access.Limit(l => l.MaxNetworkBytes, 4 * 1024 * 1024));
        if (request.Body is { } body && WorkspaceFiles.Utf8.GetByteCount(body) > maximum)
            throw new WorkspaceLimitException("Network request body size limit reached.");

        var uri = request.Uri;
        for (var hop = 0; ; hop++)
        {
            token.ThrowIfCancellationRequested();
            ValidateUri(uri);
            Access.RequireNetwork(access.Current(), uri, method);
            charge?.Invoke();
            var target = uri;
            IPAddress? connectedAddress = null;
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                MaxResponseHeadersLength = 32,
                ConnectCallback = (context, ct) => ConnectAsync(context, access, target, method, address => connectedAddress = address, ct)
            };
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var outgoing = new HttpRequestMessage(new HttpMethod(method), uri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = request.Body is { } payload ? new StringContent(payload, WorkspaceFiles.Utf8, "text/plain") : null
            };

            HttpResponseMessage response;
            try { response = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
            catch (HttpRequestException error) when (GetDenial(error) is not null)
            {
                throw new WorkspaceAccessException("Network destination denied by workspace policy.");
            }
            using (response)
            {
                Access.RequireNetwork(access.Current(), target, method, connectedAddress);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    if (hop >= 3 || method is not ("GET" or "HEAD"))
                        throw new WorkspaceAccessException("HTTP redirect exceeds the workspace redirect policy.");
                    var next = new Uri(uri, location);
                    if (uri.Scheme == "https" && next.Scheme != "https")
                        throw new WorkspaceAccessException("HTTPS requests cannot redirect to an insecure destination.");
                    uri = next;
                    continue;
                }

                maximum = Math.Min(maximum, access.Limit(l => l.MaxNetworkBytes, 4 * 1024 * 1024));
                if (method == "HEAD") return new NetworkResponse((int)response.StatusCode, []);
                if (response.Content.Headers.ContentLength > maximum)
                    throw new WorkspaceLimitException("Network response body size limit reached.");
                using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var result = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    Access.RequireNetwork(access.Current(), target, method, connectedAddress);
                    maximum = Math.Min(maximum, access.Limit(l => l.MaxNetworkBytes, 4 * 1024 * 1024));
                    if (result.Length + count > maximum) throw new WorkspaceLimitException("Network response body size limit reached.");
                    result.Write(buffer, 0, count);
                }
                return new NetworkResponse((int)response.StatusCode, result.ToArray());
            }
        }
    }

    private static WorkspaceAccessException? GetDenial(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is WorkspaceAccessException denied) return denied;
        return null;
    }

    private static void ValidateUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.OriginalString.Length > 4096 || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.IdnHost.Length == 0 || uri.IdnHost.Contains('%') ||
            uri.AbsolutePath.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '-' or '_' or '.' or '~')) ||
            uri.AbsolutePath.Contains("//", StringComparison.Ordinal))
            throw new WorkspaceAccessException("Network requests require a canonical HTTP(S) URL.");
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, Access access, Uri uri,
        string method, Action<IPAddress> connected, CancellationToken cancellationToken)
    {
        if (context.DnsEndPoint.Port != uri.Port ||
            !context.DnsEndPoint.Host.TrimEnd('.').Trim('[', ']').Equals(uri.IdnHost.TrimEnd('.').Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
            throw new WorkspaceAccessException("Unexpected HTTP connection endpoint.");

        var host = uri.IdnHost.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        SocketException? lastError = null;
        foreach (var candidate in addresses.Take(32))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate;
            try { Access.RequireNetwork(access.Current(), uri, method, address); }
            catch (WorkspaceAccessException) { continue; }

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, uri.Port), cancellationToken).ConfigureAwait(false);
                Access.RequireNetwork(access.Current(), uri, method, address);
                connected(address);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException error) { socket.Dispose(); lastError = error; }
            catch { socket.Dispose(); throw; }
        }
        if (lastError is not null) throw lastError;
        throw new WorkspaceAccessException("No resolved address is permitted by workspace policy.");
    }
}
