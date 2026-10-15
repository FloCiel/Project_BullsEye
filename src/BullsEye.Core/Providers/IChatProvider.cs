namespace BullsEye.Core;

/// <summary>대화를 보내고 응답을 스트리밍으로 받는 AI provider.</summary>
public interface IChatProvider
{
    /// <summary>
    /// 실패는 <see cref="BullsEyeException"/> 하위 타입으로 던진다. 취소는 <see cref="OperationCanceledException"/> 그대로.
    /// </summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}

public sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    bool DenyDataCollection,
    ReasoningEffort ReasoningEffort);
