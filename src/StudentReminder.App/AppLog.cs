using System.IO;

namespace StudentReminder.App;

public enum Operation { Startup, Import, Refresh, Failure }

/// <summary>Only operation names, numeric counts and exception types; never exception messages.</summary>
public sealed class AppLog(string dataDirectory)
{
    public void Write(Operation operation, int added = 0, int duplicates = 0, string? errorType = null)
    {
        try
        {
            var logDirectory = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            var line = $"{DateTimeOffset.UtcNow:O} {operation} added={added} duplicates={duplicates} error_type={errorType ?? "none"}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logDirectory, $"app-{DateTime.UtcNow:yyyyMMdd}.log"), line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
