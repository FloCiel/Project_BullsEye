using System.Text;

namespace BullsEye.Core;

public enum AgentStatus
{
    Ready,

    /// <summary>모델 응답을 기다리거나 도구를 실행하는 중.</summary>
    Working,

    /// <summary>모델이 도구 호출 없이 답하고 끝냄.</summary>
    Done,

    Failed,
    Cancelled,
}

/// <param name="MaxSteps">작업 한 번에 모델에게 보낼 요청 수 상한 (무한 반복·과금 방지).</param>
/// <param name="Language">작업 폴더의 코드 언어 (설정에서 고정하지 않았으면 MATLAB).</param>
public sealed record AgentOptions(
    string Model, bool DenyDataCollection, ReasoningEffort ReasoningEffort, int MaxSteps = 30, string Language = "MATLAB");

/// <summary>에이전트 창에 보여줄 진행 상황.</summary>
public abstract record AgentEvent;

public sealed record AgentStatusChanged(AgentStatus Status, UserError? Error) : AgentEvent;

public sealed record AgentText(string Text) : AgentEvent;

/// <summary>추론 중이라는 신호 (내용은 보여주지 않아도 된다).</summary>
public sealed record AgentThinking : AgentEvent;

public sealed record AgentToolStarted(ToolCall Call) : AgentEvent;

public sealed record AgentToolFinished(ToolCall Call, ToolResult Result) : AgentEvent;

/// <summary>
/// 작업 폴더 하나에서 도는 pi 방식의 에이전트: 모델이 도구를 부르면 실행해 결과를 돌려주고,
/// 도구 없이 답할 때까지 반복한다. 대화는 세션 동안 이어진다 (후속 지시 가능).
/// </summary>
public sealed class AgentSession
{
    public const string StepLimitMessage = "도구 호출 한도에 도달해 멈췄습니다. 이어서 하려면 빈 지시로 다시 실행하세요.";

    private const string SystemPromptTemplate =
        """
        You are a coding agent working on {1} code in the user's folder: {0}
        Write {1} code only, even if a file or comment looks like another language.
        You can only touch files inside this folder, using the tools.

        Guidelines:
        - Read files before changing them. Use list_files to find the relevant functions.
        - Use edit_file for small changes and write_file for new files or full rewrites.
        - Finish the whole task yourself. If it needs new functions or helper files, create them with write_file;
          never tell the user to create files you can create. Each change is shown to the user for approval anyway.
          Do not create files the task does not need.
        - Keep the existing style, naming and comment language. Do not change code you were not asked to change.
        - In MATLAB and Octave, a function file's name must match its function name. When adding new functions, put each
          reusable one in its own file (name.m) unless the user asks for local functions in the same file.
        - Renaming a function means changing its name (and its callers) in place. Do not create a new file, move the code
          or leave a compatibility wrapper, unless the user asks for a new function file. If the new name no longer matches
          the file name, say in the summary that the user should rename the file to match.
        - When reviewing, point out bugs and risky code with file names and line contents; change files only if asked.
        - When finished, reply in Korean with a short summary of what you did and anything the user should check.
        """;

    private readonly IChatProvider _provider;
    private readonly AgentTools _tools;
    private readonly AgentOptions _options;
    private readonly List<ChatMessage> _messages;
    private readonly AgentLog? _log;
    private CancellationTokenSource? _running;

    public AgentSession(
        IChatProvider provider, AgentWorkspace workspace, IChangeApprover approver, AgentOptions options, AgentBackup? backup = null, AgentLog? log = null)
    {
        _provider = provider;
        _tools = new AgentTools(workspace, approver, backup);
        _options = options;
        _log = log;
        Model = options.Model;
        _messages = [new ChatMessage(ChatRole.System, string.Format(SystemPromptTemplate, workspace.Root, options.Language))];
    }

    public event EventHandler<AgentEvent>? Progress;

    public AgentStatus Status { get; private set; } = AgentStatus.Ready;

    public UserError? Error { get; private set; }

    public bool IsBusy => Status == AgentStatus.Working;

    /// <summary>다음 요청부터 쓸 모델. 작업 사이에 창에서 바꿀 수 있다.</summary>
    public string Model { get; set; }

    /// <summary>지금까지의 대화 (시스템 프롬프트 포함). 테스트·디버그용.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>
    /// 지시를 보내고 끝날 때까지 돈다. 빈 지시는 멈춘 곳(실패·취소·한도)에서 이어 하기.
    /// 실패는 상태로 바꾸고 던지지 않는다.
    /// </summary>
    public async Task RunAsync(string instruction, CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(instruction))
        {
            if (_messages.Count == 1) return;
        }
        else
        {
            _messages.Add(new ChatMessage(ChatRole.User, instruction));
        }

        _log?.Instruction(string.IsNullOrWhiteSpace(instruction) ? "(이어 하기)" : instruction, Model);

        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        SetStatus(AgentStatus.Working);

        try
        {
            for (var step = 0; step < _options.MaxSteps; step++)
            {
                var reply = await RequestAsync(step, running.Token);
                if (reply is null) return; // 스트림 오류: 상태는 이미 Failed.

                _messages.Add(reply);
                if (reply.ToolCalls is not { Count: > 0 } calls)
                {
                    SetStatus(AgentStatus.Done);
                    return;
                }

                foreach (var call in calls)
                {
                    Raise(new AgentToolStarted(call));
                    var result = await _tools.ExecuteAsync(call, running.Token);
                    _messages.Add(new ChatMessage(ChatRole.Tool, result.Content, ToolCallId: call.Id));
                    _log?.Tool(call, result);
                    Raise(new AgentToolFinished(call, result));
                }
            }

            SetStatus(AgentStatus.Failed, new UserError(StepLimitMessage, ErrorAction.Retry));
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            CloseDanglingToolCalls("Cancelled by the user.");
            SetStatus(AgentStatus.Cancelled);
        }
        catch (Exception ex)
        {
            CloseDanglingToolCalls("Not executed: the run stopped with an error.");
            SetStatus(AgentStatus.Failed, ErrorMessages.For(ex));
        }
        finally
        {
            _running = null;
            var reply = Status == AgentStatus.Done ? _messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Content : null;
            _log?.Finished(Status, reply, Error);
        }
    }

    /// <summary>진행 중인 작업을 멈춘다. 이미 쓴 파일은 그대로 남는다.</summary>
    public void Cancel() => _running?.Cancel();

    /// <summary>요청 하나를 스트리밍으로 받아 assistant 메시지로 만든다. 스트림 오류면 null.</summary>
    private async Task<ChatMessage?> RequestAsync(int step, CancellationToken cancellationToken)
    {
        var request = new ChatRequest(
            Model, [.. _messages], _options.DenyDataCollection, _options.ReasoningEffort, AgentTools.Definitions);
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Arguments)>();

        await foreach (var streamEvent in _provider.StreamAsync(request, cancellationToken))
        {
            switch (streamEvent)
            {
                case TextDelta delta:
                    text.Append(delta.Text);
                    Raise(new AgentText(delta.Text));
                    break;
                case ReasoningDelta:
                    Raise(new AgentThinking());
                    break;
                case ToolCallDelta delta:
                    var call = calls.TryGetValue(delta.Index, out var existing) ? existing : (null, null, new StringBuilder());
                    call.Id ??= delta.Id;
                    call.Name ??= delta.Name;
                    call.Arguments.Append(delta.Arguments);
                    calls[delta.Index] = call;
                    break;
                case StreamFailed failed:
                    SetStatus(AgentStatus.Failed, failed.Error ?? ErrorMessages.ForStreamFailure(failed.Message));
                    return null;
            }
        }

        // id를 빠뜨리는 provider가 있어 만들어 채운다 (tool 메시지가 짝을 찾을 수 있게).
        var toolCalls = calls
            .Select(c => new ToolCall(c.Value.Id ?? $"call_{step}_{c.Key}", c.Value.Name ?? "", c.Value.Arguments.ToString()))
            .ToList();
        return new ChatMessage(ChatRole.Assistant, text.ToString(), toolCalls.Count > 0 ? toolCalls : null);
    }

    /// <summary>
    /// 도구 실행 중에 멈추면 답이 없는 호출이 남는다. API는 모든 호출에 tool 메시지를 요구하므로
    /// 채워 둬야 다음 지시(이어 하기)가 거절되지 않는다.
    /// </summary>
    private void CloseDanglingToolCalls(string content)
    {
        var lastAssistant = _messages.FindLastIndex(m => m.Role == ChatRole.Assistant);
        if (lastAssistant < 0 || _messages[lastAssistant].ToolCalls is not { } calls) return;

        var answered = _messages.Skip(lastAssistant + 1).Select(m => m.ToolCallId).ToHashSet();
        foreach (var call in calls.Where(c => !answered.Contains(c.Id)))
        {
            _messages.Add(new ChatMessage(ChatRole.Tool, content, ToolCallId: call.Id));
        }
    }

    private void SetStatus(AgentStatus status, UserError? error = null)
    {
        Status = status;
        Error = error;
        Raise(new AgentStatusChanged(status, error));
    }

    private void Raise(AgentEvent agentEvent) => Progress?.Invoke(this, agentEvent);
}
