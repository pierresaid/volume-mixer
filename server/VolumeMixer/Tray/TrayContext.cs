using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Forms;
using VolumeMixer.Web;

namespace VolumeMixer.Tray;

public sealed class TrayContext : ApplicationContext
{
    private readonly Settings _settings;
    private readonly ServerHost _host;
    private readonly NotifyIcon _icon;
    private readonly Control _uiMarshal; // hidden control so background signals can reach the UI thread
    private readonly System.Windows.Forms.Timer _networkDebounce;
    private QrForm? _qrForm;
    private int _connectedCount;

    public TrayContext(Settings settings, ServerHost host, EventWaitHandle showSignal)
    {
        _settings = settings;
        _host = host;

        _uiMarshal = new Control();
        _ = _uiMarshal.Handle; // force handle creation on the UI thread

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show QR code", null, (_, _) => ShowQr());
        menu.Items.Add("Copy phone link", null, (_, _) => CopyLink());
        menu.Items.Add("Open on this PC", null, (_, _) => OpenLocal());
        menu.Items.Add(new ToolStripSeparator());
        var autostartItem = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled, CheckOnClick = true };
        autostartItem.CheckedChanged += (_, _) => Autostart.SetEnabled(autostartItem.Checked);
        menu.Items.Add(autostartItem);
        menu.Items.Add("Fix firewall (admin)…", null, (_, _) => Firewall.Repair());
        menu.Items.Add("Reset pairing…", null, (_, _) => ResetPairing());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(32),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowQr();
        };
        UpdateTooltip();

        // a second launched instance signals us to pop the QR window
        ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => _uiMarshal.BeginInvoke(ShowQr), null, -1, executeOnlyOnce: false);

        // phones joining/leaving update the tooltip count quietly; no balloons. An open QR window
        // confirms the connection, which is the "it worked" moment of pairing.
        _host.Connections.CountChanged += count => _uiMarshal.BeginInvoke(() =>
        {
            _connectedCount = count;
            UpdateTooltip();
        });
        _host.Connections.Joined += (who, _) => _uiMarshal.BeginInvoke(() =>
        {
            if (_qrForm is { IsDisposed: false, Visible: true })
                _qrForm.ShowConnected(who);
        });

        // the PC changed networks / got a new address -> new QR + tell the user
        _networkDebounce = new System.Windows.Forms.Timer { Interval = 2000 };
        _networkDebounce.Tick += (_, _) =>
        {
            _networkDebounce.Stop();
            if (_host.RefreshLanUrl())
            {
                UpdateTooltip();
                if (_qrForm is { IsDisposed: false })
                    _qrForm.SetLink(_host.PhoneUrl);
                _icon.ShowBalloonTip(8000, "Volume Mixer",
                    "This PC's network address changed. Phones need to re-scan the QR code.",
                    ToolTipIcon.Warning);
            }
        };
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        if (_host.Port != _settings.Port)
        {
            _icon.ShowBalloonTip(8000, "Volume Mixer",
                $"Port {_settings.Port} was busy, using {_host.Port} instead. Phones need to re-scan the QR code.",
                ToolTipIcon.Warning);
        }

        if (!_settings.FirstRunCompleted)
        {
            _settings.FirstRunCompleted = true;
            _settings.Save();
            ShowQr();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        _uiMarshal.BeginInvoke(() =>
        {
            _networkDebounce.Stop();
            _networkDebounce.Start();
        });

    private void UpdateTooltip()
    {
        var suffix = _connectedCount > 0 ? $" - {_connectedCount} connected" : "";
        _icon.Text = Truncate($"Volume Mixer - {_host.LanUrl}{suffix}", 63);
    }

    private void ShowQr()
    {
        if (_qrForm is null || _qrForm.IsDisposed)
            _qrForm = new QrForm(_host.Port);
        _qrForm.SetLink(_host.PhoneUrl);
        _qrForm.Show();
        _qrForm.WindowState = FormWindowState.Normal;
        _qrForm.Activate();
    }

    private void CopyLink()
    {
        try
        {
            Clipboard.SetText(_host.PhoneUrl);
            _icon.ShowBalloonTip(3000, "Volume Mixer", "Phone link copied to clipboard.", ToolTipIcon.Info);
        }
        catch { }
    }

    private void OpenLocal()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_host.LocalUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void ResetPairing()
    {
        var confirm = MessageBox.Show(
            "Reset pairing?\n\nAll connected phones will be disconnected and every previously scanned QR code stops working.",
            "Volume Mixer", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        _settings.RotateToken();
        if (_qrForm is { IsDisposed: false, Visible: true })
            _qrForm.SetLink(_host.PhoneUrl);
        _icon.ShowBalloonTip(4000, "Volume Mixer", "Pairing reset. Scan the new QR code on your phone.", ToolTipIcon.Info);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _networkDebounce.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _qrForm?.Dispose();
            _uiMarshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
