using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace BmsWidget;

/// <summary>Draws the battery percentage as the notification-area icon.</summary>
static class TrayIconRenderer
{
    public static Icon Render(string text, Color color)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // Largest bold font that fits the icon, so "8" is big and "100" still fits.
            var fontSize = size * 0.95f;
            Font font;
            SizeF measured;
            using var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            while (true)
            {
                font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                measured = g.MeasureString(text, font, PointF.Empty, format);
                if (measured.Width <= size + 1 || fontSize < 6)
                    break;
                font.Dispose();
                fontSize -= 0.5f;
            }
            using (font)
            using (var brush = new SolidBrush(color))
                g.DrawString(text, font, brush, new RectangleF(0, 0, size, size + 1), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
