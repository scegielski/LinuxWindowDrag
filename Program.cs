namespace LinuxWindowDrag;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\LinuxWindowDrag.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Linux Window Drag is already running. Use the tray icon to exit the existing instance first.",
                "Linux Window Drag",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new LinuxDragApplicationContext());
    }
}
