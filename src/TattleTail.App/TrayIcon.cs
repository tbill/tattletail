using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TattleTail.App;

/// <summary>
/// TattleTail's icon in the system tray (the row of small icons next to the clock). It is how
/// you get the window back after closing it, shows current speeds when hovered, and pops up
/// alerts.
/// </summary>
/// <remarks>
/// WPF has no tray icon of its own, so this borrows the one from Windows Forms, which ships
/// with .NET.
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    // Windows shows at most this much of a tray tooltip; NotifyIcon throws on longer text.
    private const int MaxTooltipLength = 127;

    private readonly NotifyIcon _icon;
    private readonly Icon _image;

    public TrayIcon()
    {
        _image = DrawImage();

        var open = new ToolStripMenuItem("Open TattleTail");
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        open.Click += (_, _) => OpenRequested?.Invoke();
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = _image,
            Text = "TattleTail",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenRequested?.Invoke();
            }
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>The user clicked the icon, an alert, or "Open TattleTail".</summary>
    public event Action? OpenRequested;

    /// <summary>The user chose "Exit".</summary>
    public event Action? ExitRequested;

    public bool Visible => _icon.Visible;

    /// <summary>Sets the text shown when the mouse rests on the icon.</summary>
    public void SetTooltip(string text)
    {
        if (text.Length > MaxTooltipLength)
        {
            text = text[..MaxTooltipLength];
        }

        if (_icon.Text != text)
        {
            _icon.Text = text;
        }
    }

    /// <summary>Pops up a notification next to the tray.</summary>
    public void ShowMessage(string title, string text) =>
        _icon.ShowBalloonTip(10_000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        // Hide first, or the icon lingers in the tray until the mouse passes over it.
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        DestroyIcon(_image.Handle);
        _image.Dispose();
    }

    /// <summary>
    /// Draws the icon: an orange up arrow and a blue down arrow, the same colours the chart
    /// uses for upload and download.
    /// </summary>
    private static Icon DrawImage()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var upload = new SolidBrush(ColorTranslator.FromHtml("#E8710A"));
            using var download = new SolidBrush(ColorTranslator.FromHtml("#1A73E8"));
            g.FillPolygon(upload, Arrow(left: 2, pointsUp: true));
            g.FillPolygon(download, Arrow(left: 17, pointsUp: false));
        }

        // The icon made from the handle doesn't own it, hence DestroyIcon in Dispose.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>A 13-pixel-wide arrow filling the icon's height.</summary>
    private static PointF[] Arrow(float left, bool pointsUp)
    {
        // Drawn pointing up, then flipped top-to-bottom for the down arrow.
        PointF[] points =
        [
            new(left + 6.5f, 2), new(left + 13, 13), new(left + 9, 13), new(left + 9, 30),
            new(left + 4, 30), new(left + 4, 13), new(left, 13),
        ];
        return pointsUp ? points : points.Select(p => new PointF(p.X, 32 - p.Y)).ToArray();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
