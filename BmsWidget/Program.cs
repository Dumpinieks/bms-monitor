namespace BmsWidget;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Only one instance: the BMS accepts a single Bluetooth connection anyway.
        using var mutex = new Mutex(true, @"Local\BmsWidget", out var isFirst);
        if (!isFirst)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new WidgetContext(WidgetSettings.Load()));
    }
}
