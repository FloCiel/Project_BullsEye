using System.Drawing.Drawing2D;

namespace BullsEye.Ui;

/// <summary>트레이 아이콘: 과녁 (Bullseye Red #D9473F / 흰색). 파일 없이 그린다.</summary>
internal static class AppIcon
{
    /// <param name="badge">간소화 모드 상태 색. 오른쪽 아래에 작은 원으로 겹쳐 그린다.</param>
    public static Icon Create(int size = 32, Color? badge = null)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var red = ColorTranslator.FromHtml("#D9473F");
            Ring(g, red, size, 0.0f);
            Ring(g, Color.White, size, 0.17f);
            Ring(g, red, size, 0.32f);
            Ring(g, Color.White, size, 0.46f);
            Ring(g, red, size, 0.6f);

            if (badge is { } color)
            {
                // 흰 테두리로 과녁의 빨강과 구분한다.
                var diameter = size * 0.5f;
                var origin = size - diameter;
                using var border = new SolidBrush(Color.White);
                using var fill = new SolidBrush(color);
                g.FillEllipse(border, origin, origin, diameter, diameter);
                g.FillEllipse(fill, origin + 2, origin + 2, diameter - 4, diameter - 4);
            }
        }

        // GetHicon 핸들은 앱 수명 동안 하나만 만든다.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static void Ring(Graphics g, Color color, int size, float inset)
    {
        var margin = size * inset / 2 + 1;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, margin, margin, size - 2 * margin, size - 2 * margin);
    }
}
