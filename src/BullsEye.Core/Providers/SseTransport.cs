using System.Runtime.CompilerServices;
using System.Text;

namespace BullsEye.Core;

/// <summary>
/// SSE 스트리밍 요청의 공통 부분: 연결 제한 시간, 일시적 실패 1회 재시도, 첫 이벤트 제한 시간, 읽기 실패 변환.
/// 요청 형식과 이벤트 해석은 provider가 넘긴다.
/// </summary>
internal sealed class SseTransport(HttpClient http, OpenRouterOptions options)
{
    /// <param name="createRequest">시도마다 새 요청을 만든다 (HttpRequestMessage는 재사용할 수 없다).</param>
    /// <param name="mapError">성공이 아닌 응답(상태 코드, 본문)을 예외로.</param>
    /// <param name="feed">받은 텍스트 조각을 이벤트로 (provider별 decoder).</param>
    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Func<CancellationToken, Task<HttpRequestMessage>> createRequest,
        Func<int, string, ProviderException> mapError,
        Func<string, IReadOnlyList<StreamEvent>> feed,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(createRequest, mapError, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // 첫 이벤트(추론 포함)까지만 시간 제한. 단, 서버가 무엇이든 보내는 동안은 기다린다:
        // 추론을 글로 내보내지 않는 모델은 keep-alive 주석과 암호화된 reasoning_details만 보내며 수십 초 생각한다.
        using var firstEventTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receivedAny = false;

        var buffer = new char[4096];
        while (true)
        {
            if (!receivedAny) firstEventTimeout.CancelAfter(options.FirstEventTimeout);
            var read = await ReadChunkAsync(reader, buffer, receivedAny ? cancellationToken : firstEventTimeout.Token, cancellationToken);
            if (read == 0) break;

            foreach (var streamEvent in feed(new string(buffer, 0, read)))
            {
                receivedAny = true;
                yield return streamEvent;
            }
        }
    }

    // yield가 있는 메서드에는 catch를 둘 수 없어 읽기 실패 변환을 여기로 분리한다.
    private static async Task<int> ReadChunkAsync(
        StreamReader reader, char[] buffer, CancellationToken readToken, CancellationToken userToken)
    {
        try
        {
            return await reader.ReadAsync(buffer, readToken);
        }
        catch (OperationCanceledException ex) when (!userToken.IsCancellationRequested)
        {
            throw new ResponseTimeoutException(ex);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new NetworkException(ex);
        }
    }

    /// <summary>성공 응답을 돌려준다. 일시적 실패는 한 번만 재시도한다.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<CancellationToken, Task<HttpRequestMessage>> createRequest,
        Func<int, string, ProviderException> mapError,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await SendAsync(createRequest, cancellationToken);
            if (response.IsSuccessStatusCode) return response;

            ProviderException failure;
            using (response)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                failure = mapError((int)response.StatusCode, errorBody);
            }

            if (!failure.IsRetryable || attempt >= 2) throw failure;
            await Task.Delay(options.RetryDelay, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpRequestMessage>> createRequest, CancellationToken cancellationToken)
    {
        using var message = await createRequest(cancellationToken);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(options.ConnectTimeout);
        try
        {
            return await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ResponseTimeoutException(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkException(ex);
        }
    }
}
