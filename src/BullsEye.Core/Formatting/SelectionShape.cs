namespace BullsEye.Core;

/// <summary>붙여넣을 위치(선택 영역)의 형태. 결과를 여기에 맞춘다.</summary>
public sealed record SelectionShape(string Indent, string LineEnding)
{
    public static SelectionShape None { get; } = new("", "\r\n");

    public static SelectionShape From(string selectedText)
    {
        var lineEnding = selectedText.Contains("\r\n") ? "\r\n"
            : selectedText.Contains('\n') ? "\n"
            : None.LineEnding;

        var firstLine = selectedText.Split('\n')[0];
        var indent = firstLine[..(firstLine.Length - firstLine.TrimStart(' ', '\t').Length)];

        return new SelectionShape(indent, lineEnding);
    }
}
