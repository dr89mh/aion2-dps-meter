using System.Diagnostics;
using CubeRadar.Core.Capture;
using CubeRadar.Core.Dump;
using CubeRadar.Core.Protocol;
using DpsMeter.Meter;
using DpsMeter.Protocol;
using SharpPcap;

namespace DpsMeter.App;

/// <summary>Packets → TCP streams → game frames (Cube Radar core) → <see cref="GameEvent"/>s → <see cref="MeterEngine"/>.</summary>
public sealed class Pipeline : IDisposable
{
    private readonly MeterEngine _engine;
    private readonly DumpSession _session;
    private readonly List<GameEvent> _events = new();

    public Pipeline(MeterEngine engine, bool tickWithPackets = false)
    {
        _engine = engine;
        TickWithPackets = tickWithPackets;
        _session = new DumpSession(OpcodeBook.Empty, new DumpOptions { WriteHeartbeats = false }, jsonlPath: null);
        _session.Tap = (GameFlow flow, in FrameMeta meta, ReadOnlySpan<byte> body) =>
        {
            _events.Clear();
            CombatDecoder.Decode(meta.Opcode, body, flow.TimeMs, _events);
            foreach (var e in _events) _engine.OnEvent(e);
        };
        _session.FlowLocked += _ => Connected = true;
    }

    /// <summary>Replay / report: advance the engine clock with every packet instead of a wall clock.</summary>
    public bool TickWithPackets { get; }

    /// <summary>A game stream has been recognised.</summary>
    public bool Connected { get; private set; }

    public long Packets => _session.Packets;

    public long LastPacketMs => _session.LastPacketMs;

    public void OnPacket(RawCapture raw, string iface, string? desc = null)
    {
        try
        {
            _session.OnPacket(raw, iface, desc);
            if (TickWithPackets) _engine.Tick((long)raw.Timeval.Seconds * 1000 + (long)raw.Timeval.MicroSeconds / 1000);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write("Paket", ex); // one bad packet must not stop the capture thread
        }
    }

    /// <summary>Feeds a capture file. <paramref name="speed"/> 0 = as fast as possible, 1 = real time.</summary>
    public static Task Replay(Pipeline p, string path, double speed, ReplayClock clock, CancellationToken ct) => Task.Run(() =>
    {
        var real = Stopwatch.StartNew();
        long? first = null;
        CaptureFileSource.ReadAll(path, raw =>
        {
            var ms = (long)raw.Timeval.Seconds * 1000 + (long)raw.Timeval.MicroSeconds / 1000;
            if (first is null)
            {
                first = ms;
                clock.Start(ms, speed);
            }
            if (speed > 0)
            {
                var wait = (ms - first.Value) / speed - real.ElapsedMilliseconds;
                if (wait > 1) ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(wait, 60_000)));
            }
            p.OnPacket(raw, "replay");
        }, ct);
        clock.Finished = true;
    }, ct);

    public void Dispose() => _session.Dispose();
}

/// <summary>The engine clock: wall clock live, capture time during a replay (0 until the first packet).</summary>
public sealed class ReplayClock(bool live)
{
    private readonly Stopwatch _real = new();
    private long _firstMs;
    private double _speed = 1;
    private bool _started;

    public bool Live { get; } = live;

    public volatile bool Finished;

    public void Start(long firstPacketMs, double speed)
    {
        _firstMs = firstPacketMs;
        _speed = speed <= 0 ? 1 : speed;
        _real.Restart();
        _started = true;
    }

    public long Now() => Live
        ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        : _started ? _firstMs + (long)(_real.ElapsedMilliseconds * _speed) : 0;
}
