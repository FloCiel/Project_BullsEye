using System.Net.Http.Headers;
using System.Text.Json;

namespace BullsEye.Core;

/// <summary>로그인한 ChatGPT 계정이 쓸 수 있는 모델 목록 (<c>GET /v1/models</c>, 서버 순서 유지).</summary>
public sealed class ChatGptModelCatalog(HttpClient http)
{
    public const string Endpoint = "https://api.openai.com/v1/models";

    public async Task<IReadOnlyList<ModelInfo>> FetchAsync(ChatGptTokenSource tokens, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(cancellationToken));

        string body;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw ResponsesProvider.MapError((int)response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkException(ex);
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("models").EnumerateArray()
            .Where(m => Read(m, "visibility") == "list" && Read(m, "slug") is not null)
            .Select(m => new ModelInfo(Read(m, "slug")!, Read(m, "display_name") ?? Read(m, "slug")!, null, null))
            .ToList();
    }

    private static string? Read(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
