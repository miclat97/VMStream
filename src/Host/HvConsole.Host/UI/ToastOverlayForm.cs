// ============================================================================
// ToastOverlayForm.cs - Non-Intrusive Corner Indicator Overlay
// Displays brief status notifications (e.g., Ctrl+Alt+Shift+M Mic Toggle)
// with WS_EX_NOACTIVATE so focus is never stolen from the Hyper-V VM Console.
// ============================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HvConsole.Host.UI;

public sealed class ToastOverlayForm : Form
{
    private const int WS_EX_TOPMOST    = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly System.Windows.Forms.Timer _hideTimer;
    private string _titleText = string.Empty;
    private string _subtitleText = string.Empty;
    private Color _accentColor = Color.FromArgb(56, 189, 248);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public ToastOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(18, 22, 31);
        Size = new Size(360, 72);
        Opacity = 0.94;

        _hideTimer = new System.Windows.Forms.Timer { Interval = 2200 };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    public void ShowToast(Form ownerWindow, string title, string subtitle, Color accentColor, int durationMs = 2200)
    {
        _titleText = title;
        _subtitleText = subtitle;
        _accentColor = accentColor;

        Rectangle bounds = ownerWindow.Bounds;
        Location = new Point(
            bounds.Right - Width - 28,
            bounds.Top + 28
        );

        _hideTimer.Stop();
        _hideTimer.Interval = durationMs;

        if (!Visible)
        {
            Show(ownerWindow);
        }
        Invalidate();
        _hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var borderPen = new Pen(_accentColor, 2f);
        g.DrawRectangle(borderPen, 1, 1, Width - 2, Height - 2);

        using var accentBrush = new SolidBrush(_accentColor);
        g.FillRectangle(accentBrush, 0, 0, 6, Height);

        using var titleFont = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold);
        using var subFont = new Font("Segoe UI", 9.0f, FontStyle.Regular);
        using var whiteBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var mutedBrush = new SolidBrush(Color.FromArgb(148, 163, 184));

        g.DrawString(_titleText, titleFont, whiteBrush, new PointF(18, 12));
        g.DrawString(_subtitleText, subFont, mutedBrush, new PointF(18, 38));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
