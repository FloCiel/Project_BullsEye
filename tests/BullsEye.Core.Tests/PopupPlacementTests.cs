namespace BullsEye.Core.Tests;

public class PopupPlacementTests
{
    private static readonly ScreenRect WorkArea = new(Left: 0, Top: 0, Width: 1920, Height: 1040);
    private static readonly PopupSize Size = new(Width: 480, Height: 200);

    private static PopupAnchor Caret(int x, int y) => new(new ScreenPoint(x, y), FromCaret: true);

    [Fact]
    public void Compute_PlacesJustBelowCaret_WhenThereIsRoom()
    {
        var position = PopupPlacement.Compute(Caret(300, 400), Size, WorkArea);

        Assert.Equal(new ScreenPoint(300, 400 + PopupPlacement.CaretGap), position);
    }

    [Fact]
    public void Compute_FlipsAboveCaretLine_WhenNoRoomBelow()
    {
        var position = PopupPlacement.Compute(Caret(300, 1000), Size, WorkArea);

        // 캐럿 줄(아래 모서리 1000, 높이 LineHeight)을 가리지 않게 그 위로
        Assert.Equal(new ScreenPoint(300, 1000 - PopupPlacement.LineHeight - 200 - PopupPlacement.CaretGap), position);
    }

    [Fact]
    public void Compute_ShiftsLeft_AtRightEdge()
    {
        var position = PopupPlacement.Compute(Caret(1800, 400), Size, WorkArea);

        Assert.Equal(1920 - 480, position.X);
    }

    [Theory]
    [InlineData(-1920, 0, 1920, 1040, -50, 500)]   // 왼쪽 보조 모니터, 오른쪽 끝
    [InlineData(0, 0, 1920, 300, 300, 150)]         // 위아래 모두 부족
    [InlineData(0, 0, 1920, 1040, -100, 10)]        // 기준점이 작업 영역 밖 (왼쪽)
    [InlineData(0, 0, 400, 150, 100, 100)]          // 팝업보다 작은 작업 영역
    public void Compute_StaysInsideWorkArea(int left, int top, int width, int height, int x, int y)
    {
        var area = new ScreenRect(left, top, width, height);

        var position = PopupPlacement.Compute(Caret(x, y), Size, area);

        Assert.InRange(position.X, area.Left, Math.Max(area.Left, area.Right - Size.Width));
        Assert.InRange(position.Y, area.Top, Math.Max(area.Top, area.Bottom - Size.Height));
    }

    [Fact]
    public void Compute_LeavesRoomForMouseCursor_WhenAnchoredToMouse()
    {
        var mouse = new PopupAnchor(new ScreenPoint(300, 400), FromCaret: false);

        var position = PopupPlacement.Compute(mouse, Size, WorkArea);

        Assert.Equal(new ScreenPoint(300, 400 + PopupPlacement.MouseGap), position);
    }
}
