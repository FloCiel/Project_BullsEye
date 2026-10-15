using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BullsEye.Core;

/// <summary>
/// 개발용 가짜 provider: 키 없이 UI 흐름을 확인한다. 같은 요청에는 항상 같은 결과.
/// 앱은 환경 변수 <c>BULLSEYE_MOCK=1</c>일 때만 이것을 쓴다.
/// 도구가 붙은 요청(에이전트)에는 정해진 순서로 도구를 부른다: 목록 → 첫 .m 파일 읽기 → 두 번째 줄에 주석 추가 → 요약.
/// .m 파일이 없으면 새 파일을 만든다.
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

        if (request.Tools is { Count: > 0 })
        {
            foreach (var step in AgentStep(request.Messages))
            {
                await Task.Delay(chunkDelay, cancellationToken);
                yield return step;
            }

            yield return new Completed();
            yield break;
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

    public const string MockFileName = "bullseye_mock.m";
    public const string MockComment = "% BullsEye mock: 검토함";

    /// <summary>마지막 메시지(사용자 지시 또는 도구 결과)를 보고 다음 한 걸음을 정한다.</summary>
    private static IEnumerable<StreamEvent> AgentStep(IReadOnlyList<ChatMessage> messages)
    {
        var last = messages[^1];
        var id = $"mock_{messages.Count}";
        if (last.Role != ChatRole.Tool)
        {
            return [new TextDelta("폴더를 살펴볼게요."), Call(id, "list_files", new { pattern = "*.m" })];
        }

        var calledTool = messages
            .Where(m => m.ToolCalls is not null)
            .SelectMany(m => m.ToolCalls!)
            .LastOrDefault(c => c.Id == last.ToolCallId)?.Name;
        var firstLine = last.Content.Split('\n')[0];

        if (calledTool == "list_files")
        {
            return firstLine.EndsWith(".m", StringComparison.OrdinalIgnoreCase)
                ? [new TextDelta($"{firstLine}을 읽어볼게요."), Call(id, "read_file", new { path = firstLine })]
                : [
                    new TextDelta(".m 파일이 없어 새로 만들게요."),
                    Call(id, "write_file", new { path = MockFileName, content = $"function y = bullseye_mock(x)\n{MockComment}\ny = 2 * x;\nend\n" }),
                ];
        }

        if (calledTool == "read_file" && firstLine.Length > 0 && !last.Content.StartsWith("Error:", StringComparison.Ordinal))
        {
            var path = ReadPath(messages, last.ToolCallId);
            return
            [
                new TextDelta("첫 줄 아래에 주석을 넣을게요."),
                Call(id, "edit_file", new { path, old_text = firstLine, new_text = firstLine + "\n" + MockComment }),
            ];
        }

        return [new TextDelta($"mock 작업을 마쳤습니다.\n마지막 도구 결과: {firstLine}")];
    }

    private static ToolCallDelta Call(string id, string name, object arguments) =>
        new(0, id, name, JsonSerializer.Serialize(arguments));

    private static string ReadPath(IReadOnlyList<ChatMessage> messages, string? callId)
    {
        var call = messages.Where(m => m.ToolCalls is not null).SelectMany(m => m.ToolCalls!).Last(c => c.Id == callId);
        using var args = JsonDocument.Parse(call.Arguments);
        return args.RootElement.GetProperty("path").GetString()!;
    }
}
