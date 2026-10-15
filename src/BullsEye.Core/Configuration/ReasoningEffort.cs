using System.Text.Json.Serialization;

namespace BullsEye.Core;

/// <summary>추론 모델이 답하기 전에 얼마나 생각할지. OpenRouter <c>reasoning.effort</c>로 보낸다.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReasoningEffort>))]
public enum ReasoningEffort
{
    /// <summary><c>reasoning.enabled = false</c>. 추론을 끌 수 없는 모델은 그래도 생각한다.</summary>
    Off,
    Low,
    Medium,
    High,
}
