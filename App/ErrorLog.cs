namespace DpsMeter.App;

/// <summary>Appends unexpected exceptions to %APPDATA%\DpsMeter\fehler.log (at most 50 per run).</summary>
public static class ErrorLog
{
    private static readonly object Gate = new();
    private static int _count;

    public static void Write(string where, Exception ex)
    {
        lock (Gate)
        {
            if (++_count > 50) return;
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.AppendAllText(AppPaths.ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] Version {Program.Version}\n{ex}\n\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
