using System.Windows.Forms;
using VolumeMixer.Audio;
using VolumeMixer.Tray;
using VolumeMixer.Web;

namespace VolumeMixer;

static class Program
{
    private const string MutexName = @"Local\VolumeMixer.SingleInstance";
    private const string ShowSignalName = @"Local\VolumeMixer.ShowQr";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        if (!isFirstInstance)
        {
            // Another instance is running: ask it to show its QR window and leave.
            showSignal.Set();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settings = Settings.Load();
        var worker = new AudioWorker();
        var host = new ServerHost();
        try
        {
            host.StartAsync(settings, worker).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The server could not start:\n\n{ex.Message}", "Volume Mixer",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        worker.Start(); // start after the hub broadcast wiring exists, so no event is missed

        using var tray = new TrayContext(settings, host, showSignal);
        Application.Run(tray);

        host.StopAsync().GetAwaiter().GetResult();
        worker.Dispose();
    }
}
