using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BullsEye.Core;

/// <summary>
/// OAuth 리디렉션을 받는 <c>http://127.0.0.1:{port}/auth/callback</c> 수신기.
/// HttpListener(http.sys)는 127.0.0.1에 관리자 권한을 요구할 수 있어, TCP로 요청 줄만 직접 읽는다.
/// </summary>
public sealed class LoopbackCallback : IDisposable
{
    public const int PreferredPort = 1455;
    private const string CallbackPath = "/auth/callback";
    private const int MaxRequestBytes = 16 * 1024;

    private readonly TcpListener _listener;

    private LoopbackCallback(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>OpenAI 규칙: 호스트는 127.0.0.1(localhost 아님), 경로는 고정, 포트만 바뀔 수 있다.</summary>
    public string RedirectUri => $"http://127.0.0.1:{Port}{CallbackPath}";

    /// <summary>1455를 먼저 쓰고, 다른 프로그램(Codex 등)이 쓰고 있으면 빈 포트를 쓴다.</summary>
    public static LoopbackCallback Start(int preferredPort = PreferredPort)
    {
        foreach (var port in new[] { preferredPort, 0 })
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return new LoopbackCallback(listener);
            }
            catch (SocketException) when (port != 0)
            {
                listener.Dispose();
            }
        }

        throw new InvalidOperationException("unreachable");
    }

    /// <summary>콜백 경로로 온 첫 요청의 쿼리를 돌려준다. 다른 경로(favicon 등)는 404로 넘긴다.</summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            var target = await ReadRequestTargetAsync(stream, cancellationToken);

            var query = target is null ? null : ParseTarget(target);
            if (query is null)
            {
                await RespondAsync(stream, "404 Not Found", "", cancellationToken);
                continue;
            }

            var page = query.ContainsKey("error")
                ? "로그인이 취소되었습니다. 이 탭을 닫고 BullsEye로 돌아가세요."
                : "BullsEye가 로그인 응답을 받았습니다. 이 탭을 닫고 BullsEye로 돌아가세요.";
            await RespondAsync(stream, "200 OK", page, cancellationToken);
            return query;
        }
    }

    public void Dispose() => _listener.Dispose();

    /// <summary><c>/auth/callback?…</c>면 쿼리, 아니면 null.</summary>
    internal static Dictionary<string, string>? ParseTarget(string target)
    {
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        if (path != CallbackPath) return null;

        var query = new Dictionary<string, string>();
        if (question < 0) return query;

        foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = Decode(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? "" : Decode(pair[(equals + 1)..]);
            query.TryAdd(name, value);
        }

        return query;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    /// <summary><c>GET /auth/callback?… HTTP/1.1</c>의 가운데 부분. GET이 아니면 null.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
        }

        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var firstLine = head.Split("\r\n", 2)[0].Split(' ');
        return firstLine.Length == 3 && firstLine[0] == "GET" ? firstLine[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string message, CancellationToken cancellationToken)
    {
        var html = $"<!doctype html><meta charset=\"utf-8\"><title>BullsEye</title><p style=\"font:16px system-ui,sans-serif;margin:2em\">{WebUtility.HtmlEncode(message)}</p>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}
