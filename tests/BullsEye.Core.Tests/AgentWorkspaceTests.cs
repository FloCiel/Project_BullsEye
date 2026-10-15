using System.Diagnostics;
using System.Text;
using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class AgentWorkspaceTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private AgentWorkspace Workspace() => new(_folder.Path);

    [Theory]
    [InlineData("a.m")]
    [InlineData("sub/b.m")]
    [InlineData("./sub/../a.m")]
    public void Resolve_PathInside_ReturnsFullPath(string path)
    {
        var full = Workspace().Resolve(path);

        Assert.StartsWith(_folder.Path, full, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.m")]
    [InlineData("sub/../../outside.m")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\x.m")]
    [InlineData("")]
    public void Resolve_PathOutsideOrEmpty_Throws(string path)
    {
        Assert.Throws<AgentToolException>(() => Workspace().Resolve(path));
    }

    [Fact]
    public void Resolve_SiblingFolderWithSamePrefix_Throws()
    {
        // C:\temp\ws 와 C:\temp\ws-other: 문자열 접두사만 보면 안쪽으로 착각한다.
        var sibling = _folder.Path + "-other";

        Assert.Throws<AgentToolException>(() => Workspace().Resolve(sibling + "\\x.m"));
    }

    [Fact]
    public void Resolve_ThroughJunctionToOutside_Throws()
    {
        using var outside = new TempFolder();
        outside.File("secret.m", "x");
        var link = Path.Combine(_folder.Path, "link");
        // 정션은 관리자 권한 없이 만들 수 있다 (심볼릭 링크와 달리).
        var mklink = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Path}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using (var process = Process.Start(mklink)!)
        {
            process.WaitForExit();
        }

        try
        {
            Assert.True(Directory.Exists(link));
            Assert.Throws<AgentToolException>(() => Workspace().Resolve("link/secret.m"));
        }
        finally
        {
            Directory.Delete(link); // 정션만 지운다 (대상 폴더는 그대로).
        }
    }

    [Fact]
    public void ReadText_NormalizesCrlf_AndWriteTextRestoresIt()
    {
        _folder.File("a.m", "x = 1;\r\ny = 2;\r\n");
        var workspace = Workspace();

        Assert.Equal("x = 1;\ny = 2;\n", workspace.ReadText("a.m"));

        workspace.WriteText("a.m", "x = 1;\nz = 3;\n");
        Assert.Equal("x = 1;\r\nz = 3;\r\n", File.ReadAllText(Path.Combine(_folder.Path, "a.m")));
    }

    [Fact]
    public void WriteText_KeepsLfFile_AsLf()
    {
        _folder.File("a.m", "x = 1;\n");
        var workspace = Workspace();

        workspace.WriteText("a.m", "y = 2;\n");

        Assert.Equal("y = 2;\n", File.ReadAllText(Path.Combine(_folder.Path, "a.m")));
    }

    [Fact]
    public void Cp949File_IsReadAsKorean_AndWrittenBackAsCp949()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp949 = Encoding.GetEncoding(949);
        var path = _folder.Bytes("a.m", cp949.GetBytes("% 넓이 계산\r\nA = w*h;\r\n"));
        var workspace = Workspace();

        Assert.Equal("% 넓이 계산\nA = w*h;\n", workspace.ReadText("a.m"));

        workspace.WriteText("a.m", "% 부피 계산\nV = w*h*d;\n");
        Assert.Equal("% 부피 계산\r\nV = w*h*d;\r\n", cp949.GetString(File.ReadAllBytes(path)));
    }

    [Fact]
    public void Cp949File_WithUnencodableCharacter_RefusesInsteadOfWritingQuestionMarks()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var original = Encoding.GetEncoding(949).GetBytes("% 넓이\r\n");
        var path = _folder.Bytes("a.m", original);

        Assert.Throws<AgentToolException>(() => Workspace().WriteText("a.m", "% 넓이 🎯\n"));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Utf8BomFile_KeepsBom()
    {
        var path = _folder.Bytes("a.m", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("x = 1;\n")]);
        var workspace = Workspace();

        Assert.Equal("x = 1;\n", workspace.ReadText("a.m"));
        workspace.WriteText("a.m", "x = 2;\n");

        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("x = 2;\n")], File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteText_NewFile_IsUtf8WithoutBom_AndCrlf_AndCreatesFolders()
    {
        Workspace().WriteText("sub/new.m", "% 새 함수\nf = 1;\n");

        var bytes = File.ReadAllBytes(Path.Combine(_folder.Path, "sub", "new.m"));
        Assert.Equal(Encoding.UTF8.GetBytes("% 새 함수\r\nf = 1;\r\n"), bytes);
    }

    [Fact]
    public void ReadText_BinaryOrMissingOrDirectory_Throws()
    {
        _folder.Bytes("a.mat", [0x4D, 0x41, 0x00, 0x01]);
        Directory.CreateDirectory(Path.Combine(_folder.Path, "dir"));
        var workspace = Workspace();

        Assert.Throws<AgentToolException>(() => workspace.ReadText("a.mat"));
        Assert.Throws<AgentToolException>(() => workspace.ReadText("missing.m"));
        Assert.Throws<AgentToolException>(() => workspace.ReadText("dir"));
    }
}
