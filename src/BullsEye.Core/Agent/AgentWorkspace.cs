using System.Text;

namespace BullsEye.Core;

/// <summary>
/// 에이전트가 만질 수 있는 폴더 하나. 모든 경로는 이 폴더 기준이고 밖으로 나갈 수 없다
/// (<c>..</c>, 다른 드라이브, 밖을 가리키는 정션·심볼릭 링크 모두 거절).
/// 파일의 인코딩(UTF-8, BOM, CP949)과 줄바꿈을 기억해 쓸 때 되살린다: 모델은 항상 유니코드와 <c>\n</c>으로 본다.
/// </summary>
public sealed class AgentWorkspace
{
    /// <summary>이보다 큰 파일은 읽지 않는다 (MATLAB 함수 파일에는 넉넉하다).</summary>
    public const int MaxFileBytes = 1024 * 1024;

    // 한국어 Windows의 구형 MATLAB은 .m 파일을 시스템 코드 페이지(CP949)로 저장한다.
    private static readonly Encoding Cp949;

    static AgentWorkspace()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public AgentWorkspace(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException($"작업 폴더가 없습니다: {Root}");
    }

    public string Root { get; }

    /// <summary>작업 폴더 안의 절대 경로로 바꾼다. 상대 경로는 작업 폴더 기준.</summary>
    /// <exception cref="AgentToolException">비었거나 작업 폴더 밖을 가리킴.</exception>
    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new AgentToolException("Path is empty.");

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, Root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentToolException($"Invalid path: {path}");
        }

        if (!IsInsideRoot(full) || CrossesLink(full))
        {
            throw new AgentToolException($"Path is outside the workspace: {path}");
        }

        return full;
    }

    /// <summary>작업 폴더 기준 상대 경로 (구분자 <c>/</c>). 모델에게 보여줄 때 쓴다.</summary>
    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    /// <exception cref="AgentToolException">없거나, 너무 크거나, 텍스트로 읽을 수 없음.</exception>
    public string ReadText(string path) => Read(Resolve(path), path).Text;

    /// <summary>
    /// 파일을 통째로 쓴다. 이미 있는 파일은 원래 인코딩·줄바꿈을 지키고,
    /// 새 파일은 UTF-8(BOM 없음) + CRLF. 필요한 폴더는 만든다.
    /// </summary>
    /// <exception cref="AgentToolException">원래 인코딩으로 표현할 수 없는 문자가 있음 (CP949 파일에 이모지 등).</exception>
    public void WriteText(string path, string text)
    {
        var full = Resolve(path);
        var format = File.Exists(full) ? Read(full, path).Format : TextFormat.Default;

        var normalized = text.ReplaceLineEndings("\n");
        if (format.LineEnding != "\n") normalized = normalized.Replace("\n", format.LineEnding);

        byte[] bytes;
        try
        {
            bytes = [.. format.Encoding.GetPreamble(), .. format.Encoding.GetBytes(normalized)];
        }
        catch (EncoderFallbackException)
        {
            throw new AgentToolException(
                $"{path} is saved as {format.Encoding.WebName} and cannot store some characters in the new text. Use only characters that encoding supports.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private (string Text, TextFormat Format) Read(string full, string path)
    {
        if (Directory.Exists(full)) throw new AgentToolException($"{path} is a directory, not a file.");
        if (!File.Exists(full)) throw new AgentToolException($"File not found: {path}");

        var bytes = File.ReadAllBytes(full);
        if (bytes.Length > MaxFileBytes) throw new AgentToolException($"{path} is too large ({bytes.Length} bytes).");

        var (encoding, text) = Decode(bytes) ?? throw new AgentToolException($"{path} is not a text file.");
        var lineEnding = text.Contains("\r\n") ? "\r\n" : "\n";
        return (text.ReplaceLineEndings("\n"), new TextFormat(encoding, lineEnding));
    }

    private static (Encoding, string)? Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            var withBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
            return (withBom, withBom.GetString(bytes, 3, bytes.Length - 3));
        }

        if (Array.IndexOf(bytes, (byte)0) >= 0) return null; // UTF-16이나 바이너리. .m 파일에는 없다.

        try
        {
            return (TextFormat.Default.Encoding, TextFormat.Default.Encoding.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
        }

        try
        {
            return (Cp949, Cp949.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private bool IsInsideRoot(string full) =>
        full.Equals(Root, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>작업 폴더 아래 경로 중 이미 있는 부분에 정션·심볼릭 링크가 있으면 밖으로 샐 수 있다.</summary>
    private bool CrossesLink(string full)
    {
        for (var current = full; current.Length > Root.Length; current = Path.GetDirectoryName(current)!)
        {
            var info = new FileInfo(current);
            if (info.Exists || Directory.Exists(current))
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            }
        }

        return false;
    }

    private sealed record TextFormat(Encoding Encoding, string LineEnding)
    {
        public static TextFormat Default { get; } =
            new(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), "\r\n");
    }
}
