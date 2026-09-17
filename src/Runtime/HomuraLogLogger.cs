using Godot;

namespace HomuraLog.Runtime;

internal sealed class HomuraLogLogger
{
    private readonly string _path;
    private readonly object _gate = new();

    public HomuraLogLogger()
    {
        string directory = Path.Combine(OS.GetUserDataDir(), "HomuraLog", "logs");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "HomuraLog.log");
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            string line = $"{DateTimeOffset.Now:O} [{level}] {message}{System.Environment.NewLine}";
            lock (_gate) File.AppendAllText(_path, line);
            GD.Print($"[HomuraLog] {message}");
        }
        catch
        {
            // Logging is observational and must never affect the game action queue.
        }
    }
}
