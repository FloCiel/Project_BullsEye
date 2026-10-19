namespace BullsEye.Core;

/// <summary>
/// 창 없는 에이전트가 고칠 곳: 선택한 텍스트가 들어 있는 저장된 파일과 그 줄 범위.
/// </summary>
/// <param name="Folder">작업 폴더.</param>
/// <param name="File">작업 폴더 기준 상대 경로 (<c>/</c> 구분).</param>
/// <param name="FirstLine">선택 영역의 첫 줄 (1부터). 파일 안에 같은 내용이 여러 번 있으면 null.</param>
/// <param name="InstructionLines">선택 영역 안의 <c>&gt;&gt;</c> 지시 줄 (줄 번호, 원문). 끝난 뒤 정리할 때 쓴다.</param>
public sealed record SelectionLocation(
    string Folder, string File, int? FirstLine, int? LastLine, IReadOnlyList<(int Line, string Text)> InstructionLines);

/// <summary>
/// 선택한 텍스트를 디스크의 파일에서 찾는다. 창 없는 모드는 묻지 않고 파일을 고치므로,
/// "지금 보고 있는 그 파일"임을 확인한 뒤에만 실행한다.
/// <list type="bullet">
/// <item>창 제목에 파일 경로가 있으면 그 파일에 선택 내용이 있어야 한다 (없으면 저장하지 않은 것).</item>
/// <item>경로가 없으면(도킹한 MATLAB 편집기) 지난 작업 폴더에서 선택 내용이 든 파일이 딱 하나여야 한다.</item>
/// </list>
/// </summary>
public static class SelectionLocator
{
    public const int MaxScannedFiles = 500;

    /// <returns>찾았으면 위치, 못 찾았으면 사용자에게 보여줄 이유.</returns>
    public static (SelectionLocation? Location, string? Error) Locate(string selection, string? titleFile, string? fallbackFolder)
    {
        var wanted = Normalize(selection);
        if (wanted.Length == 0) return (null, "선택한 내용이 비어 있습니다.");

        if (titleFile is not null)
        {
            var folder = Path.GetDirectoryName(titleFile);
            if (folder is null || !Directory.Exists(folder)) return (null, null);

            var name = Path.GetFileName(titleFile);
            var workspace = new AgentWorkspace(folder);
            string text;
            try
            {
                text = workspace.ReadText(name);
            }
            catch (Exception ex) when (ex is AgentToolException or IOException or UnauthorizedAccessException)
            {
                return (null, $"{name} 파일을 텍스트로 읽지 못해 선택 위치를 확인할 수 없습니다.");
            }

            return Find(text, wanted) is { } found
                ? (Build(folder, name, text, wanted, found), null)
                : (null, $"선택한 내용이 저장된 {name} 파일과 다릅니다. 저장(Ctrl+S)한 뒤 다시 누르세요.");
        }

        if (fallbackFolder is null || !Directory.Exists(fallbackFolder)) return (null, null);

        var root = new AgentWorkspace(fallbackFolder);
        var hits = new List<(string File, string Text, List<int> Found)>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        foreach (var full in Directory.EnumerateFiles(root.Root, "*", options).Take(MaxScannedFiles))
        {
            var relative = root.Relative(full);
            try
            {
                var text = root.ReadText(relative);
                if (Find(text, wanted) is { } found) hits.Add((relative, text, found));
            }
            catch (Exception ex) when (ex is AgentToolException or IOException or UnauthorizedAccessException)
            {
                // 바이너리이거나 읽을 수 없는 파일.
            }
        }

        var folderName = Path.GetFileName(root.Root);
        return hits.Count switch
        {
            1 => (Build(root.Root, hits[0].File, hits[0].Text, wanted, hits[0].Found), null),
            0 => (null, $"선택한 내용을 작업 폴더({folderName})의 저장된 파일에서 찾지 못했습니다. 저장했는지, 같은 폴더인지 확인하세요."),
            _ => (null, $"선택한 내용이 여러 파일({string.Join(", ", hits.Take(3).Select(h => h.File))})에 있어 어느 파일인지 알 수 없습니다."),
        };
    }

    /// <summary>줄바꿈을 <c>\n</c>으로 맞추고 앞뒤 빈 줄을 뺀다 (선택이 줄 끝까지 잡히는 편집기가 있다).</summary>
    private static string Normalize(string selection) => selection.ReplaceLineEndings("\n").Trim('\n');

    private static List<int>? Find(string text, string wanted)
    {
        var found = new List<int>();
        for (var i = text.IndexOf(wanted, StringComparison.Ordinal); i >= 0; i = text.IndexOf(wanted, i + 1, StringComparison.Ordinal))
        {
            found.Add(i);
        }

        return found.Count > 0 ? found : null;
    }

    private static SelectionLocation Build(string folder, string file, string text, string wanted, List<int> found)
    {
        if (found.Count > 1) return new SelectionLocation(folder, file, null, null, []);

        var lines = text.Split('\n');
        var first = text.AsSpan(0, found[0]).Count('\n') + 1;
        var last = first + wanted.AsSpan().Count('\n');
        var instructions = Enumerable.Range(first, last - first + 1)
            .Where(line => InlineInstruction.IsMarkerLine(lines[line - 1]))
            .Select(line => (line, lines[line - 1]))
            .ToList();
        return new SelectionLocation(folder, file, first, last, instructions);
    }
}
