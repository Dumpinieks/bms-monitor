using Microsoft.Win32;
using static BmsWidget.NativeMethods;

namespace BmsWidget;

/// <param name="PowerShort">Compact power text for a vertical taskbar, e.g. "197W".</param>
/// <param name="DetailShort">Compact detail text for a vertical taskbar, e.g. "4h26m".</param>
sealed record PanelContent(string Percent, string Power, string Detail, string PowerShort, string DetailShort, Level Level, bool Stale);

/// <summary>
/// A small always-on-top window laid over the taskbar (Windows 11 has no taskbar widget API).
/// On a horizontal taskbar it shows two lines and can be dragged left/right; on a vertical one it shows
/// three compact lines and can be dragged up/down. The position is kept as a distance from the taskbar's
/// right (horizontal) or bottom (vertical) edge.
/// </summary>
sealed class TaskbarPanel : Form
{
    const int BasePadding = 8, BaseGap = 6, BaseMargin = 3;
    // Sized for these so the panel doesn't jitter as numbers change.
    const string WidestPercent = "100%", WidestPower = "8.88 kW", WidestDetail = "full in 88h 88m";
    // Typical values on a narrow vertical taskbar; rarer longer ones ("+1.2kW") are shrunk individually when drawn.
    static readonly string[] WidestVerticalLines = ["100%", "888W", "8h88m"];
    // Font pixel sizes at 96 DPI: bold percent, regular power, small detail.
    const float BoldSize = 13, RegularSize = 13, SmallSize = 12;

    readonly System.Windows.Forms.Timer _keepOnTop = new() { Interval = 500 };
    PanelContent _content = new("--%", "", "searching…", "", "…", Level.Unknown, Stale: true);
    Theme _theme = Theme.Current;
    Font? _bold, _regular, _small;
    int? _offsetFromRight, _offsetFromBottom;
    Rectangle _lastTaskbar;
    bool _vertical;
    bool _wantVisible;
    bool _dragging;
    Point _dragStartCursor, _dragStartLocation;

    /// <summary>Raised after the user drags the panel, with the new distance from the taskbar's right or bottom edge.</summary>
    public event Action<bool /*vertical*/, int>? OffsetChanged;

    public TaskbarPanel(int? offsetFromRight, int? offsetFromBottom)
    {
        _offsetFromRight = offsetFromRight;
        _offsetFromBottom = offsetFromBottom;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        TopMost = true;
        Text = "BMS";

        _keepOnTop.Tick += (_, _) => KeepOnTop();
        SystemEvents.UserPreferenceChanged += OnSystemChanged;
        SystemEvents.DisplaySettingsChanged += OnSystemChanged;
        CreateHandle();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var corner = DWMWCP_ROUNDSMALL;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    public void SetWanted(bool visible)
    {
        _wantVisible = visible;
        _keepOnTop.Enabled = visible;
        if (visible)
        {
            Relayout();
            KeepOnTop();
        }
        else
        {
            Hide();
        }
    }

    public void ResetPosition()
    {
        _offsetFromRight = null;
        _offsetFromBottom = null;
        Relayout();
    }

    public void SetContent(PanelContent content)
    {
        _content = content;
        Invalidate();
    }

    void OnSystemChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;
        BeginInvoke(() =>
        {
            _theme = Theme.Current;
            Relayout();
            Invalidate();
        });
    }

    float DpiScale => DeviceDpi / 96f;
    int Scaled(int value) => (int)(value * DpiScale);

    void SetFonts(float factor)
    {
        _bold?.Dispose();
        _regular?.Dispose();
        _small?.Dispose();
        _bold = new Font("Segoe UI Semibold", BoldSize * DpiScale * factor, GraphicsUnit.Pixel);
        _regular = new Font("Segoe UI", RegularSize * DpiScale * factor, GraphicsUnit.Pixel);
        _small = new Font("Segoe UI", SmallSize * DpiScale * factor, GraphicsUnit.Pixel);
    }

    static Rectangle? TaskbarBounds() => GetWindowRectangle(FindWindow("Shell_TrayWnd", null));

    static Rectangle? TrayBounds() =>
        GetWindowRectangle(FindWindowEx(FindWindow("Shell_TrayWnd", null), IntPtr.Zero, "TrayNotifyWnd", null));

    void Relayout()
    {
        if (TaskbarBounds() is not { Width: > 0, Height: > 0 } taskbar)
            return;
        _lastTaskbar = taskbar;
        _vertical = taskbar.Height > taskbar.Width;
        if (_vertical)
            LayoutVertical(taskbar);
        else
            LayoutHorizontal(taskbar);
        Invalidate();
    }

    void LayoutHorizontal(Rectangle taskbar)
    {
        SetFonts(1);
        var padding = Scaled(BasePadding);
        var line1 = TextRenderer.MeasureText(WidestPercent, _bold).Width + Scaled(BaseGap) + TextRenderer.MeasureText(WidestPower, _regular).Width;
        var line2 = TextRenderer.MeasureText(WidestDetail, _small).Width;
        var width = Math.Min(taskbar.Width, Math.Max(line1, line2) + padding * 2);
        var height = Math.Min(taskbar.Height - Scaled(2 * BaseMargin), _bold!.Height + _small!.Height + Scaled(6));

        // Default: just left of the notification area (clock and tray icons).
        var offset = _offsetFromRight
            ?? (TrayBounds() is { Width: > 0 } tray && tray.Left > taskbar.Left ? taskbar.Right - tray.Left + Scaled(8) : Scaled(320));
        var x = Math.Clamp(taskbar.Right - offset - width, taskbar.Left, taskbar.Right - width);
        var y = taskbar.Top + (taskbar.Height - height) / 2;
        Bounds = new Rectangle(x, y, width, height);
    }

    void LayoutVertical(Rectangle taskbar)
    {
        var width = taskbar.Width - Scaled(2 * BaseMargin);
        var inner = width - Scaled(4);

        // Shrink the fonts until the widest compact lines fit the taskbar's width.
        var factor = 1f;
        while (true)
        {
            SetFonts(factor);
            var widest = Math.Max(TextRenderer.MeasureText(WidestVerticalLines[0], _bold).Width,
                Math.Max(TextRenderer.MeasureText(WidestVerticalLines[1], _regular).Width,
                         TextRenderer.MeasureText(WidestVerticalLines[2], _small).Width));
            if (widest <= inner || factor <= 0.5f)
                break;
            factor -= 0.05f;
        }
        var height = _bold!.Height + _regular!.Height + _small!.Height + Scaled(8);

        // Default: just above the notification area.
        var offset = _offsetFromBottom
            ?? (TrayBounds() is { Height: > 0 } tray && tray.Top > taskbar.Top ? taskbar.Bottom - tray.Top + Scaled(8) : Scaled(320));
        var x = taskbar.Left + (taskbar.Width - width) / 2;
        var y = Math.Clamp(taskbar.Bottom - offset - height, taskbar.Top, Math.Max(taskbar.Top, taskbar.Bottom - height));
        Bounds = new Rectangle(x, y, width, height);
    }

    void KeepOnTop()
    {
        if (!_wantVisible || _dragging)
            return;

        // Get out of the way of full-screen video, games and presentations, like the taskbar does.
        var fullScreen = SHQueryUserNotificationState(out var state) == 0 &&
                         state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        if (fullScreen)
        {
            Hide();
            return;
        }
        // Moving or resizing the taskbar doesn't reliably raise a system event, so poll its bounds.
        if (!Visible || TaskbarBounds() is { } taskbar && taskbar != _lastTaskbar)
            Relayout();
        if (!Visible)
            Show();
        // The taskbar is topmost too and comes back above us whenever it's clicked.
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_bold is null || _regular is null || _small is null)
            return;
        var g = e.Graphics;
        g.Clear(_theme.Background);

        var percentColor = _content.Stale ? _theme.SecondaryText : _theme.For(_content.Level);
        var textColor = _content.Stale ? _theme.SecondaryText : _theme.Text;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        if (_vertical)
        {
            var top = (Height - _bold.Height - _regular.Height - _small.Height) / 2;
            DrawCentered(_content.Percent, _bold, percentColor, top);
            DrawCentered(_content.PowerShort, _regular, textColor, top + _bold.Height);
            DrawCentered(_content.DetailShort, _small, _theme.SecondaryText, top + _bold.Height + _regular.Height);
        }
        else
        {
            var padding = Scaled(BasePadding);
            var top = (Height - _bold.Height - _small.Height) / 2;
            TextRenderer.DrawText(g, _content.Percent, _bold, new Point(padding, top), percentColor, _theme.Background, flags);
            var percentWidth = TextRenderer.MeasureText(g, _content.Percent, _bold, Size.Empty, flags).Width;
            TextRenderer.DrawText(g, _content.Power, _regular, new Point(padding + percentWidth + Scaled(BaseGap), top), textColor, _theme.Background, flags);
            TextRenderer.DrawText(g, _content.Detail, _small, new Point(padding, top + _bold.Height), _theme.SecondaryText, _theme.Background, flags);
        }

        void DrawCentered(string text, Font font, Color color, int y)
        {
            var bounds = new Rectangle(0, y, Width, font.Height);
            var available = Width - Scaled(4);
            var measured = TextRenderer.MeasureText(g, text, font, Size.Empty, flags).Width;
            if (measured <= available)
            {
                TextRenderer.DrawText(g, text, font, bounds, color, _theme.Background, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            using var smaller = new Font(font.FontFamily, font.Size * available / measured, font.Style, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, text, smaller, bounds, color, _theme.Background, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        _dragging = true;
        _dragStartCursor = Cursor.Position;
        _dragStartLocation = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || TaskbarBounds() is not { } taskbar)
            return;
        var cursor = Cursor.Position;
        if (_vertical)
            Top = Math.Clamp(_dragStartLocation.Y + cursor.Y - _dragStartCursor.Y, taskbar.Top, Math.Max(taskbar.Top, taskbar.Bottom - Height));
        else
            Left = Math.Clamp(_dragStartLocation.X + cursor.X - _dragStartCursor.X, taskbar.Left, Math.Max(taskbar.Left, taskbar.Right - Width));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging)
            return;
        _dragging = false;
        if (Location == _dragStartLocation || TaskbarBounds() is not { } taskbar)
            return;
        if (_vertical)
        {
            _offsetFromBottom = taskbar.Bottom - Bottom;
            OffsetChanged?.Invoke(true, _offsetFromBottom.Value);
        }
        else
        {
            _offsetFromRight = taskbar.Right - Right;
            OffsetChanged?.Invoke(false, _offsetFromRight.Value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnSystemChanged;
            SystemEvents.DisplaySettingsChanged -= OnSystemChanged;
            _keepOnTop.Dispose();
            _bold?.Dispose();
            _regular?.Dispose();
            _small?.Dispose();
        }
        base.Dispose(disposing);
    }
}
