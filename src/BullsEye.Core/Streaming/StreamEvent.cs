namespace BullsEye.Core;

/// <summary>스트리밍 응답에서 해석된 사건.</summary>
public abstract record StreamEvent;

public sealed record TextDelta(string Text) : StreamEvent;

public sealed record Completed : StreamEvent;

/// <summary>스트림 도중 provider가 보낸 에러. 이미 받은 텍스트는 유효하다.</summary>
public sealed record StreamFailed(string Message) : StreamEvent;
