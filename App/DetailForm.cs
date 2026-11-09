using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DpsMeter.Meter;

namespace DpsMeter.App;

/// <summary>
/// Skill details of one player in one fight: summary tiles, the amount per second as a chart, and a table of every skill
/// (amount, share, per second, hits, crit, back, perfect, double, multi, average, max, misses), sortable by any column.
/// Updates live while the fight runs; ◀ ▶ step through the players of the fight.
/// </summary>
internal sealed class DetailForm : Form
{
    private sealed record Column(string Title, int Width, int Priority, Func<SkillRow, string> Text, Func<SkillRow, double> Key,
        bool Left = false);

    private readonly MeterEngine _engine;
    private readonly Settings _settings;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private DetailView? _view;
    private int _fightId;
    private uint _entity;
    private string? _name;
    private Mode _mode;
    private int _sortColumn = 1;
    private bool _sortAscending;
    private int _scroll;
    private string? _hover;
    private Point? _pressAt;
    private string? _pressOn;
    private float _k = 1;
    private Font _fName = null!, _fText = null!, _fBold = null!, _fSmall = null!, _fTile = null!, _fIcon = null!;

    public DetailForm(MeterEngine engine, Settings settings)
    {
        _engine = engine;
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Back;
        Text = L.T("Skill details", "Skill-Details");
        MinimumSize = new Size(420, 260);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Size = new Size(Math.Max(420, settings.DetailW), Math.Max(260, settings.DetailH));
        Opacity = Math.Clamp(settings.Opacity, 30, 100) / 100.0;
        MakeFonts();
        _timer.Tick += (_, _) => Reload();
    }

    /// <summary>Opens (or switches) the window to a player.</summary>
    public void ShowFor(int fightId, uint entity, string name, Mode mode, Form near)
    {
        if (_entity != entity || _fightId != fightId) _scroll = 0;
        _fightId = fightId;
        _entity = entity;
        _name = name;
        _mode = mode;
        if (!Visible)
        {
            var area = Screen.FromControl(near).WorkingArea;
            var r = new Rectangle(_settings.DetailX, _settings.DetailY, Width, Height);
            if (_settings.DetailX < 0 || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)))
            {
                var x = near.Left - Width - 8;
                if (x < area.Left) x = Math.Min(near.Right + 8, area.Right - Width);
                r = new Rectangle(x, Math.Clamp(near.Top, area.Top, Math.Max(area.Top, area.Bottom - Height)), Width, Height);
            }
            Bounds = r;
            Show();
        }
        Reload();
        _timer.Start();
    }

    /// <summary>Loads a player without showing the window (preview images).</summary>
    public void ShowForPreview(int fightId, uint entity, string name, Mode mode)
    {
        _fightId = fightId;
        _entity = entity;
        _name = name;
        _mode = mode;
        _scroll = 0;
        Reload();
    }

    public void SaveBounds()
    {
        if (WindowState != FormWindowState.Normal || !Visible) return;
        _settings.DetailX = Left;
        _settings.DetailY = Top;
        _settings.DetailW = Width;
        _settings.DetailH = Height;
    }

    public void ApplySettings()
    {
        Opacity = Math.Clamp(_settings.Opacity, 30, 100) / 100.0;
        MakeFonts();
        Reload();
    }

    private void Reload()
    {
        _view = _engine.Detail(_fightId, _entity, _name, _mode);
        if (_view is not null)
        {
            _entity = _view.Entity;
            _name = _view.Name;
        }
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _timer.Stop();
    }

    private void MakeFonts()
    {
        _k = (float)(Math.Clamp(_settings.Scale, 0.7, 2.0) * DeviceDpi / 96.0);
        foreach (var f in new[] { _fName, _fText, _fBold, _fSmall, _fTile, _fIcon }) f?.Dispose();
        _fName = new Font("Segoe UI", 17f * _k, FontStyle.Bold, GraphicsUnit.Pixel);
        _fText = new Font("Segoe UI", 12.5f * _k, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBold = new Font("Segoe UI", 12.5f * _k, FontStyle.Bold, GraphicsUnit.Pixel);
        _fSmall = new Font("Segoe UI", 11f * _k, FontStyle.Regular, GraphicsUnit.Pixel);
        _fTile = new Font("Segoe UI", 15f * _k, FontStyle.Bold, GraphicsUnit.Pixel);
        _fIcon = new Font(Theme.IconFont, 11f * _k, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Theme.WS_EX_TOOLWINDOW | Theme.WS_EX_TOPMOST | Theme.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        MakeFonts();
        Invalidate();
    }

    // ----- columns ---------------------------------------------------------------------------------------------------

    private static string Rate(SkillRow s, int n) => s.Hits == 0 ? "–" : (n * 100.0 / s.Hits).ToString("0", L.Culture) + " %";

    private static string Count(int n) => n == 0 ? "–" : n.ToString("#,0", L.Culture);

    private List<Column> Columns()
    {
        var name = new Column(L.T("Skill", "Fähigkeit"), 0, 0, s => s.Name, s => 0, Left: true);
        var common = new List<Column>
        {
            name,
            new(ModeAmount(_mode), 96, 0, s => Fmt.Full(s.Total), s => s.Total),
            new(L.T("Share", "Anteil"), 60, 0, s => Fmt.Pct(s.Share), s => s.Share),
            new(_mode == Mode.Healing ? "HPS" : _mode == Mode.Damage ? "DPS" : L.T("per s", "pro s"), 66, 1,
                s => Fmt.Compact(s.PerSec), s => s.PerSec),
            new(L.T("Hits", "Treffer"), 58, 1, s => Count(s.Hits), s => s.Hits),
            new(L.T("Crit", "Krit"), 52, 2, s => Rate(s, s.Crits), s => s.Rate(s.Crits)),
        };
        if (_mode == Mode.Damage)
        {
            common.Add(new(L.T("Back", "Rücken"), 58, 3, s => Rate(s, s.Back), s => s.Rate(s.Back)));
            common.Add(new(L.T("Perfect", "Perfekt"), 58, 4, s => Rate(s, s.Perfect), s => s.Rate(s.Perfect)));
            common.Add(new(L.T("Double", "Wucht"), 54, 4, s => Rate(s, s.Double), s => s.Rate(s.Double)));
            common.Add(new(L.T("Multi", "Mehrf."), 54, 5, s => Rate(s, s.Multi), s => s.Rate(s.Multi)));
        }
        if (_mode == Mode.Taken)
        {
            common.Add(new(L.T("Block", "Block"), 54, 3, s => Rate(s, s.Blocks), s => s.Rate(s.Blocks)));
            common.Add(new(L.T("Parry", "Parade"), 56, 3, s => Rate(s, s.Parries), s => s.Rate(s.Parries)));
        }
        common.Add(new("Ø", 70, 2, s => s.Hits == 0 ? "–" : Fmt.Compact(s.Avg), s => s.Avg));
        common.Add(new("Max", 78, 2, s => s.Max == 0 ? "–" : Fmt.Full(s.Max), s => s.Max));
        if (_mode != Mode.Healing)
            common.Add(new(_mode == Mode.Taken ? L.T("Evaded", "Ausgew.") : L.T("Missed", "Verfehlt"), 58, 5,
                s => Count(s.Misses), s => s.Misses));
        // drop the least important columns until the rest fits
        var width = ClientSize.Width - 2 * K(10);
        var minName = K(150);
        for (var p = 5; p >= 1 && common.Sum(c => K(c.Width)) + minName > width; p--) common.RemoveAll(c => c.Priority == p);
        return common;
    }

    private static string ModeAmount(Mode m) => m switch
    {
        Mode.Damage => L.T("Damage", "Schaden"),
        Mode.Healing => L.T("Healing", "Heilung"),
        _ => L.T("Taken", "Erlitten"),
    };

    // ----- layout ----------------------------------------------------------------------------------------------------

    private int K(float v) => (int)Math.Round(v * _k);
    private int HeaderH => K(46);
    private int TilesH => K(50);
    private int TabsH => K(26);
    private int ChartH => Math.Clamp(ClientSize.Height / 5, K(60), K(110));
    private int TableTop => HeaderH + TilesH + TabsH + ChartH + K(12);
    private int RowH => K(22);
    private int VisibleRows => Math.Max(1, (ClientSize.Height - TableTop - RowH - K(8)) / RowH);

    private IEnumerable<(string Id, Rectangle R, string Glyph)> Buttons()
    {
        var w = K(30);
        var x = ClientSize.Width - w;
        yield return ("close", new Rectangle(x, 0, w, HeaderH), Theme.IconClose);
        x -= w;
        yield return ("next", new Rectangle(x, 0, w, HeaderH), Theme.IconNext);
        x -= w;
        yield return ("prev", new Rectangle(x, 0, w, HeaderH), Theme.IconPrev);
    }

    private IEnumerable<(Mode Mode, Rectangle R)> Tabs()
    {
        var x = K(10);
        var y = HeaderH + TilesH;
        foreach (var m in new[] { Mode.Damage, Mode.Healing, Mode.Taken })
        {
            var w = TextRenderer.MeasureText(MeterForm.ModeName(m), _fText).Width + K(12);
            yield return (m, new Rectangle(x, y, w, TabsH));
            x += w + K(4);
        }
    }

    private List<(Column Col, Rectangle R)> ColumnRects(List<Column> cols)
    {
        var list = new List<(Column, Rectangle)>();
        var left = K(10);
        var right = ClientSize.Width - K(10);
        var fixedW = cols.Where(c => c.Width > 0).Sum(c => K(c.Width));
        var nameW = Math.Max(K(80), right - left - fixedW);
        var x = left;
        foreach (var c in cols)
        {
            var w = c.Width == 0 ? nameW : K(c.Width);
            list.Add((c, new Rectangle(x, TableTop, w, RowH)));
            x += w;
        }
        return list;
    }

    private List<SkillRow> SortedSkills(List<Column> cols)
    {
        var skills = _view?.Skills.ToList() ?? [];
        if (_sortColumn <= 0 || _sortColumn >= cols.Count)
            skills.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture) * (_sortAscending ? 1 : -1));
        else
        {
            var key = cols[_sortColumn].Key;
            skills.Sort((a, b) => key(a).CompareTo(key(b)) * (_sortAscending ? 1 : -1));
        }
        return skills;
    }

    // ----- painting --------------------------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics);

    /// <summary>Paints the whole window (also used for the preview images).</summary>
    public void Draw(Graphics g)
    {
        Theme.Setup(g);
        g.Clear(Theme.Back);
        PaintHeader(g);
        if (_view is null)
        {
            Theme.Str(g, L.T("This player has nothing in this fight.", "Dieser Spieler hat in diesem Kampf nichts."), _fText,
                new Rectangle(0, HeaderH, ClientSize.Width, K(80)), Theme.Dim, Theme.Center);
        }
        else
        {
            PaintTiles(g, _view);
            PaintTabs(g);
            PaintChart(g, _view);
            PaintTable(g, _view);
        }
        using var border = new Pen(Theme.Line);
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void PaintHeader(Graphics g)
    {
        using (var b = new SolidBrush(Theme.Header)) g.FillRectangle(b, 0, 0, ClientSize.Width, HeaderH);
        var v = _view;
        var color = ClassInfo.Color(v?.Class ?? 0);
        using (var b = new SolidBrush(color)) g.FillRectangle(b, 0, 0, Math.Max(3, K(4)), HeaderH);
        var x = K(14);
        var buttonsLeft = ClientSize.Width - 3 * K(30);
        var name = v?.Name ?? _name ?? "";
        var nameW = Math.Min(TextRenderer.MeasureText(name, _fName).Width, buttonsLeft - x - K(40));
        Theme.Str(g, name, _fName, new Rectangle(x, 0, nameW, HeaderH), color, Theme.Left);
        x += nameW + K(10);
        if (v is not null)
        {
            var parts = new List<string> { ClassInfo.Name(v.Class) };
            parts.Add(v.Fight.Title);
            if (v.Fight.Zone.Length > 0) parts.Add(v.Fight.Zone);
            parts.Add(Fmt.Duration(v.Fight.DurationMs) + (v.Fight.Active ? L.T(" (running)", " (läuft)") : ""));
            Theme.Str(g, string.Join("  ·  ", parts), _fText, new Rectangle(x, 0, Math.Max(10, buttonsLeft - x - K(6)), HeaderH),
                Theme.Dim, Theme.Left);
        }
        foreach (var (id, r, glyph) in Buttons())
        {
            if (_hover == id)
                using (var b = new SolidBrush(id == "close" ? Theme.Alpha(Theme.Danger, 160) : Theme.Hover)) g.FillRectangle(b, r);
            Theme.Str(g, glyph, _fIcon, r, _hover == id ? Theme.Text : Theme.Dim, Theme.Center);
        }
    }

    private void PaintTiles(Graphics g, DetailView v)
    {
        var s = v.Sum;
        var tiles = new List<(string Caption, string Value)>
        {
            (ModeAmount(v.Mode), Fmt.Compact(v.Total)),
            (v.Mode == Mode.Healing ? "HPS" : v.Mode == Mode.Damage ? "DPS" : L.T("Per second", "Pro Sekunde"), Fmt.Compact(v.PerSec)),
            (L.T("Share", "Anteil"), Fmt.Pct(v.Share)),
            (L.T("Hits", "Treffer"), Fmt.Full(s.Hits)),
            (L.T("Crit", "Krit"), Rate(s, s.Crits)),
        };
        if (v.Mode == Mode.Damage)
        {
            tiles.Add((L.T("Back", "Rücken"), Rate(s, s.Back)));
            tiles.Add((L.T("Perfect", "Perfekt"), Rate(s, s.Perfect)));
            tiles.Add((L.T("Double", "Wucht"), Rate(s, s.Double)));
            tiles.Add((L.T("Multi hit", "Mehrfach"), Rate(s, s.Multi)));
        }
        if (v.Mode == Mode.Taken)
        {
            tiles.Add((L.T("Block", "Block"), Rate(s, s.Blocks)));
            tiles.Add((L.T("Parry", "Parade"), Rate(s, s.Parries)));
        }
        tiles.Add((L.T("Max hit", "Max. Treffer"), s.Max == 0 ? "–" : Fmt.Full(s.Max)));
        tiles.Add((L.T("Deaths", "Tode"), v.Deaths.ToString(L.Culture)));
        var left = K(10);
        var width = ClientSize.Width - 2 * left;
        var n = tiles.Count;
        // narrow window: fewer tiles
        while (n > 4 && width / n < K(78)) n--;
        var tw = width / n;
        for (var i = 0; i < n; i++)
        {
            var r = new Rectangle(left + i * tw, HeaderH + K(6), tw - K(4), TilesH - K(8));
            using (var path = Theme.Rounded(r, K(4)))
            using (var b = new SolidBrush(Theme.Panel))
                g.FillPath(b, path);
            Theme.Str(g, tiles[i].Caption, _fSmall, new Rectangle(r.X + K(8), r.Y + K(3), r.Width - K(10), K(16)), Theme.Dim, Theme.Left);
            Theme.Str(g, tiles[i].Value, _fTile, new Rectangle(r.X + K(8), r.Y + K(18), r.Width - K(10), r.Height - K(19)),
                i == 0 ? ClassInfo.Color(v.Class) : Theme.Text, Theme.Left);
        }
    }

    private void PaintTabs(Graphics g)
    {
        foreach (var (m, r) in Tabs())
        {
            var on = m == _mode;
            Theme.Str(g, MeterForm.ModeName(m), on ? _fBold : _fText, r, on || _hover == "tab" + (int)m ? Theme.Text : Theme.Dim,
                Theme.Center);
            if (on)
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, r.Left + K(4), r.Bottom - Math.Max(2, K(2)), r.Width - K(8), Math.Max(2, K(2)));
        }
        if (_view is { } v && _view.Ranking.Count > 1)
        {
            var place = v.Ranking.ToList().FindIndex(r => r.Entity == v.Entity) + 1;
            var text = place > 0
                ? L.T($"Place {place} of {v.Ranking.Count}", $"Platz {place} von {v.Ranking.Count}")
                : "";
            var y = HeaderH + TilesH;
            Theme.Str(g, text, _fSmall, new Rectangle(0, y, ClientSize.Width - K(12), TabsH), Theme.Dim, Theme.Right);
        }
    }

    private void PaintChart(Graphics g, DetailView v)
    {
        var area = new Rectangle(K(10), HeaderH + TilesH + TabsH + K(4), ClientSize.Width - K(20), ChartH);
        using (var path = Theme.Rounded(area, K(4)))
        using (var b = new SolidBrush(Theme.Panel))
            g.FillPath(b, path);
        var series = v.Series;
        if (series.Length < 2) return;
        // 5-second moving average, like the in-game graph it smooths single big hits
        var smooth = new double[series.Length];
        double sum = 0;
        for (var i = 0; i < series.Length; i++)
        {
            sum += series[i];
            if (i >= 5) sum -= series[i - 5];
            smooth[i] = sum / Math.Min(i + 1, 5);
        }
        var max = Math.Max(1, smooth.Max());
        var inner = Rectangle.Inflate(area, -K(8), -K(8));
        inner.Width -= K(46);
        float X(int i) => inner.Left + inner.Width * i / (float)(series.Length - 1);
        float Y(double val) => inner.Bottom - (float)(inner.Height * val / max);
        var pts = new List<PointF> { new(X(0), inner.Bottom) };
        for (var i = 0; i < smooth.Length; i++) pts.Add(new PointF(X(i), Y(smooth[i])));
        pts.Add(new PointF(X(series.Length - 1), inner.Bottom));
        var color = ClassInfo.Color(v.Class);
        using (var fill = new LinearGradientBrush(new Rectangle(inner.Left, inner.Top - 1, inner.Width, inner.Height + 2),
                   Theme.Alpha(color, 150), Theme.Alpha(color, 20), LinearGradientMode.Vertical))
            g.FillPolygon(fill, pts.ToArray());
        using (var line = new Pen(color, Math.Max(1.5f, K(2))))
            g.DrawLines(line, pts.Skip(1).Take(smooth.Length).ToArray());
        // average line and labels
        var avgY = Y(v.PerSec);
        using (var dash = new Pen(Theme.Alpha(Theme.Text, 120), 1) { DashStyle = DashStyle.Dash })
            g.DrawLine(dash, inner.Left, avgY, inner.Right, avgY);
        var labelX = inner.Right + K(6);
        Theme.Str(g, Fmt.Compact(max), _fSmall, new Rectangle(labelX, inner.Top - K(6), K(48), K(14)), Theme.Dim, Theme.Left);
        Theme.Str(g, "Ø " + Fmt.Compact(v.PerSec), _fSmall, new Rectangle(labelX, (int)avgY - K(7), K(48), K(14)), Theme.Text,
            Theme.Left);
        Theme.Str(g, "0:00", _fSmall, new Rectangle(inner.Left, inner.Bottom - K(14), K(40), K(14)), Theme.Faint, Theme.Left);
        Theme.Str(g, Fmt.Duration(v.Fight.DurationMs), _fSmall, new Rectangle(inner.Right - K(50), inner.Bottom - K(14), K(50), K(14)),
            Theme.Faint, Theme.Right);
    }

    private void PaintTable(Graphics g, DetailView v)
    {
        var cols = Columns();
        var rects = ColumnRects(cols);
        var head = new Rectangle(K(10), TableTop, ClientSize.Width - K(20), RowH);
        using (var b = new SolidBrush(Theme.Header)) g.FillRectangle(b, head);
        for (var i = 0; i < rects.Count; i++)
        {
            var (c, r) = rects[i];
            var title = c.Title + (i == _sortColumn ? _sortAscending ? " ▲" : " ▼" : "");
            var pad = new Rectangle(r.X + K(6), r.Y, r.Width - K(12), r.Height);
            Theme.Str(g, title, i == _sortColumn ? _fBold : _fSmall, pad,
                i == _sortColumn || _hover == "col" + i ? Theme.Text : Theme.Dim, c.Left ? Theme.Left : Theme.Right);
        }
        var skills = SortedSkills(cols);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, skills.Count - VisibleRows));
        var color = ClassInfo.Color(v.Class);
        var maxShare = skills.Count == 0 ? 1 : Math.Max(0.0001, skills.Max(s => s.Share));
        for (var n = 0; n < VisibleRows && _scroll + n < skills.Count; n++)
        {
            var s = skills[_scroll + n];
            var y = TableTop + RowH * (n + 1);
            var row = new Rectangle(head.X, y, head.Width, RowH);
            if (_hover == "skill" + n)
                using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, row);
            else if (n % 2 == 1)
                using (var b = new SolidBrush(Theme.Alpha(Theme.Panel, 140))) g.FillRectangle(b, row);
            // share bar under the skill name
            var nameRect = rects[0].R;
            var barW = (int)((nameRect.Width - K(8)) * (s.Share / maxShare));
            using (var b = new SolidBrush(Theme.Alpha(color, 70)))
                g.FillRectangle(b, nameRect.X + K(2), y + K(3), Math.Max(K(2), barW), RowH - K(6));
            for (var i = 0; i < rects.Count; i++)
            {
                var (c, r) = rects[i];
                var pad = new Rectangle(r.X + K(6), y, r.Width - K(12), RowH);
                var font = i == 1 ? _fBold : _fText;
                var fg = i == 0 && s.Periodic ? Theme.Mix(Theme.Text, color, 0.35) : Theme.Text;
                Theme.Str(g, c.Text(s), font, pad, fg, c.Left ? Theme.Left : Theme.Right);
            }
        }
        if (skills.Count > VisibleRows)
        {
            var area = new Rectangle(0, TableTop + RowH, 0, VisibleRows * RowH);
            var h = Math.Max(K(14), area.Height * VisibleRows / skills.Count);
            var y = area.Top + (area.Height - h) * _scroll / Math.Max(1, skills.Count - VisibleRows);
            using var b = new SolidBrush(Theme.Line);
            g.FillRectangle(b, ClientSize.Width - K(6), y, K(3), h);
        }
        if (skills.Count == 0)
            Theme.Str(g, L.T("No skills in this tab.", "Keine Fähigkeiten in diesem Reiter."), _fText,
                new Rectangle(0, TableTop + RowH, ClientSize.Width, RowH * 2), Theme.Dim, Theme.Center);
    }

    // ----- input -----------------------------------------------------------------------------------------------------

    private string? HitTest(Point p)
    {
        foreach (var (id, r, _) in Buttons())
            if (r.Contains(p)) return id;
        if (p.Y < HeaderH) return "header";
        foreach (var (m, r) in Tabs())
            if (r.Contains(p)) return "tab" + (int)m;
        if (_view is null) return null;
        var rects = ColumnRects(Columns());
        for (var i = 0; i < rects.Count; i++)
            if (rects[i].R.Contains(p)) return "col" + i;
        if (p.Y > TableTop + RowH)
        {
            var n = (p.Y - TableTop - RowH) / RowH;
            if (n < VisibleRows && _scroll + n < _view.Skills.Count) return "skill" + n;
        }
        return null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Theme.WM_NCHITTEST)
        {
            var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
            var edge = Theme.Edge(p, ClientSize, Math.Max(5, K(6)));
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
        if (_pressAt is { } at && _pressOn == "header" && (Math.Abs(e.X - at.X) > 3 || Math.Abs(e.Y - at.Y) > 3))
        {
            _pressAt = null;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0);
            return;
        }
        var hover = HitTest(e.Location);
        if (hover == _hover) return;
        _hover = hover;
        Cursor = hover is not null && hover != "header" && !hover.StartsWith("skill") ? Cursors.Hand : Cursors.Default;
        Invalidate();
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
        if (e.Button != MouseButtons.Left || _pressAt is null) return;
        _pressAt = null;
        var on = HitTest(e.Location);
        if (on != _pressOn) return;
        switch (on)
        {
            case "close":
                SaveBounds();
                Hide();
                break;
            case "prev":
            case "next":
                Step(on == "next" ? 1 : -1);
                break;
            case { } t when t.StartsWith("tab"):
                _mode = (Mode)int.Parse(t[3..]);
                _scroll = 0;
                Reload();
                break;
            case { } c when c.StartsWith("col"):
                var i = int.Parse(c[3..]);
                if (i == _sortColumn) _sortAscending = !_sortAscending;
                else
                {
                    _sortColumn = i;
                    _sortAscending = i == 0;
                }
                Invalidate();
                break;
        }
    }

    private void Step(int direction)
    {
        if (_view is null) return;
        var ranking = _view.Ranking.Where(r => r.Entity != 0).ToList();
        if (ranking.Count == 0) return;
        var at = ranking.FindIndex(r => r.Entity == _view.Entity);
        var next = ranking[((at < 0 ? 0 : at) + direction + ranking.Count) % ranking.Count];
        _entity = next.Entity;
        _name = next.Name;
        _scroll = 0;
        Reload();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll = Math.Max(0, _scroll - Math.Sign(e.Delta) * 3);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            foreach (var f in new[] { _fName, _fText, _fBold, _fSmall, _fTile, _fIcon }) f?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
