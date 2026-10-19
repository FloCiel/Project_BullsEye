using System.Text;
using System.Text.Json;

namespace BullsEye.Core;

/// <summary>
/// 도구 실패. 메시지는 사용자가 아니라 모델에게 돌려주는 것이라 영어로 쓴다:
/// 모델은 이걸 보고 경로나 인자를 고쳐 다시 시도한다.
/// </summary>
public sealed class AgentToolException(string message) : BullsEyeException(message);

/// <summary>에이전트가 바꾸려는 파일 하나. 쓰기 전에 <see cref="IChangeApprover"/>에게 보인다.</summary>
/// <param name="Path">작업 폴더 기준 상대 경로 (<c>/</c> 구분).</param>
/// <param name="Before">지금 내용. 새 파일이면 null.</param>
/// <param name="After">바뀐 뒤 내용. 둘 다 줄바꿈은 <c>\n</c>.</param>
public sealed record FileChange(string Path, string? Before, string After);

/// <summary>파일 변경을 실제로 쓰기 전에 사용자에게 묻는다 (2단계에서 diff 승인 창이 구현).</summary>
public interface IChangeApprover
{
    Task<bool> ApproveAsync(FileChange change, CancellationToken cancellationToken);
}

/// <summary>모델에게 돌려줄 도구 결과. 실패도 결과로 돌려줘서 모델이 스스로 고치게 한다.</summary>
public sealed record ToolResult(string Content, bool IsError = false);

/// <summary>
/// pi 방식의 최소 도구 모음: 목록 / 읽기 / 일치하는 부분 바꾸기 / 쓰기.
/// 명령 실행 도구는 없다 (공용 PC에서 아무 명령이나 돌리지 않게).
/// </summary>
/// <param name="backup">있으면 승인된 변경을 쓰기 직전에 원본을 복사해 둔다.</param>
public sealed class AgentTools(AgentWorkspace workspace, IChangeApprover approver, AgentBackup? backup = null)
{
    public const int MaxListedFiles = 300;
    public const int DefaultReadLines = 2000;
    public const int MaxReadChars = 60_000;

    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new("list_files",
            "List files in the workspace (recursively). Paths are relative to the workspace root.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Directory to list. Default: workspace root."},
              "pattern":{"type":"string","description":"File name pattern such as *.m. Default: *."}
            }}
            """),
        new("read_file",
            "Read a text file. Returns its content as-is (no line numbers). Use offset/limit for long files.",
            """
            {"type":"object","properties":{
              "path":{"type":"string"},
              "offset":{"type":"integer","description":"1-based line to start from. Default: 1."},
              "limit":{"type":"integer","description":"Maximum number of lines. Default: 2000."}
            },"required":["path"]}
            """),
        new("edit_file",
            "Replace one occurrence of old_text with new_text in a file. Copy old_text exactly from the file and include enough surrounding lines to make it unique. If it does not match exactly, whole lines are matched ignoring trailing whitespace and then indentation, but only when that finds exactly one place. Read the file first.",
            """
            {"type":"object","properties":{
              "path":{"type":"string"},
              "old_text":{"type":"string"},
              "new_text":{"type":"string"}
            },"required":["path","old_text","new_text"]}
            """),
        new("write_file",
            "Create a new file or replace a whole file. Prefer edit_file for changing part of an existing file.",
            """
            {"type":"object","properties":{
              "path":{"type":"string"},
              "content":{"type":"string"}
            },"required":["path","content"]}
            """),
    ];

    /// <summary>도구 하나를 실행한다. 잘못된 호출이나 실패는 던지지 않고 오류 결과로 돌려준다 (취소만 던진다).</summary>
    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = ParseArguments(call.Arguments);
            return call.Name switch
            {
                "list_files" => List(Optional(args, "path") ?? ".", Optional(args, "pattern") ?? "*"),
                "read_file" => Read(Required(args, "path"), OptionalInt(args, "offset") ?? 1, OptionalInt(args, "limit") ?? DefaultReadLines),
                "edit_file" => await EditAsync(Required(args, "path"), Required(args, "old_text"), Required(args, "new_text"), cancellationToken),
                "write_file" => await WriteAsync(Required(args, "path"), Required(args, "content"), cancellationToken),
                _ => throw new AgentToolException($"Unknown tool: {call.Name}"),
            };
        }
        catch (AgentToolException ex)
        {
            return new ToolResult($"Error: {ex.Message}", IsError: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ToolResult($"Error: {ex.Message}", IsError: true);
        }
    }

    private ToolResult List(string path, string pattern)
    {
        var directory = workspace.Resolve(path);
        if (!Directory.Exists(directory)) throw new AgentToolException($"Directory not found: {path}");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var files = Directory.EnumerateFiles(directory, pattern, options)
            .Select(workspace.Relative)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(MaxListedFiles + 1)
            .ToList();

        if (files.Count == 0) return new ToolResult("(no files)");
        if (files.Count <= MaxListedFiles) return new ToolResult(string.Join("\n", files));
        return new ToolResult(string.Join("\n", files.Take(MaxListedFiles)) + $"\n(truncated: more than {MaxListedFiles} files, narrow path or pattern)");
    }

    private ToolResult Read(string path, int offset, int limit)
    {
        if (offset < 1) throw new AgentToolException("offset must be 1 or more.");
        if (limit < 1) throw new AgentToolException("limit must be 1 or more.");

        var lines = workspace.ReadText(path).Split('\n');
        if (offset > lines.Length) throw new AgentToolException($"offset {offset} is past the end of the file ({lines.Length} lines).");

        var text = new StringBuilder();
        var end = offset - 1;
        while (end < lines.Length && end < offset - 1 + limit)
        {
            if (text.Length + lines[end].Length > MaxReadChars && end > offset - 1) break;
            if (end > offset - 1) text.Append('\n');
            text.Append(lines[end]);
            end++;
        }

        if (end < lines.Length)
        {
            text.Append($"\n\n(showing lines {offset}-{end} of {lines.Length}; continue with offset={end + 1})");
        }

        return new ToolResult(text.ToString());
    }

    private async Task<ToolResult> EditAsync(string path, string oldText, string newText, CancellationToken cancellationToken)
    {
        oldText = oldText.ReplaceLineEndings("\n");
        newText = newText.ReplaceLineEndings("\n");
        if (oldText.Length == 0) throw new AgentToolException("old_text is empty. Use write_file to create a file.");

        var before = workspace.ReadText(path);
        var match = EditMatcher.Find(before, oldText, newText, path);
        var after = string.Concat(before.AsSpan(0, match.Start), match.Replacement, before.AsSpan(match.Start + match.Length));
        var edited = $"Edited {workspace.Relative(workspace.Resolve(path))}.";
        return await ApplyAsync(path, before, after, cancellationToken)
            ?? new ToolResult(match.Note is null ? edited : $"{edited} ({match.Note})");
    }

    private async Task<ToolResult> WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var full = workspace.Resolve(path);
        if (Directory.Exists(full)) throw new AgentToolException($"{path} is a directory.");

        var before = File.Exists(full) ? workspace.ReadText(path) : null;
        var after = content.ReplaceLineEndings("\n");
        var lineCount = after.Length == 0 ? 0 : after.TrimEnd('\n').Split('\n').Length;
        return await ApplyAsync(path, before, after, cancellationToken)
            ?? new ToolResult($"{(before is null ? "Created" : "Wrote")} {workspace.Relative(full)} ({lineCount} lines).");
    }

    /// <summary>승인을 받아 쓴다. 거절되면 그 결과를, 썼으면 null을 돌려준다.</summary>
    private async Task<ToolResult?> ApplyAsync(string path, string? before, string after, CancellationToken cancellationToken)
    {
        var relative = workspace.Relative(workspace.Resolve(path));
        if (before == after) return new ToolResult($"No change: {relative} already has this content.");

        if (!await approver.ApproveAsync(new FileChange(relative, before, after), cancellationToken))
        {
            return new ToolResult(
                $"The user rejected the change to {relative}. Do not retry the same change; ask the user what they want instead.",
                IsError: true);
        }

        backup?.Save(relative);
        workspace.WriteText(path, after);
        return null;
    }

    private static JsonElement ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) arguments = "{}";
        try
        {
            using var json = JsonDocument.Parse(arguments);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new AgentToolException("Arguments must be a JSON object.");
            return json.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AgentToolException("Arguments are not valid JSON.");
        }
    }

    private static string Required(JsonElement args, string name) =>
        Optional(args, name) ?? throw new AgentToolException($"Missing required argument: {name}");

    private static string? Optional(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new AgentToolException($"Argument {name} must be a string.")
            : null;

    private static int? OptionalInt(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        // 모델이 숫자를 문자열로 보내는 경우가 흔하다.
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n)) return n;
        throw new AgentToolException($"Argument {name} must be an integer.");
    }
}
