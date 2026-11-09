using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using CubeRadar.Core.Capture;
using DpsMeter.Meter;

namespace DpsMeter.App;

internal static class TrayApp
{
    /// <summary>Held by the live program while it runs.</summary>
    public const string LiveMutex = "DpsMeter.Live";

    public static int Run(string? replayFile, double speed, bool afterUpdate = false)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write("crash", (Exception)e.ExceptionObject);
        var settings = Settings.Load();
        L.Lang = settings.Language;
        using var single = new Mutex(true, replayFile is null ? LiveMutex : "DpsMeter.Replay." + Environment.ProcessId, out var first);
        if (!first)
        {
            MessageBox.Show(L.T("The DPS meter is already running (icon in the notification area, bottom right).",
                "Der DPS-Meter läuft bereits (Symbol im Infobereich unten rechts)."), L.AppName, MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 1;
        }
        if (!LiveSniffer.IsNpcapInstalled())
        {
            var answer = MessageBox.Show(L.T(
                    "Npcap is not installed. The DPS meter uses it to read the network traffic of Aion 2 " +
                    "(read only, like Wireshark).\n\nOpen npcap.com now? Run the installer with its default options, then start the meter again.",
                    "Npcap ist nicht installiert. Der DPS-Meter liest damit den Netzwerkverkehr von Aion 2 mit " +
                    "(nur lesen, wie Wireshark).\n\nJetzt npcap.com öffnen? Installer mit den Standardoptionen ausführen, danach den Meter neu starten."),
                L.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) Open("https://npcap.com/#download");
            return 3;
        }
        if (replayFile is not null && !File.Exists(replayFile))
        {
            MessageBox.Show(L.T("File not found: ", "Datei nicht gefunden: ") + replayFile, L.AppName, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }
        using var ctx = new MeterContext(settings, replayFile, speed, afterUpdate);
        Application.Run(ctx);
        return 0;
    }

    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public static Icon AppIcon()
    {
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("meter.ico");
        return s is null ? SystemIcons.Application : new Icon(s, SystemInformation.SmallIconSize);
    }
}

/// <summary>Capture (or replay) → engine → meter and detail windows, tray icon with the menu, global hotkeys.</summary>
internal sealed class MeterContext : ApplicationContext
{
    private readonly Settings _settings;
    private readonly MeterEngine _engine;
    private readonly Pipeline _pipe;
    private readonly ReplayClock _clock;
    private readonly LiveSniffer? _sniffer;
    private readonly CancellationTokenSource _cts = new();
    private readonly MeterForm _meter;
    private readonly DetailForm _detail;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ContextMenuStrip _menu;
    private readonly string? _replayName;
    private readonly Updater? _updater;
    private volatile string _capture = "starting";

    public MeterContext(Settings settings, string? replayFile, double speed, bool afterUpdate)
    {
        _settings = settings;
        _replayName = replayFile is null ? null : Path.GetFileName(replayFile);
        _engine = new MeterEngine(GameData.Load());
        _settings.Apply(_engine);
        _clock = new ReplayClock(live: replayFile is null);
        _pipe = new Pipeline(_engine);

        _meter = new MeterForm(_engine, _settings, Status);
        _detail = new DetailForm(_engine, _settings);
        _menu = Theme.Menu();
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _meter.MainMenu = _menu;
        _meter.RowClicked += (fight, row, mode) => _detail.ShowFor(fight, row.Entity, row.Name, mode, _meter);
        _meter.ResizeEnd += (_, _) => SaveSoon();
        _detail.ResizeEnd += (_, _) => SaveSoon();

        _tray = new NotifyIcon
        {
            Icon = TrayApp.AppIcon(),
            Text = L.AppName,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleMeter();
        };

        _hotkeys = new HotkeyWindow();
        _hotkeys.Register(1, 'D', ToggleMeter);
        _hotkeys.Register(2, 'N', NewFight);
        _hotkeys.Register(3, 'L', ToggleLock);

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        if (replayFile is null)
        {
            _sniffer = new LiveSniffer(new LiveSnifferOptions());
            _sniffer.PacketArrived += (raw, dev) => _pipe.OnPacket(raw, dev.Name, LiveSniffer.Describe(dev));
            _sniffer.StatusChanged += s => _capture = s;
            _sniffer.Start();
            if (afterUpdate)
                _tray.ShowBalloonTip(5000, L.T($"Updated to version {Program.Version}", $"Auf Version {Program.Version} aktualisiert"),
                    L.T("What's new: see the releases page on GitHub.", "Was neu ist, steht auf der Release-Seite bei GitHub."),
                    ToolTipIcon.Info);
            if (Environment.ProcessPath is { } exe && Updater.Allowed(exe))
            {
                _updater = new Updater(_settings, exe, busy: () => _engine.InFight || GameData.IsInstance(_engine.Zone),
                    quit: () => _meter.BeginInvoke(ExitThread));
                _updater.Found += r => _meter.BeginInvoke(() =>
                    _tray.ShowBalloonTip(6000, L.T($"Version {r.Version.ToString(3)} is available", $"Version {r.Version.ToString(3)} ist verfügbar"),
                        _settings.AutoUpdate
                            ? L.T("It installs itself as soon as you are not in a fight or a dungeon.",
                                "Sie installiert sich, sobald du nicht im Kampf oder in einem Dungeon bist.")
                            : L.T("Right-click the icon → Install update.", "Rechtsklick auf das Symbol → Update installieren."),
                        ToolTipIcon.Info));
                _updater.Installing += r => _meter.BeginInvoke(() =>
                    _tray.ShowBalloonTip(3000, L.AppName, L.T($"Installing version {r.Version.ToString(3)} …",
                        $"Installiere Version {r.Version.ToString(3)} …"), ToolTipIcon.Info));
                _updater.Failed += reason => _meter.BeginInvoke(() =>
                    _tray.ShowBalloonTip(6000, L.T("Update failed", "Update fehlgeschlagen"), reason, ToolTipIcon.Warning));
                _updater.Start();
            }
        }
        else
        {
            _capture = "replay";
            _ = Pipeline.Replay(_pipe, replayFile, speed, _clock, _cts.Token).ContinueWith(t =>
            {
                if (t.Exception is { } ex) ErrorLog.Write("Replay", ex);
            }, TaskScheduler.Default);
        }

        _settings.Hidden = false;
        _meter.Show();
        _meter.ApplyLock();
        if (_settings.Locked)
            _tray.ShowBalloonTip(4000, L.AppName, L.T("The meter is locked (clicks go to the game). Ctrl+Alt+L unlocks it.",
                "Der Meter ist fixiert (Klicks gehen ans Spiel). Strg+Alt+L löst ihn."), ToolTipIcon.Info);
    }

    /// <summary>Text for the empty meter while nothing has been counted yet; null once there is a fight.</summary>
    private string? Status()
    {
        if (_engine.Fights().Count > 0) return null;
        if (_replayName is not null)
            return _clock.Finished ? L.T("Replay finished, no fight in it.", "Wiedergabe fertig, kein Kampf darin.") : L.T("Replay: ", "Wiedergabe: ") + _replayName;
        if (!_pipe.Connected)
            return _capture.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                ? L.T("Capture problem: ", "Mitschnitt-Problem: ") + _capture
                : L.T("Looking for the Aion 2 connection … (start the game, or change the zone once)",
                    "Suche die Aion-2-Verbindung … (Spiel starten oder einmal das Gebiet wechseln)");
        if (!_engine.SelfKnown)
            return L.T("Connected. Waiting for your character … (a teleport or zone change helps)",
                "Verbunden. Warte auf deinen Charakter … (Teleport oder Gebietswechsel hilft)");
        return L.T("Ready. Waiting for a fight …", "Bereit. Warte auf einen Kampf …");
    }

    private void OnTick()
    {
        _engine.Tick(_clock.Now());
        var text = L.AppName + (_replayName is not null ? " – " + _replayName : _pipe.Connected ? L.T(" – connected", " – verbunden") : "");
        if (_tray.Text != text) _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    // ----- actions ---------------------------------------------------------------------------------------------------

    private void ToggleMeter()
    {
        if (_meter.Visible)
        {
            _meter.Hide();
            _detail.Hide();
        }
        else
        {
            _meter.Show();
            _meter.Refresh(force: true);
        }
        _settings.Hidden = !_meter.Visible;
    }

    private void NewFight()
    {
        _engine.Split();
        _meter.FightId = MeterEngine.Follow;
        _meter.Refresh(force: true);
    }

    private void ToggleLock()
    {
        _settings.Locked = !_settings.Locked;
        _meter.ApplyLock();
        _settings.Save();
    }

    private void CopySummary()
    {
        if (_meter.CurrentView is not { Rows.Count: > 0 } v) return;
        var sb = new StringBuilder();
        sb.Append($"{v.Info.Title}");
        if (v.Info.Zone.Length > 0) sb.Append($" ({v.Info.Zone})");
        sb.Append($" – {Fmt.Duration(v.Info.DurationMs)} – {MeterForm.ModeName(v.Mode)} {Fmt.Short(v.Total)}");
        sb.AppendLine();
        var i = 0;
        foreach (var r in v.Rows.Take(12))
            sb.AppendLine($"{++i}. {r.Name} ({ClassInfo.Tag(r.Class)}) {Fmt.Short(r.PerSec)}/s · {Fmt.Short(r.Value)} · {Fmt.Pct(r.Share)}");
        try
        {
            Clipboard.SetText(sb.ToString().TrimEnd());
            _tray.ShowBalloonTip(2000, L.AppName, L.T("Summary copied.", "Zusammenfassung kopiert."), ToolTipIcon.None);
        }
        catch (ExternalException) { }
    }

    private System.Windows.Forms.Timer? _saveTimer;

    private void SaveSoon()
    {
        _saveTimer ??= new System.Windows.Forms.Timer { Interval = 800 };
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveNow;
        _saveTimer.Tick += SaveNow;
        _saveTimer.Start();
    }

    private void SaveNow(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        _meter.SaveBounds();
        _detail.SaveBounds();
        _settings.Save();
    }

    // ----- menu ------------------------------------------------------------------------------------------------------

    private void BuildMenu()
    {
        _menu.Items.Clear();
        ToolStripMenuItem Item(string text, Action action, bool check = false, string? keys = null)
        {
            var item = new ToolStripMenuItem(text) { Checked = check, ShortcutKeyDisplayString = keys };
            item.Click += (_, _) => action();
            return item;
        }
        ToolStripMenuItem Choice<T>(string text, IEnumerable<(string Label, T Value)> options, T current, Action<T> set)
        {
            var parent = new ToolStripMenuItem(text);
            foreach (var (label, value) in options)
                parent.DropDownItems.Add(Item(label, () =>
                {
                    set(value);
                    _settings.Save();
                    _meter.ApplySettings();
                    _detail.ApplySettings();
                }, EqualityComparer<T>.Default.Equals(value, current)));
            if (parent.DropDown is ToolStripDropDownMenu dd)
            {
                dd.ShowImageMargin = false;
                dd.ShowCheckMargin = true;
            }
            parent.DropDown.Renderer = _menu.Renderer;
            return parent;
        }

        _menu.Items.Add(Item(_meter.Visible ? L.T("Hide meter", "Meter ausblenden") : L.T("Show meter", "Meter anzeigen"), ToggleMeter,
            keys: L.T("Ctrl+Alt+D", "Strg+Alt+D")));
        _menu.Items.Add(Item(L.T("New fight", "Neuer Kampf"), NewFight, keys: L.T("Ctrl+Alt+N", "Strg+Alt+N")));
        _menu.Items.Add(Item(L.T("Clear all fights", "Alle Kämpfe löschen"), () =>
        {
            _engine.Reset();
            _meter.FightId = MeterEngine.Follow;
            _detail.Hide();
            _meter.Refresh(force: true);
        }));
        _menu.Items.Add(Item(L.T("Copy summary", "Zusammenfassung kopieren"), CopySummary));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item(L.T("Lock (clicks go to the game)", "Fixieren (Klicks gehen ans Spiel)"), ToggleLock, _settings.Locked,
            L.T("Ctrl+Alt+L", "Strg+Alt+L")));
        _menu.Items.Add(Choice(L.T("Opacity", "Deckkraft"), new[] { 100, 90, 80, 70, 60, 50, 40 }.Select(v => ($"{v} %", v)),
            _settings.Opacity, v => _settings.Opacity = v));
        _menu.Items.Add(Choice(L.T("Text size", "Textgröße"),
            new[] { 0.8, 0.9, 1.0, 1.15, 1.3, 1.5 }.Select(v => ($"{v * 100:0} %", v)), _settings.Scale, v => _settings.Scale = v));
        _menu.Items.Add(Choice(L.T("Fight ends after", "Kampfende nach"),
            new[] { 4, 6, 8, 12, 20, 30 }.Select(v => (L.T($"{v} s without a hit", $"{v} s ohne Treffer"), v)), _settings.QuietSeconds,
            v =>
            {
                _settings.QuietSeconds = v;
                _settings.Apply(_engine);
            }));
        _menu.Items.Add(Choice(L.T("Language", "Sprache"), new[] { ("Deutsch", "de"), ("English", "en") },
            L.Lang, v =>
            {
                _settings.Language = v;
                L.Lang = v;
            }));
        _menu.Items.Add(new ToolStripSeparator());
        var status = _replayName is not null
            ? L.T("Replay: ", "Wiedergabe: ") + _replayName
            : _pipe.Connected
                ? L.T("Connected to Aion 2", "Mit Aion 2 verbunden")
                : L.T("No Aion 2 connection yet", "Noch keine Aion-2-Verbindung");
        _menu.Items.Add(new ToolStripMenuItem(status) { Enabled = false });
        _menu.Items.Add(new ToolStripMenuItem($"{L.AppName} {Program.Version}") { Enabled = false });
        if (_updater is not null)
        {
            _menu.Items.Add(Item(L.T("Automatic updates", "Automatische Updates"), () =>
            {
                _settings.AutoUpdate = !_settings.AutoUpdate;
                _settings.Save();
            }, _settings.AutoUpdate));
            _menu.Items.Add(Item(_updater.Available is { } r
                ? L.T($"Install update {r.Version.ToString(3)} now", $"Update {r.Version.ToString(3)} jetzt installieren")
                : L.T("Check for updates", "Nach Updates suchen"), UpdateClicked));
        }
        _menu.Items.Add(Item(L.T("Exit", "Beenden"), ExitThread));
    }

    private async void UpdateClicked()
    {
        if (_updater is null) return;
        if (_updater.Available is null)
        {
            if (!await _updater.CheckAsync())
                _tray.ShowBalloonTip(4000, L.AppName, L.T($"You have the latest version ({Program.Version}).",
                    $"Du hast die neueste Version ({Program.Version})."), ToolTipIcon.Info);
            return;
        }
        if (_updater.Available is { Installable: false } page)
        {
            TrayApp.Open(page.PageUrl);
            return;
        }
        await _updater.InstallAsync(); // quits on success; Failed shows the reason otherwise
    }

    protected override void ExitThreadCore()
    {
        try
        {
            _meter.SaveBounds();
            _detail.SaveBounds();
            _settings.Save();
            _cts.Cancel();
            _updater?.Dispose();
            _sniffer?.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write("Beenden", ex);
        }
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    private bool _disposed;

    /// <summary>Runs once: WinForms disposes the context after Application.Run, and the caller's using does it again.</summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            _timer.Dispose();
            _saveTimer?.Dispose();
            _hotkeys.Dispose();
            _tray.Dispose();
            _sniffer?.Dispose();
            _pipe.Dispose();
            _meter.Dispose();
            _detail.Dispose();
            _menu.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Global hotkeys Ctrl+Alt+key (a key another program already took is silently left out).</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    private readonly Dictionary<int, Action> _actions = new();

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public void Register(int id, char key, Action action)
    {
        if (RegisterHotKey(Handle, id, MOD_ALT | MOD_CONTROL | MOD_NOREPEAT, key)) _actions[id] = action;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var action)) action();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(Handle, id);
        DestroyHandle();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
