namespace BullsEye;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        // M5에서 트레이 상주 ApplicationContext로 교체
        Application.Run(new Form { Text = "BullsEye" });
    }
}
