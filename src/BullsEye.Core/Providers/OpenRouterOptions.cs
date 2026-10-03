namespace BullsEye.Core;

public sealed record OpenRouterOptions
{
    /// <summary>429/502/503/529 후 한 번 재시도하기 전 대기 시간.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>요청을 보내고 응답 헤더를 받기까지의 제한 (시도마다).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>헤더 이후 첫 이벤트(추론 포함)까지의 제한. 그 뒤로는 제한 없음 (Esc로 취소).</summary>
    public TimeSpan FirstEventTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
