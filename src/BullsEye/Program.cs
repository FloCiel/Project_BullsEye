using BullsEye.Core;
using BullsEye.Diagnostics;

namespace BullsEye;

internal static class Program
{
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

        // M5에서 트레이 상주 ApplicationContext로 교체
        Application.Run(new Form { Text = "BullsEye" });
    }
}
