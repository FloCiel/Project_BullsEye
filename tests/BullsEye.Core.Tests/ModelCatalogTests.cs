using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class ModelCatalogTests
{
    private const string ModelsJson =
        """
        {"data":[
          {"id":"deepseek/deepseek-v4.1-flash","name":"DeepSeek: DeepSeek V4.1 Flash","context_length":1048576,
           "pricing":{"prompt":"0.0000003","completion":"0.0000012"}},
          {"id":"vendor/no-pricing","name":"No Pricing"}
        ]}
        """;

    [Fact]
    public async Task FetchAsync_ParsesModelsWithPricePerMillionTokens()
    {
        var handler = new StubHttpHandler(StubHttpHandler.Status(200, ModelsJson));
        var catalog = new ModelCatalog(new HttpClient(handler));

        var models = await catalog.FetchAsync();

        Assert.Equal("https://openrouter.ai/api/v1/models", Assert.Single(handler.Requests).Request.RequestUri!.ToString());
        Assert.Equal(
            [
                new ModelInfo("deepseek/deepseek-v4.1-flash", "DeepSeek: DeepSeek V4.1 Flash", 0.3m, 1.2m),
                new ModelInfo("vendor/no-pricing", "No Pricing", null, null),
            ],
            models);
    }

    [Fact]
    public async Task FetchAsync_Failure_ThrowsProviderException()
    {
        var catalog = new ModelCatalog(new HttpClient(new StubHttpHandler(StubHttpHandler.Status(503))));

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => catalog.FetchAsync());
    }
}
