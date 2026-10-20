namespace Shortcutz;

internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static StreamWriter? _writer;

    public static string FilePath { get; private set; } = "";

    public static void Start()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shortcutz");
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, "diagnostics.log");
            _writer = new StreamWriter(new FileStream(
                FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Write($"START {DateTimeOffset.Now:O}");
        }
        catch
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            lock (Sync)
            {
                if (_writer is not null) _writer.Write(line);
                else if (FilePath.Length > 0) File.AppendAllText(FilePath, line);
            }
        }
        catch { }
    }

    public static void WriteException(string source, Exception exception) =>
        Write($"EXCEPTION source={source}{Environment.NewLine}{exception}");

    public static void Stop()
    {
        try
        {
            lock (Sync)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
        catch { }
    }
}
