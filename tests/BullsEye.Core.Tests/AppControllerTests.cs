using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class AppControllerTests
{
    private static readonly ForegroundWindow Matlab = new(0x1234, "MATLAB.exe", "Editor - C:\\work\\rect.m");
    private static readonly PopupAnchor Anchor = new(new ScreenPoint(300, 400), FromCaret: false);
    private static readonly SessionOptions Options = new(["vendor/fast"], true, true);

    private readonly FakeProvider _provider = new();
    private readonly FakePopupHost _popup = new();
    private readonly FakeWindows _windows = new(Matlab);

    private AppController Controller(FakeSelection selection) => new(
        _windows,
        selection,
        new FakeCaret(Anchor),
        _popup,
        LanguageDetector.Default,
        (sel, lang) => new CompletionSession(_provider, new FakeClipboard(), new PromptHistory(10), Options, sel, lang),
        captureTimeout: TimeSpan.FromMilliseconds(300));

    [Theory]
    [InlineData("    area = ")]
    [InlineData(null)]
    public async Task OnHotkey_CapturesContext_AndOpensPopup(string? selection)
    {
        var controller = Controller(new FakeSelection(selection));

        await controller.OnHotkeyAsync();

        var request = Assert.Single(_popup.Opened);
        Assert.Equal(Matlab, request.Window);
        Assert.Equal(Anchor, request.Anchor);
        Assert.Equal("MATLAB", request.Language);
        Assert.Equal(selection, request.Selection);
        Assert.Equal(SessionStatus.Ready, request.Session.Status);
    }

    [Fact]
    public async Task ClosingPopup_RestoresFocus_AndCancelsRunningRequest()
    {
        _provider.Hang();
        var controller = Controller(new FakeSelection("area = "));
        await controller.OnHotkeyAsync();
        var session = _popup.Opened[0].Session;
        var submit = session.SubmitAsync("사각형 넓이로 완성");

        _popup.Close();
        await submit;

        Assert.Equal([Matlab], _windows.Restored);
        Assert.Equal(SessionStatus.Cancelled, session.Status);
    }

    [Fact]
    public async Task ClosingPopup_ByClickingAnotherWindow_DoesNotStealFocusBack()
    {
        var controller = Controller(new FakeSelection("area = "));
        await controller.OnHotkeyAsync();

        _popup.Close(returnFocus: false);

        Assert.Empty(_windows.Restored);
    }

    [Fact]
    public async Task OnHotkey_WhilePopupOpen_ActivatesItInsteadOfCapturingAgain()
    {
        var selection = new FakeSelection("area = ");
        var controller = Controller(selection);
        await controller.OnHotkeyAsync();

        await controller.OnHotkeyAsync();

        Assert.Single(_popup.Opened);
        Assert.Equal(1, selection.Calls);
        Assert.Equal(1, _popup.Activations);
    }

    [Fact]
    public async Task OnHotkey_DuringCapture_IsIgnored()
    {
        var selection = new FakeSelection("area = ").Hold();
        var controller = Controller(selection);

        var first = controller.OnHotkeyAsync();
        await controller.OnHotkeyAsync();
        selection.Release();
        await first;

        Assert.Equal(1, selection.Calls);
        Assert.Single(_popup.Opened);
    }
}
