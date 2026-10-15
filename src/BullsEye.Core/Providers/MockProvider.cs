using System.Runtime.CompilerServices;

namespace BullsEye.Core;

/// <summary>
/// 개발용 가짜 provider: 키 없이 UI 흐름을 확인한다. 같은 요청에는 항상 같은 결과.
/// 앱은 환경 변수 <c>BULLSEYE_MOCK=1</c>일 때만 이것을 쓴다.
/// </summary>
public sealed class MockProvider(TimeSpan chunkDelay) : IChatProvider
{
    public MockProvider() : this(TimeSpan.FromMilliseconds(40))
    {
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request.ReasoningEffort != ReasoningEffort.Off)
        {
            await Task.Delay(chunkDelay, cancellationToken);
            yield return new ReasoningDelta("Mock reasoning...");
        }

        var instruction = request.Messages[^1].Content.Split('\n')[^1];
        var code = $"% BullsEye mock response ({request.Model})\n% {instruction}\nresult = 42;";

        foreach (var line in code.Split('\n').Select((text, i) => i == 0 ? text : "\n" + text))
        {
            await Task.Delay(chunkDelay, cancellationToken);
            yield return new TextDelta(line);
        }

        yield return new Completed();
    }
}
