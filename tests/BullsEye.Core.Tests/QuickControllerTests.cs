using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class QuickControllerTests
{
    private static readonly ForegroundWindow Matlab = new(0x1234, "MATLAB.exe", "Editor - C:\\work\\rect.m");
    private static readonly SessionOptions Options = new(["vendor/fast"], true, ReasoningEffort.Medium);

    private readonly FakeProvider _provider = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeIndicator _indicator = new();
    private readonly FakeWindows _windows = new(Matlab);

    private QuickController Controller(FakeSelection selection) => new(
        _windows,
        selection,
        _indicator,
        LanguageDetector.Default,
        (code, lang, shape, keep) => new CompletionSession(_provider, _clipboard, new PromptHistory(10), Options, code, lang, shape, keep),
        captureTimeout: TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task OnHotkey_SendsInstructionSeparately_AndCopiesResultShapedLikeWholeSelection()
    {
        _provider.Reply(new TextDelta("y = x.^2;"), new Completed());
        var controller = Controller(new FakeSelection("    % >> 벡터화해줘\r\n    for i = 1:n\r\n        y(i) = x(i)^2;\r\n    end\r\n"));

        await controller.OnHotkeyAsync();

        var user = Assert.Single(_provider.Requests).Messages[^1].Content;
        Assert.Contains("Instruction: 벡터화해줘", user);
        Assert.Contains("MATLAB", user);
        Assert.Contains("for i = 1:n", user);
        Assert.DoesNotContain(">>", user);

        // 지시 줄까지 포함한 선택 영역 전체를 교체한다: 첫 줄 들여쓰기와 끝 줄바꿈을 따르고, 지시 줄은 위에 남긴다.
        Assert.Equal(["    % >> 벡터화해줘\r\n    y = x.^2;\r\n"], _clipboard.Writes);
        Assert.Equal([SessionStatus.Thinking, SessionStatus.Writing, SessionStatus.Done], _indicator.Statuses);
        Assert.Empty(_windows.Restored);
    }

    [Fact]
    public async Task OnHotkey_InstructionOnly_GeneratesNewCode()
    {
        _provider.Reply(new TextDelta("function f = fib(n)"), new Completed());
        var controller = Controller(new FakeSelection("% >> 피보나치 함수"));

        await controller.OnHotkeyAsync();

        Assert.Contains("Nothing is selected", _provider.Requests[0].Messages[^1].Content);
        Assert.Equal(["% >> 피보나치 함수\r\nfunction f = fib(n)"], _clipboard.Writes);
    }

    [Fact]
    public async Task OnHotkey_PastedResultWithEditedInstruction_RequestsAgainOnPreviousResult()
    {
        _provider.Reply(new TextDelta("y = x.^2;"), new Completed());
        await Controller(new FakeSelection("% >> 벡터화해줘\nfor i = 1:n\n    y(i) = x(i)^2;\nend\n")).OnHotkeyAsync();

        // 붙여넣은 결과에서 지시만 고쳐 영역째 다시 선택한 상황.
        var edited = _clipboard.Writes[0].Replace("벡터화해줘", "주석 달아줘");
        _provider.Reply(new TextDelta("y = x.^2; % 제곱"), new Completed());
        await Controller(new FakeSelection(edited)).OnHotkeyAsync();

        var user = _provider.Requests[1].Messages[^1].Content;
        Assert.Contains("Instruction: 주석 달아줘", user);
        Assert.Contains("y = x.^2;", user);
        Assert.DoesNotContain(">>", user);
        Assert.Equal("% >> 주석 달아줘\ny = x.^2; % 제곱\n", _clipboard.Writes[1]);
    }

    [Theory]
    [InlineData(null, QuickController.NoSelectionMessage)]
    [InlineData("x = 1;", QuickController.NoInstructionMessage)]
    public async Task OnHotkey_WithoutInstruction_ReportsFailureWithoutRequest(string? selection, string message)
    {
        var controller = Controller(new FakeSelection(selection));

        await controller.OnHotkeyAsync();

        Assert.Empty(_provider.Requests);
        Assert.Empty(_clipboard.Writes);
        Assert.Equal([(SessionStatus.Failed, message)], _indicator.Shown);
    }

    [Fact]
    public async Task OnHotkey_WhileRunning_CancelsWithoutCapturingAgain()
    {
        _provider.Hang(new TextDelta("y ="));
        var selection = new FakeSelection("% >> 벡터화해줘\nfor i = 1:n\nend");
        var controller = Controller(selection);
        var first = controller.OnHotkeyAsync();
        Assert.True(controller.IsBusy);

        await controller.OnHotkeyAsync();
        await first;

        Assert.Equal(1, selection.Calls);
        Assert.Equal(SessionStatus.Cancelled, _indicator.Statuses[^1]);
        Assert.Empty(_clipboard.Writes);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task OnHotkey_ProviderFailure_SaysToPressHotkeyAgainInsteadOfEnter()
    {
        _provider.Throw(new NetworkException(new HttpRequestException()));
        var controller = Controller(new FakeSelection("% >> 벡터화해줘"));

        await controller.OnHotkeyAsync();

        var (status, message) = _indicator.Shown[^1];
        Assert.Equal(SessionStatus.Failed, status);
        Assert.Contains("단축키를 다시 눌러", message);
        Assert.DoesNotContain("Enter", message);
    }

    [Fact]
    public async Task OnHotkey_InstructionAboveCommentedFunction_IsSentAsRequest()
    {
        _provider.Reply(new TextDelta("V = 1;"), new Completed());
        var text = """
            % >> 좀더 간소화해봐.

            function V = triPrismVolume(a, b, c, L)
            % triPrismVolume  삼각기둥의 부피를 반환한다.
            %   a, b, c : 삼각형 밑면의 세 변의 길이
            %   L       : 기둥의 높이(길이)
            %
            %   V = triPrismVolume(a, b, c, L)

                if nargin < 4
                    error('triPrismVolume: 입력값 4개(a, b, c, L)가 필요합니다.');
                end
                if any([a b c L] <= 0)
                    error('triPrismVolume: 모든 입력값은 양수여야 합니다.');
                end

                s = (a + b + c) / 2;                    % 반둘레
                baseArea = sqrt(s * (s-a) * (s-b) * (s-c));   % 헤론의 공식
                if ~isreal(baseArea)
                    error('triPrismVolume: 세 변이 삼각형을 이루지 못합니다.');
                end

                V = baseArea * L;
            end
            """.ReplaceLineEndings("\r\n");
        var controller = Controller(new FakeSelection(text));

        await controller.OnHotkeyAsync();

        Assert.Equal([SessionStatus.Thinking, SessionStatus.Writing, SessionStatus.Done], _indicator.Statuses);
    }
}
