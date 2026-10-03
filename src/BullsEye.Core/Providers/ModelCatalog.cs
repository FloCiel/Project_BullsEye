using System.Globalization;
using System.Text.Json;

namespace BullsEye.Core;

/// <summary>모델 목록의 한 항목. 가격은 100만 토큰당 달러 (모르면 null).</summary>
public sealed record ModelInfo(string Id, string Name, decimal? PromptPricePerMillion, decimal? CompletionPricePerMillion);

/// <summary>설정 창의 즐겨찾기 선택용 OpenRouter 모델 목록. 키 없이 조회된다.</summary>
public sealed class ModelCatalog(HttpClient http)
{
    private const string Endpoint = "https://openrouter.ai/api/v1/models";

    public async Task<IReadOnlyList<ModelInfo>> FetchAsync(CancellationToken cancellationToken = default)
    {
        string body;
        try
        {
            using var response = await http.GetAsync(Endpoint, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw ProviderErrorMapper.Map((int)response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkException(ex);
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(ParseModel).ToList();
    }

    private static ModelInfo ParseModel(JsonElement model)
    {
        decimal? prompt = null, completion = null;
        if (model.TryGetProperty("pricing", out var pricing))
        {
            prompt = PerMillion(pricing, "prompt");
            completion = PerMillion(pricing, "completion");
        }

        return new ModelInfo(
            model.GetProperty("id").GetString()!,
            model.TryGetProperty("name", out var name) ? name.GetString()! : model.GetProperty("id").GetString()!,
            prompt,
            completion);
    }

    private static decimal? PerMillion(JsonElement pricing, string property) =>
        pricing.TryGetProperty(property, out var value)
        && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var perToken)
            ? perToken * 1_000_000m
            : null;
}
