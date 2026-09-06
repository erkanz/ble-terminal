using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace BLESerialTerminal;

internal static class RuntimeDiagnostics
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BLESerialTerminal",
        "diagnostics");

    public static string LogFilePath => Path.Combine(DirectoryPath, "runtime.log");

    public static void Write(string area, string message)
    {
        WriteCore(area, message, null, fatal: false);
    }

    public static void Write(string area, Exception exception, bool fatal = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WriteCore(area, exception.Message, exception, fatal);
    }

    private static void WriteCore(string area, string message, Exception? exception, bool fatal)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();

                var sb = new StringBuilder();
                sb.Append(DateTimeOffset.UtcNow.ToString("O"));
                sb.Append(" | ").Append(string.IsNullOrWhiteSpace(area) ? "GENERAL" : area.Trim());
                sb.Append(" | fatal=").Append(fatal ? "YES" : "NO");
                sb.Append(" | pid=").Append(Environment.ProcessId);
                sb.Append(" | version=").Append(CurrentVersion());
                sb.Append(" | ").Append(message ?? string.Empty);
                sb.AppendLine();

                if (exception != null)
                {
                    sb.Append("HRESULT=0x").Append(exception.HResult.ToString("X8")).AppendLine();
                    sb.AppendLine(exception.ToString());
                }

                File.AppendAllText(LogFilePath, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never become a second failure path.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var file = new FileInfo(LogFilePath);
            if (!file.Exists || file.Length < MaxLogBytes)
                return;

            string previous = Path.Combine(DirectoryPath, "runtime.previous.log");
            if (File.Exists(previous))
                File.Delete(previous);
            File.Move(LogFilePath, previous);
        }
        catch
        {
            // Rotation is best-effort. Appending to the current file is still preferable.
        }
    }

    private static string CurrentVersion()
    {
        try
        {
            Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
