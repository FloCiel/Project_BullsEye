using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class QuickControllerTests
{
    private static readonly ForegroundWindow Matlab = new(0x1234, "MATLAB.exe", "Editor - C:\\work\\rect.m");
    private static readonly SessionOptions Options = new(["vendor/fast"], true, true);

    private readonly FakeProvider _provider = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeIndicator _indicator = new();
    private readonly FakeWindows _windows = new(Matlab);

    private QuickController Controller(FakeSelection selection) => new(
        _windows,
        selection,
        _indicator,
        LanguageDetector.Default,
        (code, lang, shape) => new CompletionSession(_provider, _clipboard, new PromptHistory(10), Options, code, lang, shape),
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

        // 지시 줄까지 포함한 선택 영역 전체를 교체한다: 첫 줄 들여쓰기와 끝 줄바꿈을 따른다.
        Assert.Equal(["    y = x.^2;\r\n"], _clipboard.Writes);
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
        Assert.Equal(["function f = fib(n)"], _clipboard.Writes);
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
}
