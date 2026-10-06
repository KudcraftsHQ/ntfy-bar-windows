using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>Draws the tray bell at the current DPI: outline when idle, filled with a badge when
/// there are unread messages (as in ntfy-bar), a slash on auth errors, dimmed while disconnected.
/// Drawn at runtime so it is crisp at every scale and follows the taskbar's light/dark theme.</summary>
internal static class TrayIcons
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static Icon Render(ConnectionState state, int unread, int size, bool lightTaskbar)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(size / 32f, size / 32f);

            var dim = state is ConnectionState.NotConfigured or ConnectionState.Connecting or ConnectionState.Reconnecting;
            var baseColor = lightTaskbar ? Color.FromArgb(32, 32, 32) : Color.White;
            var ink = dim ? Color.FromArgb(115, baseColor) : baseColor;

            using var bell = BellPath();
            if (unread > 0 && state != ConnectionState.AuthError)
            {
                using var fill = new SolidBrush(ink);
                g.FillPath(fill, bell);
            }
            else
            {
                using var pen = new Pen(ink, 2.6f) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, bell);
            }
            using (var brush = new SolidBrush(ink))
            {
                g.FillEllipse(brush, 12.5f, 25.5f, 7f, 4.5f); // clapper
                g.FillEllipse(brush, 14.2f, 1.8f, 3.6f, 3.6f); // knob
            }

            if (state == ConnectionState.AuthError)
            {
                using var cut = new Pen(lightTaskbar ? Color.White : Color.FromArgb(32, 32, 32), 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(cut, 4, 4, 28, 28);
                using var slash = new Pen(Color.FromArgb(232, 72, 64), 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(slash, 4, 4, 28, 28);
            }
            else if (unread > 0)
            {
                using var red = new SolidBrush(Color.FromArgb(232, 72, 64));
                if (size >= 24)
                {
                    var text = unread > 99 ? "99" : unread.ToString();
                    var w = text.Length > 1 ? 17f : 14f;
                    var rect = new RectangleF(32 - w, 0, w, 14f);
                    using var pill = Pill(rect);
                    g.FillPath(red, pill);
                    using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
                    using var white = new SolidBrush(Color.White);
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(text, font, white, new RectangleF(rect.X, rect.Y + 0.5f, rect.Width, rect.Height), sf);
                }
                else
                {
                    g.FillEllipse(red, 20, 0, 12, 12);
                }
            }
        }
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath BellPath()
    {
        var p = new GraphicsPath();
        p.StartFigure();
        p.AddLine(7.5f, 22.5f, 7.5f, 14f);
        p.AddArc(7.5f, 5f, 17f, 17f, 180, 180);
        p.AddLine(24.5f, 14f, 24.5f, 22.5f);
        p.AddLine(24.5f, 22.5f, 27.5f, 24.5f);
        p.AddLine(27.5f, 24.5f, 4.5f, 24.5f);
        p.AddLine(4.5f, 24.5f, 7.5f, 22.5f);
        p.CloseFigure();
        return p;
    }

    private static GraphicsPath Pill(RectangleF r)
    {
        var p = new GraphicsPath();
        var d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    /// <summary>Coloured circle with the topic's initial, for rows without an icon (as in ntfy-bar).</summary>
    public static Color TopicColor(string topic)
    {
        Color[] palette =
        {
            Color.FromArgb(0x2F, 0x80, 0xED), Color.FromArgb(0x27, 0xAE, 0x60), Color.FromArgb(0xEB, 0x57, 0x57),
            Color.FromArgb(0xF2, 0x99, 0x4A), Color.FromArgb(0x9B, 0x51, 0xE0), Color.FromArgb(0x13, 0x9E, 0xA0),
            Color.FromArgb(0xD1, 0x4D, 0x8C), Color.FromArgb(0x6D, 0x6F, 0xE0),
        };
        var h = 0u;
        foreach (var c in topic) h = h * 31 + c;
        return palette[h % (uint)palette.Length];
    }
}
