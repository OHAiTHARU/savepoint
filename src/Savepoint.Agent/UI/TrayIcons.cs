using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Savepoint.Agent.Services;

namespace Savepoint.Agent.UI;

/// <summary>Tray icons drawn at runtime: a save glyph with a status badge.</summary>
public static class TrayIcons
{
    private static readonly Dictionary<AgentStatus, Icon> Cache = new();

    public static Icon For(AgentStatus status)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(status, out var icon))
            {
                icon = Draw(status);
                Cache[status] = icon;
            }
            return icon;
        }
    }

    public static Icon App => For(AgentStatus.Idle);

    private static Icon Draw(AgentStatus status)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var body = status is AgentStatus.Paused or AgentStatus.NotConfigured
                ? Color.FromArgb(120, 128, 140)
                : Color.FromArgb(37, 99, 235);

            using (var path = RoundedRect(new RectangleF(2, 2, 28, 28), 6))
            using (var brush = new SolidBrush(body))
                g.FillPath(brush, path);

            // Floppy: label slot and shutter.
            using (var white = new SolidBrush(Color.White))
            {
                g.FillRectangle(white, 9, 6, 12, 7);
                g.FillEllipse(white, 11, 16, 10, 10);
            }
            using (var bodyBrush = new SolidBrush(body))
                g.FillEllipse(bodyBrush, 14, 19, 4, 4);

            Color? badge = status switch
            {
                AgentStatus.Syncing => Color.FromArgb(56, 189, 248),
                AgentStatus.Offline => Color.FromArgb(148, 163, 184),
                AgentStatus.Attention => Color.FromArgb(245, 158, 11),
                AgentStatus.NotConfigured => Color.FromArgb(245, 158, 11),
                _ => null,
            };
            if (badge is not null)
            {
                using var outline = new SolidBrush(Color.White);
                using var fill = new SolidBrush(badge.Value);
                g.FillEllipse(outline, 18, 18, 14, 14);
                g.FillEllipse(fill, 20, 20, 10, 10);
            }
        }
        var hicon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hicon).Clone(); }
        finally { DestroyIcon(hicon); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
