using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TaskReminderTray.Services;

internal enum TrayIconState
{
    Loading,
    Healthy,
    Warning,
    Error
}

internal static class TrayIconRenderer
{
    public static Icon Create(int? count, TrayIconState state)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var background = state switch
        {
            TrayIconState.Healthy => Color.FromArgb(40, 146, 82),
            TrayIconState.Warning => Color.FromArgb(218, 135, 29),
            TrayIconState.Error => Color.FromArgb(196, 53, 63),
            _ => Color.FromArgb(62, 112, 194)
        };
        using (var brush = new SolidBrush(background))
        {
            graphics.FillEllipse(brush, 1, 1, 30, 30);
        }

        var text = count is null ? "…" : count > 99 ? "99+" : count.Value.ToString();
        using var font = new Font("Segoe UI", text.Length switch
        {
            <= 2 => 15F,
            _ => 10F
        }, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString(text, font, textBrush, new RectangleF(0, 0, size, size), format);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
