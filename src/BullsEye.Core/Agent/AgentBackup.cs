namespace BullsEye.Core;

/// <summary>
/// 에이전트가 파일을 처음 바꾸기 직전의 원본을 작업 폴더의 <c>.bullseye-backup/시각/</c>에 복사해 둔다.
/// 세션 하나에 폴더 하나, 파일마다 한 번만: 이 폴더의 파일을 되돌려 놓으면 세션 시작 전 상태가 된다.
/// </summary>
public sealed class AgentBackup
{
    public const string FolderName = ".bullseye-backup";

    private readonly AgentWorkspace _workspace;
    private readonly HashSet<string> _saved = new(StringComparer.OrdinalIgnoreCase);

    public AgentBackup(AgentWorkspace workspace, DateTimeOffset startedAt)
    {
        _workspace = workspace;
        Folder = Path.Combine(workspace.Root, FolderName, startedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss"));
    }

    /// <summary>이번 세션의 백업 폴더. 아직 아무것도 백업하지 않았으면 없을 수 있다.</summary>
    public string Folder { get; }

    /// <summary>원본을 복사한 파일 수 (새로 만든 파일은 원본이 없어 세지 않는다).</summary>
    public int Count { get; private set; }

    /// <summary>백업 폴더 안의 경로인지 (에이전트가 백업을 고치지 못하게).</summary>
    public static bool IsBackupPath(string relative) =>
        relative.Equals(FolderName, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(FolderName + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>쓰기 직전에 부른다. 이미 있는 파일이고 이번 세션에서 처음이면 원본 바이트를 그대로 복사한다.</summary>
    public void Save(string relative)
    {
        if (!_saved.Add(relative)) return;

        var source = _workspace.Resolve(relative);
        if (!File.Exists(source)) return;

        var target = Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        HideRoot();
        File.Copy(source, target, overwrite: true);
        Count++;
    }

    // MATLAB 경로·탐색기·list_files에 끼어들지 않게 숨김으로 둔다.
    private void HideRoot()
    {
        var root = new DirectoryInfo(Path.Combine(_workspace.Root, FolderName));
        if (!root.Attributes.HasFlag(FileAttributes.Hidden)) root.Attributes |= FileAttributes.Hidden;
    }
}
