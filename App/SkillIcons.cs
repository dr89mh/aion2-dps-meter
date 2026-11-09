using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using DpsMeter.Meter;

namespace DpsMeter.App;

/// <summary>
/// Skill icons from the official Aion 2 game data CDN of NCSOFT, downloaded on first use and kept (64 px) in
/// %APPDATA%\DpsMeter\icons. Nothing is bundled with the program. Lookups never block: a missing icon starts a background
/// download and <see cref="IconReady"/> fires when it is there. Icon names as in A2Tools-DPS-Meter / aion2-dps-meter:
/// ICON_&lt;class&gt;_SKILL_[Passive_]&lt;nnn&gt;, plus a table for skills that use another icon (data/skill-icons.json).
/// </summary>
internal sealed class SkillIcons : IDisposable
{
    private const string BaseUrl = "https://assets.playnccdn.com/static-aion2-gamedata/resources/";
    private const int CacheSize = 64;

    private static readonly Dictionary<uint, string> ClassCodes = new()
    {
        [11] = "GL", [12] = "TE", [13] = "AS", [14] = "RA", [15] = "SO", [16] = "EL", [17] = "CL", [18] = "CH", [19] = "GT",
    };

    private readonly Dictionary<uint, string> _overrides = new(); // first four digits of an 8-digit code → icon
    private readonly ConcurrentDictionary<string, Image?> _loaded = new(); // null = the CDN has no such icon
    private readonly ConcurrentDictionary<string, Task> _pending = new();
    private readonly ConcurrentDictionary<string, DateTime> _retryAfter = new();
    private readonly SemaphoreSlim _parallel = new(4, 4);
    private readonly HttpClient _http;
    private readonly string _dir;

    public SkillIcons(bool enabled, string? cacheDir = null)
    {
        Enabled = enabled;
        _dir = cacheDir ?? AppPaths.Icons;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DpsMeter", Program.Version));
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("data/skill-icons.json");
        if (s is null) return;
        using var doc = JsonDocument.Parse(s);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (uint.TryParse(p.Name, out var prefix) && p.Value.GetString() is { Length: > 0 } name)
                _overrides[prefix] = name[(name.LastIndexOf('/') + 1)..]; // the CDN keeps every icon at the top level
    }

    /// <summary>Off: no icons are drawn and nothing is downloaded.</summary>
    public bool Enabled { get; set; }

    /// <summary>Raised on a worker thread when a downloaded icon has arrived.</summary>
    public event Action? IconReady;

    /// <summary>The icon file of a skill (without .png), or null when it has none (enemy skills, pets).</summary>
    public string? IconName(uint code)
    {
        // theostones come with 7 digits (3000017): one icon per group (3000…, 3001…)
        if (Skills.IsTheostone(code)) return _overrides.GetValueOrDefault(code / 1_000);
        if (code is < 10_000_000 or > 99_999_999) return null;
        if (_overrides.TryGetValue(code / 10_000, out var icon)) return icon;
        if (!Skills.IsClassSkill(code) || !ClassCodes.TryGetValue(code / 1_000_000, out var cls)) return null;
        var number = code / 10_000 % 100;
        if (number == 0) return null;
        var passive = number >= 70;
        return $"ICON_{cls}_SKILL_{(passive ? "Passive_" : "")}{(passive ? number - 70 : number):D3}";
    }

    /// <summary>The icon if it is already there; otherwise starts loading it and returns null.</summary>
    public Image? Get(uint code)
    {
        if (!Enabled || IconName(code) is not { } name) return null;
        if (_loaded.TryGetValue(name, out var img)) return img;
        Load(name);
        return null;
    }

    /// <summary>Loads the icons of these skills and waits for them (preview images).</summary>
    public Task FetchAsync(IEnumerable<uint> codes) =>
        Task.WhenAll(codes.Select(IconName).OfType<string>().Distinct().Where(n => !_loaded.ContainsKey(n)).Select(Load));

    private Task Load(string name)
    {
        if (_retryAfter.TryGetValue(name, out var until) && DateTime.UtcNow < until) return Task.CompletedTask;
        return _pending.GetOrAdd(name, n => Task.Run(async () =>
        {
            try
            {
                await LoadAsync(n).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(n, out _);
            }
        }));
    }

    private async Task LoadAsync(string name)
    {
        var file = Path.Combine(_dir, name + ".png");
        try
        {
            if (File.Exists(file))
            {
                _loaded[name] = FromFile(file);
                IconReady?.Invoke();
                return;
            }
            await _parallel.WaitAsync().ConfigureAwait(false);
            byte[] bytes;
            try
            {
                using var response = await _http.GetAsync(BaseUrl + Uri.EscapeDataString(name) + ".png").ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _loaded[name] = null;
                    return;
                }
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            finally
            {
                _parallel.Release();
            }
            using (var ms = new MemoryStream(bytes))
            using (var original = Image.FromStream(ms))
            using (var small = Scale(original, CacheSize))
            {
                Directory.CreateDirectory(_dir);
                var tmp = file + ".tmp";
                small.Save(tmp, ImageFormat.Png);
                File.Move(tmp, file, overwrite: true);
            }
            _loaded[name] = FromFile(file);
            IconReady?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or ArgumentException
                                       or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            // offline or a broken file: try again in a few minutes, no icon until then
            _retryAfter[name] = DateTime.UtcNow.AddMinutes(5);
            if (ex is ArgumentException or System.Runtime.InteropServices.ExternalException)
                try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Image FromFile(string file)
    {
        using var fs = File.OpenRead(file);
        using var img = Image.FromStream(fs);
        return new Bitmap(img); // a copy that does not keep the file open
    }

    private static Bitmap Scale(Image src, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark seam at the edges
        g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return bmp;
    }

    public void Dispose() => _http.Dispose();
}
