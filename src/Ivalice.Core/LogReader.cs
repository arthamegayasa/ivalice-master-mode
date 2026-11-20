using System.Text.RegularExpressions;

namespace Ivalice.Core;

public sealed record LogSummary(
    string Path, DateTime WrittenAt, IReadOnlyList<string> LoadedModIds,
    IReadOnlyList<string> Problems, IReadOnlyList<string> Lines);

/// <summary>Reads the most recent Reloaded-II session log and picks out which mods loaded and what went wrong.</summary>
public sealed partial class LogReader(ReloadedLayout layout)
{
    [GeneratedRegex(@"\[Reloaded\] - AppId\s*:\s*(\S+)")]
    private static partial Regex AppIdLine();

    [GeneratedRegex(@"not found|exception\b|failed|could not|unable to|\berror\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProblemLine();

    // The mod loader tries two signatures for this function and logs the miss of the first one
    // even when the alternative is found, so on its own this line is not a problem.
    private static readonly string[] KnownHarmless = ["Pattern CFILE_DAT__Decode not found"];

    /// <param name="processName">Game process name without extension; only its logs are considered.</param>
    public LogSummary? ReadLatest(string processName)
    {
        if (!Directory.Exists(layout.LogsDirectory)) return null;

        var latest = new DirectoryInfo(layout.LogsDirectory)
            .EnumerateFiles($"*~ {processName}.txt")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest is null) return null;

        string[] lines;
        try
        {
            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (IOException)
        {
            return null;
        }

        var loaded = lines.Select(l => AppIdLine().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var problems = lines
            .Where(l => ProblemLine().IsMatch(l) && !KnownHarmless.Any(h => l.Contains(h, StringComparison.Ordinal)))
            .ToList();

        return new LogSummary(latest.FullName, latest.LastWriteTime, loaded, problems, lines);
    }
}
