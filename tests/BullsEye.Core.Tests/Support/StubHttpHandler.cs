using System.Net;
using System.Text;

namespace BullsEye.Core.Tests.Support;

/// <summary>네트워크 대신 미리 정한 응답을 돌려주고, 받은 요청을 기록한다.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public StubHttpHandler(params HttpResponseMessage[] responses)
        : this(Sequence(responses))
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return await respond(request, ct);
    }

    public static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    public static HttpResponseMessage Streaming(Stream body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(body) };

    public static HttpResponseMessage Status(int status, string body = "") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Sequence(HttpResponseMessage[] responses)
    {
        var index = 0;
        return (_, _) => Task.FromResult(responses[Math.Min(index++, responses.Length - 1)]);
    }
}
