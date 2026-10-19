using BullsEye.Core;
using BullsEye.Diagnostics;
using BullsEye.Platform;
using BullsEye.Ui;

namespace BullsEye;

internal static class Program
{
    /// <summary>exe 옆 (USB). 단일 파일 배포에서도 exe가 있는 폴더를 가리킨다.</summary>
    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "BullsEye.dat");

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Shutdown.InstallCrashHandlers();

        // BullsEye.exe --probe [단축키]  : Win32 레이어 진단
        if (args.Length > 0 && args[0] == "--probe")
        {
            Application.Run(new ProbeForm(args.Length > 1 ? args[1] : new Settings().Hotkey));
            return;
        }

#if DEBUG
        // BullsEye.exe --agent-demo <폴더> : PIN·키 없이 가짜 provider로 에이전트 창만 띄운다 (개발용).
        if (args.Length > 1 && args[0] == "--agent-demo")
        {
            var models = new[] { "mock/agent", "mock/other" };
            Application.Run(new AgentForm(
                new MockProvider(TimeSpan.FromMilliseconds(150)),
                new AgentOptions(models[0], DenyDataCollection: true, ReasoningEffort.Medium),
                models,
                args[1],
                () => { },
                _ => null,
                Path.Combine(AppContext.BaseDirectory, AgentBackup.FolderName),
                Path.Combine(AppContext.BaseDirectory, AgentLog.FolderName)));
            return;
        }
#endif

        // 이미 실행 중이면 그쪽 설정 창을 띄우게 하고 끝낸다.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null) return;

        // USB가 빠지면 즉시 종료 (메모리의 키도 함께 사라진다).
        using var usb = new UsbWatcher();
        usb.Removed += (_, _) => Shutdown.Emergency();

        var store = new SettingsStore(SettingsPath);
        // 타임아웃은 OpenRouterProvider가 관리한다 (긴 추론 응답이 100초 기본값에 끊기지 않게).
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var catalog = new ModelCatalog(http);
        var chatGpt = new ChatGptServices(new ChatGptAuth(http, TimeProvider.System), new ChatGptModelCatalog(http));

        // 실행할 때마다 설정 창 (잠금 해제) → 닫으면 트레이로.
        using var settings = new SettingsForm(store, catalog, chatGpt, store.Exists ? SettingsMode.Unlock : SettingsMode.FirstRun);
        Action onSecondLaunch = () => settings.Activate();
        instance.Listen(SynchronizationContext.Current!, () => onSecondLaunch());

        if (settings.ShowDialog() != DialogResult.OK || settings.Result is null || settings.Pin is null) return;

        var tray = new TrayApplicationContext(store, http, catalog, chatGpt, settings.Result, settings.Pin);
        onSecondLaunch = tray.OpenSettings;
        Application.Run(tray);
    }
}
