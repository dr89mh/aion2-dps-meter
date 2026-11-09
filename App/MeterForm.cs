using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DpsMeter.Meter;

namespace DpsMeter.App;

/// <summary>
/// The meter overlay: fight title with timer (click: choose a fight), tabs damage / healing / taken, one bar per player
/// (click: skill details). Borderless, always on top, never takes the focus from the game; drag by the header, resize at
/// the edges. Locked = clicks go through to the game.
/// </summary>
internal sealed class MeterForm : Form
{
    private readonly MeterEngine _engine;
    private readonly Settings _settings;
    private readonly Func<string?> _status;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private FightView? _view;
    private Font _fTitle = null!, _fText = null!, _fBold = null!, _fSmall = null!, _fIcon = null!;
    private float _k = 1;
    private int _scroll;
    private string? _hover;
    private Point? _pressAt;
    private string? _pressOn;

    public MeterForm(MeterEngine engine, Settings settings, Func<string?> status)
    {
        _engine = engine;
        _settings = settings;
        _status = status;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Back;
        Text = L.AppName;
        MinimumSize = new Size(200, 110);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Bounds = InitialBounds();
        Opacity = Math.Clamp(settings.Opacity, 30, 100) / 100.0;
        MakeFonts();
        _timer.Tick += (_, _) => Refresh(force: false);
        _timer.Start();
    }

    /// <summary>The fight on display: <see cref="MeterEngine.Follow"/>, <see cref="MeterEngine.Total"/> or a fight id.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int FightId { get; set; } = MeterEngine.Follow;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Mode Mode { get; set; } = Mode.Damage;

    /// <summary>Settings / main menu (built by the tray app).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ContextMenuStrip? MainMenu { get; set; }

    public event Action<int, RowView, Mode>? RowClicked;

    /// <summary>The view on display (for copying a summary).</summary>
    public FightView? CurrentView => _view;

    private Rectangle InitialBounds()
    {
        var w = Math.Max(200, _settings.MeterW);
        var h = Math.Max(110, _settings.MeterH);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var r = new Rectangle(_settings.MeterX, _settings.MeterY, w, h);
        if (_settings.MeterX < 0 || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)))
            r = new Rectangle(area.Right - w - 24, area.Top + area.Height / 4, w, h);
        return r;
    }

    public void SaveBounds()
    {
        if (WindowState != FormWindowState.Normal) return;
        _settings.MeterX = Left;
        _settings.MeterY = Top;
        _settings.MeterW = Width;
        _settings.MeterH = Height;
    }

    public void ApplySettings()
    {
        Opacity = Math.Clamp(_settings.Opacity, 30, 100) / 100.0;
        MakeFonts();
        ApplyLock();
        Refresh(force: true);
    }

    public void ApplyLock()
    {
        if (IsHandleCreated) Theme.ClickThrough(Handle, _settings.Locked);
        Invalidate();
    }

    private void MakeFonts()
    {
        _k = (float)(Math.Clamp(_settings.Scale, 0.7, 2.0) * DeviceDpi / 96.0);
        foreach (var f in new[] { _fTitle, _fText, _fBold, _fSmall, _fIcon }) f?.Dispose();
        _fTitle = new Font("Segoe UI", 12.5f * _k, FontStyle.Bold, GraphicsUnit.Pixel);
        _fText = new Font("Segoe UI", 12.5f * _k, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBold = new Font("Segoe UI", 12.5f * _k, FontStyle.Bold, GraphicsUnit.Pixel);
        _fSmall = new Font("Segoe UI", 11f * _k, FontStyle.Regular, GraphicsUnit.Pixel);
        _fIcon = new Font(Theme.IconFont, 11f * _k, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Theme.WS_EX_TOOLWINDOW | Theme.WS_EX_TOPMOST | Theme.WS_EX_NOACTIVATE | Theme.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyLock();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        MakeFonts();
        Invalidate();
    }

    public void Refresh(bool force)
    {
        if (!Visible && !force) return;
        _view = _engine.View(FightId, Mode);
        if (FightId > 0 && _view is null) FightId = MeterEngine.Follow; // fight dropped by a reset
        Invalidate();
    }

    // ----- layout ----------------------------------------------------------------------------------------------------

    private int K(float v) => (int)Math.Round(v * _k);

    private int HeaderH => K(28);
    private int TabsH => K(22);
    private int RowH => K(22);
    private int RowGap => Math.Max(1, K(2));

    private IEnumerable<(string Id, Rectangle R, string Glyph)> Buttons()
    {
        var w = K(26);
        var x = ClientSize.Width - w;
        yield return ("close", new Rectangle(x, 0, w, HeaderH), Theme.IconClose);
        x -= w;
        yield return ("lock", new Rectangle(x, 0, w, HeaderH), _settings.Locked ? Theme.IconLock : Theme.IconUnlock);
        x -= w;
        yield return ("reset", new Rectangle(x, 0, w, HeaderH), Theme.IconReset);
        x -= w;
        yield return ("menu", new Rectangle(x, 0, w, HeaderH), Theme.IconMenu);
    }

    private Rectangle TitleRect => new(0, 0, ClientSize.Width - 4 * K(26), HeaderH);

    private IEnumerable<(Mode Mode, Rectangle R)> Tabs()
    {
        var x = K(6);
        foreach (var m in new[] { Mode.Damage, Mode.Healing, Mode.Taken })
        {
            var w = TextRenderer.MeasureText(ModeName(m), _fSmall).Width + K(6);
            yield return (m, new Rectangle(x, HeaderH, w, TabsH));
            x += w + K(2);
        }
    }

    private Rectangle RowsArea => new(0, HeaderH + TabsH + K(3), ClientSize.Width, ClientSize.Height - HeaderH - TabsH - K(6));

    private int VisibleRows => Math.Max(1, (RowsArea.Height + RowGap) / (RowH + RowGap));

    private Rectangle RowRect(int index) =>
        new(K(4), RowsArea.Top + (index - _scroll) * (RowH + RowGap), ClientSize.Width - K(8), RowH);

    public static string ModeName(Mode m) => m switch
    {
        Mode.Damage => L.T("Damage", "Schaden"),
        Mode.Healing => L.T("Healing", "Heilung"),
        _ => L.T("Taken", "Erlitten"),
    };

    // ----- painting --------------------------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics);

    /// <summary>Paints the whole window (also used for the preview images).</summary>
    public void Draw(Graphics g)
    {
        Theme.Setup(g);
        g.Clear(Theme.Back);
        PaintHeader(g);
        PaintTabs(g);
        PaintRows(g);
        using var border = new Pen(Theme.Line);
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void PaintHeader(Graphics g)
    {
        using (var b = new SolidBrush(Theme.Header)) g.FillRectangle(b, 0, 0, ClientSize.Width, HeaderH);
        var info = _view?.Info;
        var x = K(8);
        // live dot: green while the fight runs
        var dot = K(7);
        using (var b = new SolidBrush(info is { Active: true } ? Theme.Live : Theme.Faint))
            g.FillEllipse(b, x, (HeaderH - dot) / 2f, dot, dot);
        x += dot + K(6);
        var title = TitleRect;
        var right = title.Right - K(4);
        var time = info is null ? "" : Fmt.Duration(info.DurationMs);
        if (info?.BossHp is { } hp) time = $"{(hp * 100).ToString("0", L.Culture)} %  ·  {time}";
        var timeW = TextRenderer.MeasureText(time, _fSmall).Width;
        var name = info?.Title ?? L.AppName;
        if (FightId == MeterEngine.Total && info is not null) name = info.Title;
        var chevron = "";
        var chevW = K(14);
        var nameRect = new Rectangle(x, 0, Math.Max(10, right - timeW - x - chevW - K(4)), HeaderH);
        var hoverTitle = _hover == "title";
        Theme.Str(g, name, _fTitle, nameRect, hoverTitle ? Theme.Accent : Theme.Text, Theme.Left);
        var nameW = Math.Min(nameRect.Width, TextRenderer.MeasureText(name, _fTitle).Width);
        Theme.Str(g, chevron, _fIcon, new Rectangle(x + nameW, 0, chevW, HeaderH), Theme.Dim, Theme.Center);
        Theme.Str(g, time, _fSmall, new Rectangle(right - timeW, 0, timeW, HeaderH), Theme.Dim, Theme.Right);
        if (FightId != MeterEngine.Follow)
        {
            // not following the running fight: an accent line tells
            using var p = new Pen(Theme.Accent, Math.Max(1, K(2)));
            g.DrawLine(p, 0, HeaderH - 1, K(40), HeaderH - 1);
        }
        if (info?.BossHp is { } bossHp)
        {
            var w = (int)(ClientSize.Width * bossHp);
            using var bar = new SolidBrush(Theme.Alpha(Theme.Danger, 200));
            g.FillRectangle(bar, 0, HeaderH - Math.Max(2, K(2)), w, Math.Max(2, K(2)));
        }
        foreach (var (id, r, glyph) in Buttons())
        {
            if (_hover == id)
                using (var b = new SolidBrush(id == "close" ? Theme.Alpha(Theme.Danger, 160) : Theme.Hover)) g.FillRectangle(b, r);
            var color = id == "lock" && _settings.Locked ? Theme.Accent : _hover == id ? Theme.Text : Theme.Dim;
            Theme.Str(g, glyph, _fIcon, r, color, Theme.Center);
        }
    }

    private void PaintTabs(Graphics g)
    {
        using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, 0, HeaderH, ClientSize.Width, TabsH);
        var lastRight = 0;
        foreach (var (m, r) in Tabs())
        {
            var on = m == Mode;
            Theme.Str(g, ModeName(m), _fSmall, r, on ? Theme.Text : _hover == "tab" + (int)m ? Theme.Text : Theme.Dim, Theme.Center);
            if (on)
                using (var p = new SolidBrush(Theme.Accent))
                    g.FillRectangle(p, r.Left + K(3), r.Bottom - Math.Max(2, K(2)), r.Width - K(6), Math.Max(2, K(2)));
            lastRight = r.Right;
        }
        if (_view is { Total: > 0 } v)
        {
            var text = $"Σ {Fmt.Compact(v.Total)}  ·  {Fmt.Compact(v.PerSec)}/s";
            var r = new Rectangle(lastRight + K(4), HeaderH, ClientSize.Width - lastRight - K(10), TabsH);
            Theme.Str(g, text, _fSmall, r, Theme.Dim, Theme.Right);
        }
    }

    private void PaintRows(Graphics g)
    {
        var rows = _view?.Rows ?? [];
        var area = RowsArea;
        if (rows.Count == 0)
        {
            var text = _status() ?? (_view is null
                ? L.T("Waiting for a fight …", "Warte auf einen Kampf …")
                : L.T("Nothing in this tab.", "Nichts in diesem Reiter."));
            Theme.Str(g, text, _fSmall, new Rectangle(K(10), area.Top, area.Width - K(20), Math.Min(area.Height, K(60))), Theme.Dim,
                Theme.Center | TextFormatFlags.WordBreak);
            return;
        }
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, rows.Count - VisibleRows));
        var max = Math.Max(1, rows.Max(r => r.Value));
        var showTotal = ClientSize.Width >= K(250);
        var colShare = K(42);
        var colTotal = showTotal ? K(52) : 0;
        var colRate = K(52);
        var state = g.Save();
        g.SetClip(area);
        for (var i = _scroll; i < rows.Count && i < _scroll + VisibleRows + 1; i++)
        {
            var row = rows[i];
            var r = RowRect(i);
            var color = ClassInfo.Color(row.Class);
            using (var track = new SolidBrush(_hover == "row" + i ? Theme.Hover : Theme.Panel)) g.FillRectangle(track, r);
            var w = (int)(r.Width * (row.Value / (double)max));
            // one colour per class, the same for everyone (you are marked by the gold edge and the bold name)
            using (var bar = new SolidBrush(Theme.Mix(Theme.Panel, color, 0.56)))
                g.FillRectangle(bar, r.X, r.Y, Math.Max(w, K(2)), r.Height);
            if (row.IsSelf)
                using (var edge = new SolidBrush(Theme.Accent)) g.FillRectangle(edge, r.X, r.Y, Math.Max(2, K(2)), r.Height);
            var tx = r.X + K(6);
            var rank = (i + 1).ToString();
            var rankW = K(18);
            Theme.StrShadow(g, rank, _fSmall, new Rectangle(tx - K(2), r.Y, rankW, r.Height), Theme.Alpha(Theme.Text, 190),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            tx += rankW;
            var iconH = r.Height - K(2);
            ClassIcons.Badge(g, row.Class, new Rectangle(tx, r.Y + (r.Height - iconH) / 2, iconH, iconH), K(4));
            tx += iconH + K(5);
            var numbersW = colRate + colTotal + colShare + K(6);
            var nameRect = new Rectangle(tx, r.Y, Math.Max(10, r.Right - numbersW - tx), r.Height);
            var nameFont = row.IsSelf ? _fBold : _fText;
            Theme.StrShadow(g, row.Name, nameFont, nameRect, Theme.Text, Theme.Left);
            if (row.Deaths > 0)
            {
                // deaths: a red mark right after the name
                var nameW = Math.Min(nameRect.Width, TextRenderer.MeasureText(row.Name, nameFont).Width);
                var dead = "☠" + row.Deaths;
                var deadW = TextRenderer.MeasureText(dead, _fSmall).Width;
                if (nameW + deadW < nameRect.Width)
                    Theme.StrShadow(g, dead, _fSmall, new Rectangle(nameRect.X + nameW, r.Y, deadW + K(2), r.Height),
                        Color.FromArgb(255, 140, 140), Theme.Left);
            }
            var x = r.Right - K(6);
            Theme.StrShadow(g, Fmt.Pct(row.Share).Replace(" ", ""), _fSmall, new Rectangle(x - colShare, r.Y, colShare, r.Height),
                Theme.Alpha(Theme.Text, 215), Theme.Right);
            x -= colShare;
            if (showTotal)
            {
                Theme.StrShadow(g, Fmt.Compact(row.Value), _fSmall, new Rectangle(x - colTotal, r.Y, colTotal, r.Height),
                    Theme.Alpha(Theme.Text, 215), Theme.Right);
                x -= colTotal;
            }
            Theme.StrShadow(g, Fmt.Compact(row.PerSec), _fBold, new Rectangle(x - colRate, r.Y, colRate, r.Height), Theme.Text, Theme.Right);
        }
        g.Restore(state);
        if (rows.Count > VisibleRows)
        {
            // scroll position
            var h = Math.Max(K(12), area.Height * VisibleRows / rows.Count);
            var y = area.Top + (area.Height - h) * _scroll / Math.Max(1, rows.Count - VisibleRows);
            using var b = new SolidBrush(Theme.Line);
            g.FillRectangle(b, ClientSize.Width - K(3), y, K(2), h);
        }
    }

    // ----- input -----------------------------------------------------------------------------------------------------

    private string? HitTest(Point p)
    {
        foreach (var (id, r, _) in Buttons())
            if (r.Contains(p)) return id;
        if (p.Y < HeaderH) return "title";
        foreach (var (m, r) in Tabs())
            if (r.Contains(p)) return "tab" + (int)m;
        if (p.Y < HeaderH + TabsH) return null;
        var rows = _view?.Rows ?? [];
        for (var i = _scroll; i < rows.Count && i < _scroll + VisibleRows + 1; i++)
            if (RowRect(i).Contains(p)) return "row" + i;
        return null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Theme.WM_NCHITTEST)
        {
            if (_settings.Locked)
            {
                m.Result = Theme.HTTRANSPARENT;
                return;
            }
            var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
            var edge = Theme.Edge(p, ClientSize, Math.Max(4, K(5)));
            if (edge != Theme.HTCLIENT)
            {
                m.Result = edge;
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressAt is { } at && _pressOn == "title" && (Math.Abs(e.X - at.X) > 3 || Math.Abs(e.Y - at.Y) > 3))
        {
            // drag the window by its header
            _pressAt = null;
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
            return;
        }
        var hover = HitTest(e.Location);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover is not null && hover != "title" ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressAt = e.Location;
        _pressOn = HitTest(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            MainMenu?.Show(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left || _pressAt is null) return;
        _pressAt = null;
        var on = HitTest(e.Location);
        if (on != _pressOn) return;
        switch (on)
        {
            case "close":
                Hide();
                _settings.Hidden = true;
                break;
            case "lock":
                _settings.Locked = !_settings.Locked;
                ApplyLock();
                break;
            case "reset":
                _engine.Split();
                FightId = MeterEngine.Follow;
                Refresh(force: true);
                break;
            case "menu":
                MainMenu?.Show(this, new Point(Buttons().First(b => b.Id == "menu").R.Left, HeaderH));
                break;
            case "title":
                FightMenu().Show(this, new Point(K(8), HeaderH));
                break;
            case { } t when t.StartsWith("tab"):
                Mode = (Mode)int.Parse(t[3..]);
                _scroll = 0;
                Refresh(force: true);
                break;
            case { } r when r.StartsWith("row"):
                var rows = _view?.Rows ?? [];
                var i = int.Parse(r[3..]);
                if (i < rows.Count && rows[i].Entity != 0) RowClicked?.Invoke(FightId, rows[i], Mode);
                break;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll = Math.Max(0, _scroll - Math.Sign(e.Delta));
        Invalidate();
    }

    /// <summary>The fights to choose from: the running one (follow), all together, then each fight, newest first.</summary>
    private ContextMenuStrip FightMenu()
    {
        var menu = Theme.Menu();
        void Add(string text, int id)
        {
            var item = new ToolStripMenuItem(text) { Checked = FightId == id };
            item.Click += (_, _) =>
            {
                FightId = id;
                _scroll = 0;
                Refresh(force: true);
            };
            menu.Items.Add(item);
        }
        Add(L.T("Current fight (follow)", "Aktueller Kampf (folgen)"), MeterEngine.Follow);
        Add(L.T("All fights together", "Alle Kämpfe zusammen"), MeterEngine.Total);
        var fights = _engine.Fights();
        if (fights.Count > 0) menu.Items.Add(new ToolStripSeparator());
        foreach (var f in fights.Take(30))
        {
            var start = DateTimeOffset.FromUnixTimeMilliseconds(f.StartMs).ToLocalTime();
            var text = $"{start:HH:mm}   {f.Title}   {Fmt.Duration(f.DurationMs)}   {Fmt.Compact(f.Damage)}";
            Add(f.Active ? "● " + text : text, f.Id);
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        return menu;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            foreach (var f in new[] { _fTitle, _fText, _fBold, _fSmall, _fIcon }) f?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
