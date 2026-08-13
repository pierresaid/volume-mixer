using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace VolumeMixer.Tray;

/// <summary>
/// The pairing window: a QR code the phone's built-in camera app can open directly.
/// </summary>
public sealed class QrForm : Form
{
    private readonly PictureBox _qrPicture;
    private readonly TextBox _linkBox;
    private string _link = "";

    public QrForm()
    {
        Text = "Volume Mixer - connect your phone";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 560);
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

        var copyButton = new Button
        {
            Text = "Copy",
            Bounds = new Rectangle(272, 356, 64, 28),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
        };
        copyButton.FlatAppearance.BorderColor = Color.FromArgb(80, 86, 100);
        copyButton.Click += (_, _) => { try { Clipboard.SetText(_link); } catch { } };

        var hints = new Label
        {
            Text = "•  The phone must be on the same Wi-Fi as this PC\n" +
                   "•  If Windows Firewall asks, click “Allow”\n" +
                   "•  Router “AP / client isolation” must be off\n" +
                   "•  If the link ever stops working, just re-scan",
            AutoSize = false,
            Bounds = new Rectangle(24, 400, 320, 130),
            ForeColor = Color.FromArgb(170, 178, 192),
        };

        Controls.AddRange([title, _qrPicture, _linkBox, copyButton, hints]);
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // keep the form around for instant re-open from the tray
            Hide();
        }
        base.OnFormClosing(e);
    }
}
