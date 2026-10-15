namespace BullsEye.Core;

/// <summary>스트리밍 응답에서 해석된 사건.</summary>
public abstract record StreamEvent;

public sealed record TextDelta(string Text) : StreamEvent;

/// <summary>추론 모델의 생각 과정. 클립보드에는 넣지 않고, 응답이 살아 있다는 신호로 쓴다.</summary>
public sealed record ReasoningDelta(string Text) : StreamEvent;

/// <summary>
/// 도구 호출 조각. 같은 <paramref name="Index"/>의 조각을 이어 붙여 호출 하나가 된다:
/// id와 이름은 보통 첫 조각에만 오고, 인자 JSON은 여러 조각으로 나뉘어 온다.
/// </summary>
public sealed record ToolCallDelta(int Index, string? Id, string? Name, string Arguments) : StreamEvent;

public sealed record Completed : StreamEvent;

/// <summary>스트림 도중 provider가 보낸 에러. 이미 받은 텍스트는 유효하다.</summary>
public sealed record StreamFailed(string Message) : StreamEvent;
