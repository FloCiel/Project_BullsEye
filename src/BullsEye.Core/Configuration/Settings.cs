namespace BullsEye.Core;

/// <summary>USB의 암호화 파일 하나에 저장되는 BullsEye 설정 전체.</summary>
public sealed record Settings
{
    public string ApiKey { get; init; } = "";

    /// <summary>OpenRouter 모델 ID. 첫 번째가 기본 모델, 팝업에서 Ctrl+Tab으로 순환.</summary>
    public IReadOnlyList<string> FavoriteModels { get; init; } = [];

    public string Hotkey { get; init; } = "Ctrl+Alt+K";

    /// <summary>간소화 모드: 창 없이 선택 영역의 <c>&gt;&gt;</c> 지시 주석으로 바로 요청.</summary>
    public string QuickHotkey { get; init; } = "Ctrl+Alt+J";

    public int IdleTimeoutMinutes { get; init; } = 60;

    public int HistoryCapacity { get; init; } = 20;

    /// <summary>최근 지시문 (최신순). 코드는 저장하지 않는다.</summary>
    public IReadOnlyList<string> History { get; init; } = [];

    /// <summary>OpenRouter provider.data_collection = "deny".</summary>
    public bool DenyDataCollection { get; init; } = true;

    /// <summary>추론 모델의 생각 단계를 켠다. 느리고 비싸지만 정확도가 우선이라 기본 켬.</summary>
    public bool EnableReasoning { get; init; } = true;

    /// <summary>Ctrl+C 시뮬레이션 후 클립보드 변화를 기다리는 시간.</summary>
    public int SelectionCaptureTimeoutMs { get; init; } = 300;
}
