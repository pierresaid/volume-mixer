using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace VolumeMixer.Tray;

/// <summary>
/// The pairing window: a QR code the phone's built-in camera app can open directly, plus a
/// live status of the usual blockers (which network, firewall) and a confirmation once a phone
/// connects.
/// </summary>
public sealed class QrForm : Form
{
    private static readonly Color Dim = Color.FromArgb(170, 178, 192);
    private static readonly Color Amber = Color.FromArgb(240, 166, 60);
    private static readonly Color Green = Color.FromArgb(111, 207, 130);

    private readonly int _port;
    private readonly PictureBox _qrPicture;
    private readonly TextBox _linkBox;
    private readonly Label _networkLabel;
    private readonly Panel _firewallRow;
    private readonly Label _connectedLabel;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private bool _checkingFirewall;
    private string _link = "";

    public QrForm(int port)
    {
        _port = port;

        Text = "Volume Mixer - connect your phone";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 590);
        BackColor = Color.FromArgb(15, 17, 21);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);
        Icon = TrayIconFactory.Create();

        var title = new Label
        {
            Text = "Scan with your phone's camera",
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(10, 18, 340, 30),
        };

        _qrPicture = new PictureBox
        {
            Bounds = new Rectangle(40, 60, 280, 280),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
        };

        _linkBox = new TextBox
        {
            ReadOnly = true,
            Bounds = new Rectangle(24, 358, 240, 28),
            BackColor = Color.FromArgb(30, 33, 40),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var copyButton = MakeButton("Copy", new Rectangle(272, 356, 64, 28));
        copyButton.Click += (_, _) => { try { Clipboard.SetText(_link); } catch { } };

        // live status, then the hints: one column that collapses to whatever applies right now
        var column = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Bounds = new Rectangle(24, 400, 320, 200),
        };

        _networkLabel = new Label { AutoSize = true, MaximumSize = new Size(320, 0), ForeColor = Dim, Margin = new Padding(0, 0, 0, 8) };

        _firewallRow = new Panel { Size = new Size(320, 36), Margin = new Padding(0, 0, 0, 8), Visible = false };
        var firewallLabel = new Label
        {
            Text = "⚠  Windows Firewall is blocking phones on this network",
            ForeColor = Amber,
            AutoSize = false,
            Bounds = new Rectangle(0, 0, 244, 36),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var fixButton = MakeButton("Fix", new Rectangle(256, 4, 64, 28));
        fixButton.Click += (_, _) => Firewall.Repair(); // asks for admin; the next status check picks up the result
        _firewallRow.Controls.AddRange([firewallLabel, fixButton]);

        _connectedLabel = new Label { AutoSize = true, ForeColor = Green, Margin = new Padding(0, 0, 0, 8), Visible = false };

        var hints = new Label
        {
            Text = "•  If Windows Firewall asks, click “Allow”\n" +
                   "•  Router “AP / client isolation” must be off\n" +
                   "•  If the link ever stops working, just re-scan",
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            ForeColor = Dim,
            Margin = new Padding(0, 6, 0, 0),
        };

        column.Controls.AddRange([_networkLabel, _firewallRow, _connectedLabel, hints]);
        Controls.AddRange([title, _qrPicture, _linkBox, copyButton, column]);

        _statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                RefreshStatus();
                _statusTimer.Start();
            }
            else
            {
                _statusTimer.Stop();
                _connectedLabel.Visible = false; // a fresh confirmation for the next pairing
            }
        };
    }

    public void SetLink(string link)
    {
        _link = link;
        _linkBox.Text = link;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
        var pngBytes = new PngByteQRCode(data).GetGraphic(10);
        using var stream = new MemoryStream(pngBytes);
        using var image = Image.FromStream(stream);
        _qrPicture.Image?.Dispose();
        _qrPicture.Image = new Bitmap(image);
    }

    /// <summary>A phone just connected ("An iPhone", "An Android phone", ...).</summary>
    public void ShowConnected(string who)
    {
        _connectedLabel.Text = $"✓  {who} connected";
        _connectedLabel.Visible = true;
    }

    private async void RefreshStatus()
    {
        var network = Net.DescribeNetwork();
        _networkLabel.Text = network is null
            ? "This PC isn't connected to a network"
            : $"Your phone must be on {network}";

        if (_checkingFirewall) return;
        _checkingFirewall = true;
        try
        {
            var firewall = await Task.Run(() => Firewall.Check(_port));
            if (!IsDisposed) _firewallRow.Visible = firewall == Firewall.Status.Blocked;
        }
        finally
        {
            _checkingFirewall = false;
        }
    }

    private static Button MakeButton(string text, Rectangle bounds)
    {
        var button = new Button
        {
            Text = text,
            Bounds = bounds,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(80, 86, 100);
        return button;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // keep the form around for instant re-open from the tray
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _statusTimer.Dispose();
        base.Dispose(disposing);
    }
}
