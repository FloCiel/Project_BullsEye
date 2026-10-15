namespace BullsEye.Core;

/// <summary>
/// 에이전트가 파일을 처음 바꾸기 직전의 원본을 USB(exe 옆)의 백업 폴더에 복사해 둔다.
/// 작업 폴더에는 아무것도 남기지 않는다 (학교 PC에 흔적이 남지 않게).
/// 세션 하나에 폴더 하나(<c>시각 작업폴더이름</c>), 파일마다 한 번만: 되돌려 놓으면 세션 시작 전 상태가 된다.
/// </summary>
public sealed class AgentBackup
{
    /// <summary>exe 옆에 만드는 백업 폴더 이름.</summary>
    public const string FolderName = "BullsEye-backup";

    /// <summary>각 세션 폴더에 원래 작업 폴더 경로를 적어 두는 파일 (어디로 되돌릴지 알 수 있게).</summary>
    public const string SourceFileName = "원래 위치.txt";

    private readonly AgentWorkspace _workspace;
    private readonly HashSet<string> _saved = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="backupRoot">세션 폴더들을 만들 곳 (앱은 exe 옆의 <see cref="FolderName"/>).</param>
    public AgentBackup(AgentWorkspace workspace, string backupRoot, DateTimeOffset startedAt)
    {
        _workspace = workspace;
        var name = Path.GetFileName(workspace.Root);
        var stamp = startedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        Folder = Path.Combine(backupRoot, string.IsNullOrEmpty(name) ? stamp : $"{stamp} {name}");
    }

    /// <summary>이번 세션의 백업 폴더. 아직 아무것도 백업하지 않았으면 없다.</summary>
    public string Folder { get; }

    /// <summary>원본을 복사한 파일 수 (새로 만든 파일은 원본이 없어 세지 않는다).</summary>
    public int Count { get; private set; }

    /// <summary>쓰기 직전에 부른다. 이미 있는 파일이고 이번 세션에서 처음이면 원본 바이트를 그대로 복사한다.</summary>
    public void Save(string relative)
    {
        if (!_saved.Add(relative)) return;

        var source = _workspace.Resolve(relative);
        if (!File.Exists(source)) return;

        var target = Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        if (Count++ == 0) File.WriteAllText(Path.Combine(Folder, SourceFileName), _workspace.Root + Environment.NewLine);
    }
}
