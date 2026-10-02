namespace Hoard.Setup;

/// <summary>
/// Writes to the console (visible when HoardSetup is run by hand) and to a timestamped file under
/// %ProgramData%\Hoard\logs. The MSI runs this as LocalSystem with no console, so the file is the
/// only record of what happened; it is the first thing to look at when an install fails.
/// </summary>
public static class Log
{
    private static StreamWriter? _file;

    public static string? Path { get; private set; }

    public static void Open(string directory, string verb)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, $"setup-{verb}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _file = new StreamWriter(Path, append: true) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"(could not open a log file in {directory}: {ex.Message})");
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex)
    {
        Write("ERROR", message);
        Write("ERROR", ex.ToString());
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {level} {message}";
        Console.WriteLine(line);
        _file?.WriteLine(line);
    }

    public static void Close()
    {
        _file?.Flush();
        _file?.Dispose();
        _file = null;
    }
}
