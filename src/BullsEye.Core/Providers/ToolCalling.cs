namespace BullsEye.Core;

/// <summary>모델에게 알려주는 도구 하나 (OpenAI 호환 function tool).</summary>
/// <param name="ParametersSchema">인자의 JSON Schema (JSON 문자열).</param>
public sealed record ToolDefinition(string Name, string Description, string ParametersSchema);

/// <summary>모델이 요청한 도구 호출. 인자는 모델이 만든 JSON 문자열 그대로라 깨져 있을 수 있다.</summary>
public sealed record ToolCall(string Id, string Name, string Arguments);
