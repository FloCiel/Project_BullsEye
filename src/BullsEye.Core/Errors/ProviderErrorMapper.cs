using System.Text.Json;

namespace BullsEye.Core;

/// <summary>provider의 HTTP 실패 응답을 <see cref="ProviderException"/>으로 바꾼다.</summary>
public static class ProviderErrorMapper
{
    public static ProviderException Map(int status, string body) =>
        FromStatus(status, TryReadErrorMessage(body) ?? $"HTTP {status}");

    /// <summary>본문 해석은 provider가 끝냈고 상태 코드로만 나눌 때.</summary>
    public static ProviderException FromStatus(int status, string message) =>
        status switch
        {
            401 => new AuthException(message),
            402 => new CreditException(message),
            429 => new RateLimitException(message),
            >= 500 and < 600 => new ProviderUnavailableException(message, isRetryable: status is 502 or 503 or 529),
            _ => new ProviderException(message),
        };

    // OpenRouter: {"error":{"code":402,"message":"..."}}. 그 외 형태는 null.
    private static string? TryReadErrorMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // HTML 에러 페이지 등 JSON이 아닌 본문
        }

        return null;
    }
}
