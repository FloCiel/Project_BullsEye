using System.Text;

namespace BullsEye.Core;

public enum SessionStatus
{
    /// <summary>지시 입력 대기.</summary>
    Ready,

    /// <summary>요청을 보냈고 코드가 아직 오지 않음 (추론 중 포함).</summary>
    Thinking,

    /// <summary>코드가 스트리밍되는 중.</summary>
    Writing,

    /// <summary>결과가 클립보드에 들어감.</summary>
    Done,

    Failed,
    Cancelled,
}

public sealed record SessionOptions(IReadOnlyList<string> Models, bool DenyDataCollection, bool EnableReasoning);

/// <summary>
/// 팝업 하나의 수명. 지시 → 스트리밍 → 정리 → 클립보드 → 히스토리.
/// UI 스레드에서 쓴다고 가정한다 (클립보드 접근).
/// </summary>
public sealed class CompletionSession
{
    private readonly IChatProvider _provider;
    private readonly IClipboardService _clipboard;
    private readonly PromptHistory _history;
    private readonly SessionOptions _options;
    private readonly SelectionShape _shape;
    private readonly Conversation _conversation;
    private int _modelIndex;
    private bool _hasAsked;
    private CancellationTokenSource? _running;

    public CompletionSession(
        IChatProvider provider,
        IClipboardService clipboard,
        PromptHistory history,
        SessionOptions options,
        string? selection,
        string? language)
    {
        _provider = provider;
        _clipboard = clipboard;
        _history = history;
        _options = options;
        _shape = string.IsNullOrEmpty(selection) ? SelectionShape.None : SelectionShape.From(selection);
        _conversation = Conversation.Start(selection, language);
    }

    /// <summary>상태나 출력이 바뀔 때마다 (UI 갱신용).</summary>
    public event EventHandler? Changed;

    public SessionStatus Status { get; private set; } = SessionStatus.Ready;

    /// <summary>스트리밍 중에는 받은 그대로, 완료 후에는 정리된 코드.</summary>
    public string Output { get; private set; } = "";

    public UserError? Error { get; private set; }

    public string Model => _options.Models[_modelIndex];

    /// <summary>즐겨찾기 모델을 순환한다 (팝업의 Ctrl+Tab).</summary>
    public void CycleModel()
    {
        _modelIndex = (_modelIndex + 1) % _options.Models.Count;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsBusy => Status is SessionStatus.Thinking or SessionStatus.Writing;

    /// <summary>
    /// 지시를 보낸다. 빈 지시는 마지막 지시로 다시 생성 (첫 지시가 비었으면 무시).
    /// 응답을 받는 중이면 무시한다.
    /// </summary>
    public async Task SubmitAsync(string instruction, CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(instruction) && !_hasAsked) return;

        _conversation.Ask(instruction);
        _hasAsked = true;
        _history.Add(instruction);

        var request = new ChatRequest(Model, _conversation.BuildMessages(), _options.DenyDataCollection, _options.EnableReasoning);
        var raw = new StringBuilder();
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        SetState(SessionStatus.Thinking, "");

        // 실패는 팝업에 보여줄 상태로 바꾸고 밖으로 던지지 않는다. 클립보드는 건드리지 않는다.
        try
        {
            await foreach (var streamEvent in _provider.StreamAsync(request, running.Token))
            {
                switch (streamEvent)
                {
                    case TextDelta delta:
                        raw.Append(delta.Text);
                        SetState(SessionStatus.Writing, raw.ToString());
                        break;
                    case StreamFailed failed:
                        SetState(SessionStatus.Failed, raw.ToString(), ErrorMessages.ForStreamFailure(failed.Message));
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            SetState(SessionStatus.Cancelled, raw.ToString());
            return;
        }
        catch (Exception ex)
        {
            SetState(SessionStatus.Failed, raw.ToString(), ErrorMessages.For(ex));
            return;
        }
        finally
        {
            _running = null;
        }

        var code = ResponseFormatter.Format(raw.ToString(), _shape);
        _conversation.RecordResult(code);
        _clipboard.SetText(code);
        SetState(SessionStatus.Done, code);
    }

    /// <summary>진행 중인 요청을 멈춘다 (팝업의 Esc). 받은 부분은 남고 클립보드는 그대로.</summary>
    public void Cancel() => _running?.Cancel();

    private void SetState(SessionStatus status, string output, UserError? error = null)
    {
        Status = status;
        Output = output;
        Error = error;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
