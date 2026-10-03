using BullsEye.Core;
using BullsEye.Diagnostics;
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

        // BullsEye.exe --probe [단축키]  : Win32 레이어 진단
        if (args.Length > 0 && args[0] == "--probe")
        {
            Application.Run(new ProbeForm(args.Length > 1 ? args[1] : new Settings().Hotkey));
            return;
        }

        var store = new SettingsStore(SettingsPath);
        // 타임아웃은 OpenRouterProvider가 관리한다 (긴 추론 응답이 100초 기본값에 끊기지 않게).
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var catalog = new ModelCatalog(http);

        // 실행할 때마다 설정 창 (잠금 해제) → 닫으면 트레이로.
        using var settings = new SettingsForm(store, catalog, store.Exists ? SettingsMode.Unlock : SettingsMode.FirstRun);
        if (settings.ShowDialog() != DialogResult.OK || settings.Result is null || settings.Pin is null) return;

        Application.Run(new TrayApplicationContext(store, http, catalog, settings.Result, settings.Pin));
    }
}
