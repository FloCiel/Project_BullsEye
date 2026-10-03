namespace BullsEye.Core;

public sealed record ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

public sealed record PopupSize(int Width, int Height);

/// <summary>팝업의 왼쪽 위 좌표를 정한다. 기준점 아래가 기본, 화면 밖으로 나가지 않게 한다.</summary>
public static class PopupPlacement
{
    /// <summary>캐럿 아래 모서리와 팝업 사이 간격.</summary>
    public const int CaretGap = 4;

    /// <summary>위로 뒤집을 때 건너뛸 캐럿 줄 높이 (기준점은 캐럿 아래 모서리).</summary>
    public const int LineHeight = 24;

    /// <summary>마우스 기준일 때 커서 그림을 가리지 않는 간격.</summary>
    public const int MouseGap = 20;

    public static ScreenPoint Compute(PopupAnchor anchor, PopupSize size, ScreenRect workArea)
    {
        var (belowGap, aboveOffset) = anchor.FromCaret
            ? (CaretGap, LineHeight + CaretGap)
            : (MouseGap, CaretGap);

        var y = anchor.Point.Y + belowGap;
        if (y + size.Height > workArea.Bottom)
        {
            y = anchor.Point.Y - aboveOffset - size.Height;
        }

        // 그래도 넘치면 (작은 화면 등) 작업 영역 안으로 밀어 넣는다. 왼쪽/위가 우선.
        var x = Clamp(anchor.Point.X, workArea.Left, workArea.Right - size.Width);
        y = Clamp(y, workArea.Top, workArea.Bottom - size.Height);

        return new ScreenPoint(x, y);
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, max));
}
