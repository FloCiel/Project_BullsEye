using System.Text.Json;

namespace BullsEye.Core;

/// <summary>
/// 에이전트 실행 기록: 지시 · 도구 호출(이름과 경로, 성공/실패) · 끝 상태와 답.
/// 백업처럼 작업 폴더가 아니라 exe 옆의 <see cref="FolderName"/>에 둔다 (학교 PC에 흔적이 남지 않게).
/// 코드 본문(파일 내용, 바꾼 텍스트, 선택한 코드)은 적지 않는다. 기록 실패는 작업을 멈추지 않는다.
/// </summary>
public sealed class AgentLog
{
    /// <summary>exe 옆에 만드는 로그 폴더 이름.</summary>
    public const string FolderName = "BullsEye-log";

    private const int MaxErrorChars = 300;

    private readonly Func<DateTimeOffset> _now;

    /// <param name="logRoot">로그 파일을 만들 곳 (앱은 exe 옆의 <see cref="FolderName"/>).</param>
    public AgentLog(string workspaceRoot, string logRoot, DateTimeOffset startedAt, Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        var name = Path.GetFileName(workspaceRoot.TrimEnd('\\', '/'));
        var stamp = startedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        FilePath = Path.Combine(logRoot, (string.IsNullOrEmpty(name) ? stamp : $"{stamp} {name}") + ".txt");
        Write($"작업 폴더: {workspaceRoot}");
    }

    /// <summary>이 세션의 로그 파일 (세션 하나에 파일 하나, 이어 하는 지시도 같은 파일).</summary>
    public string FilePath { get; }

    /// <summary>지시를 적는다. 선택한 코드 블록(<c>```</c>부터)은 빼고 적는다.</summary>
    public void Instruction(string instruction, string model)
    {
        var text = instruction.ReplaceLineEndings("\n").Split('\n').TakeWhile(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal));
        Write($"── 지시 ({model})\n{string.Join("\n", text).TrimEnd()}");
    }

    public void Tool(ToolCall call, ToolResult result)
    {
        var path = PathOf(call.Arguments);
        var target = path is null ? call.Name : $"{call.Name} {path}";
        Write(result.IsError ? $"{target} → 실패: {Shorten(result.Content)}" : $"{target} → 성공");
    }

    public void Finished(AgentStatus status, string? reply, UserError? error)
    {
        var lines = new List<string> { $"── 끝: {status}" };
        if (error is not null) lines.Add($"오류: {error.Message}");
        if (!string.IsNullOrWhiteSpace(reply)) lines.Add(reply.Trim());
        Write(string.Join("\n", lines));
    }

    private void Write(string entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var stamped = $"[{_now().ToLocalTime():HH:mm:ss}] {entry}".ReplaceLineEndings(Environment.NewLine);
            File.AppendAllText(FilePath, stamped + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // USB가 빠졌거나 읽기 전용이면 기록만 건너뛴다.
        }
    }

    private static string? PathOf(string arguments)
    {
        try
        {
            using var json = JsonDocument.Parse(arguments);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.String
                ? path.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxErrorChars ? line : line[..MaxErrorChars] + "…";
    }
}
