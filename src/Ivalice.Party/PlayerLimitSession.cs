using System.Text.Json;

namespace Ivalice.Party;

/// <summary>Whether all stat-limit hooks are ready in this particular run of the game.</summary>
public sealed record PlayerLimitSession(int ProcessId, long StartTimeUtcTicks, bool Ready)
{
    public static bool IsReady(string? json, int processId, long startTimeUtcTicks)
    {
        if (processId <= 0 || startTimeUtcTicks <= 0 || string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var report = JsonSerializer.Deserialize(json, BridgeJson.Default.PlayerLimitSession);
            return report is { Ready: true } && report.ProcessId == processId && report.StartTimeUtcTicks == startTimeUtcTicks;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
