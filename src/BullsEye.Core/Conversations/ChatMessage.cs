namespace BullsEye.Core;

public enum ChatRole
{
    System,
    User,
    Assistant,

    /// <summary>도구 실행 결과 (에이전트 모드).</summary>
    Tool,
}

/// <param name="ToolCalls">Assistant가 요청한 도구 호출 (에이전트 모드).</param>
/// <param name="ToolCallId">Tool 메시지가 답하는 호출의 id.</param>
public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null);
