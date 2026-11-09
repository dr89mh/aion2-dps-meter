using System.Globalization;
using System.Runtime.InteropServices;
using DpsMeter.App;
using DpsMeter.Meter;

namespace DpsMeter;

internal static class Program
{
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0";

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        // language: from the settings, "--lang en|de" overrides it for this run
        L.Lang = Settings.Load().Language;
        if (args.Length > 0 && args[0] == Updater.InstallFlag) return Updater.RunInstaller(args[1..]);
        var afterUpdate = args.Contains(Updater.AfterUpdateFlag);
        args = args.Where(a => a != Updater.AfterUpdateFlag).ToArray();
        var lang = Array.IndexOf(args, "--lang");
        if (lang >= 0 && lang + 1 < args.Length)
        {
            L.Lang = args[lang + 1];
            args = args.Where((_, i) => i != lang && i != lang + 1).ToArray();
        }
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (cmd)
        {
            case "":
                return TrayApp.Run(replayFile: null, speed: 1, afterUpdate);
            case "replay":
            {
                if (args.Length < 2) return Usage();
                var speed = 1.0;
                for (var i = 2; i < args.Length - 1; i++)
                    if (args[i] == "--speed" && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                        speed = s;
                return TrayApp.Run(replayFile: args[1], speed);
            }
            case "bericht":
            case "report":
                Console();
                return ReportCommand.Run(args[1..], System.Console.Out);
            case "vorschau":
            case "preview":
                Console();
                return PreviewCommand.Run(args[1..], System.Console.Out);
            case "spur":
            case "trace":
                Console();
                return TraceCommand.Run(args[1..], System.Console.Out);
            default:
                return Usage();
        }
    }

    private static void Console()
    {
        AttachConsole(-1);
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;
        System.Console.WriteLine();
    }

    private static int Usage()
    {
        Console();
        System.Console.WriteLine(L.T("""
            DPS Meter for Aion 2
              DpsMeter                                  live (meter overlay, icon in the notification area)
              DpsMeter replay <file.pcapng> [--speed 4] play a recording in the meter (for testing)
              DpsMeter report <file.pcapng …> [--skills] [--min 20] [--diag]
                                                        every fight as text, boss damage against boss HP
              DpsMeter preview <file.pcapng> [--fight N] [--player name] [--out folder] [--anonymous]
                                                        meter and skill details as PNG images
              --lang en|de                              language for this run
            Hotkeys: Ctrl+Alt+D show/hide, Ctrl+Alt+N new fight, Ctrl+Alt+L lock (clicks go to the game)
            """, """
            DPS-Meter für Aion 2
              DpsMeter                                  Live-Betrieb (Meter-Overlay, Symbol im Infobereich)
              DpsMeter replay <datei.pcapng> [--speed 4] Aufnahme im Meter abspielen (zum Testen)
              DpsMeter bericht <datei.pcapng …> [--skills] [--min 20] [--diag]
                                                        jeder Kampf als Text, Boss-Schaden gegen Boss-HP
              DpsMeter vorschau <datei.pcapng> [--kampf N] [--spieler Name] [--out ordner] [--anonym]
                                                        Meter und Skill-Details als PNG-Bilder
              --lang en|de                              Sprache für diesen Aufruf
            Tasten: Strg+Alt+D zeigen/ausblenden, Strg+Alt+N neuer Kampf, Strg+Alt+L fixieren (Klicks ans Spiel)
            """));
        return 2;
    }
}
