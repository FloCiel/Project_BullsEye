namespace BullsEye.Core;

/// <summary>USB의 암호화 파일 하나에 저장되는 BullsEye 설정 전체.</summary>
public sealed record Settings
{
    /// <summary>요청을 보낼 AI 서비스.</summary>
    public ProviderKind Provider { get; init; } = ProviderKind.OpenRouter;

    /// <summary>OpenRouter API 키.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>OpenRouter 모델 ID. 첫 번째가 기본 모델, 팝업에서 Ctrl+Tab으로 순환.</summary>
    public IReadOnlyList<string> FavoriteModels { get; init; } = [];

    /// <summary>"Sign in with ChatGPT" 연결 정보. 한 번도 로그인하지 않았으면 null.</summary>
    public ChatGptAccount? ChatGpt { get; init; }

    /// <summary>ChatGPT 요금제로 쓸 모델(slug). 순서와 쓰임은 <see cref="FavoriteModels"/>와 같다.</summary>
    public IReadOnlyList<string> ChatGptModels { get; init; } = [];

    /// <summary>지금 고른 서비스의 즐겨찾기 모델.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ActiveModels => Provider == ProviderKind.ChatGpt ? ChatGptModels : FavoriteModels;

    public string Hotkey { get; init; } = "Ctrl+Alt+K";

    /// <summary>간소화 모드: 창 없이 선택 영역의 <c>&gt;&gt;</c> 지시 주석으로 바로 요청.</summary>
    public string QuickHotkey { get; init; } = "Ctrl+Alt+J";

    /// <summary>
    /// 간소화 모드 결과와 창 없는 에이전트가 끝난 파일에 지시 줄을 <c>&gt;&gt;</c> 그대로 남길지.
    /// 끄면(기본) <c>% 벡터화해줘</c> 같은 일반 주석이 된다. 켜면 그 줄을 고쳐서 영역째 다시 선택해 바로 재요청할 수 있다.
    /// </summary>
    public bool KeepInstructionMarker { get; init; }

    /// <summary>에이전트 창: 작업 폴더의 여러 파일을 검토 · 생성 · 수정.</summary>
    public string AgentHotkey { get; init; } = "Ctrl+Alt+L";

    /// <summary>마지막으로 에이전트를 돌린 폴더 (MATLAB 창 제목에서 폴더를 못 찾을 때 먼저 제안).</summary>
    public string? AgentFolder { get; init; }

    /// <summary>코드 언어. null이면 창 제목·프로그램으로 감지, 정하면 항상 그 언어로 요청한다.</summary>
    public string? CodeLanguage { get; init; }

    public int IdleTimeoutMinutes { get; init; } = 60;

    public int HistoryCapacity { get; init; } = 20;

    /// <summary>최근 지시문 (최신순). 코드는 저장하지 않는다.</summary>
    public IReadOnlyList<string> History { get; init; } = [];

    /// <summary>OpenRouter provider.data_collection = "deny".</summary>
    public bool DenyDataCollection { get; init; } = true;

    /// <summary>추론 등급. 느리고 비싸지만 정확도가 우선이라 기본 Medium (예전 "추론 켬"과 같은 동작).</summary>
    public ReasoningEffort ReasoningEffort { get; init; } = ReasoningEffort.Medium;

    /// <summary>Ctrl+C 시뮬레이션 후 클립보드 변화를 기다리는 시간.</summary>
    public int SelectionCaptureTimeoutMs { get; init; } = 300;
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ProviderKind>))]
public enum ProviderKind
{
    /// <summary>OpenRouter API 키 (크레딧 차감). 여러 회사의 모델.</summary>
    OpenRouter,

    /// <summary>ChatGPT Plus/Pro 요금제 (Sign in with ChatGPT). OpenAI 모델만.</summary>
    ChatGpt,
}
