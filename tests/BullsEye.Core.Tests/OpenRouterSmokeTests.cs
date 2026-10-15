namespace BullsEye.Core.Tests;

/// <summary>
/// 실제 OpenRouter 호출 (과금됨). <c>mise run smoke</c>로만 실행하고,
/// <c>OPENROUTER_API_KEY</c>가 없으면 건너뛴다.
/// </summary>
[Trait("Category", "Smoke")]
public class OpenRouterSmokeTests
{
    private const string Model = "deepseek/deepseek-v4.1-flash";

    [SkippableFact]
    public async Task CompletesMatlabSelection_EndToEnd()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Skip.If(string.IsNullOrEmpty(apiKey), "OPENROUTER_API_KEY not set");

        var selection = "    area = ";
        var conversation = Conversation.Start(selection, "MATLAB");
        conversation.Ask("width w, height h 인 사각형 넓이로 완성해줘");
        var provider = new OpenRouterProvider(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, apiKey!);

        var events = new List<StreamEvent>();
        await foreach (var e in provider.StreamAsync(new ChatRequest(Model, conversation.BuildMessages(), true, ReasoningEffort.Medium)))
        {
            events.Add(e);
        }

        Assert.IsType<Completed>(events[^1]);
        Assert.DoesNotContain(events, e => e is StreamFailed);
        var raw = string.Concat(events.OfType<TextDelta>().Select(d => d.Text));
        var code = ResponseFormatter.Format(raw, SelectionShape.From(selection));
        Assert.StartsWith("    area", code);
        Assert.Matches(@"w\s*\*\s*h|h\s*\*\s*w", code);
    }
}
