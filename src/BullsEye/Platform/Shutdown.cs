namespace BullsEye.Platform;

/// <summary>
/// 어떤 경로로 끝나든 (정상 종료, USB 제거, 예상치 못한 오류) 흔적 정리를 한 번만 실행한다.
/// </summary>
internal static class Shutdown
{
    private static readonly List<Action> Cleanups = [];
    private static int _ran;

    public static void Register(Action cleanup) => Cleanups.Add(cleanup);

    public static void RunCleanups()
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1) return;
        foreach (var cleanup in Cleanups)
        {
            try
            {
                cleanup();
            }
            catch (Exception)
            {
                // 정리 중 실패는 무시하고 나머지를 계속한다 (종료가 우선).
            }
        }
    }

    /// <summary>
    /// 처리되지 않은 예외를 잡아 정리 후 종료한다. Environment.Exit은 Windows 오류 보고(크래시 덤프)를
    /// 거치지 않으므로 메모리의 API 키가 덤프 파일로 남지 않는다.
    /// </summary>
    public static void InstallCrashHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
    }

    /// <summary>USB가 빠짐: 저장할 곳이 없으므로 정리만 하고 바로 끝낸다.</summary>
    public static void Emergency()
    {
        RunCleanups();
        Environment.Exit(2);
    }

    private static void Crash(Exception? exception)
    {
        RunCleanups();
        try
        {
            MessageBox.Show(
                $"예상치 못한 오류로 BullsEye를 종료합니다.\n\n{exception?.GetType().Name}: {exception?.Message}",
                "BullsEye",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Environment.Exit(1);
        }
    }
}
