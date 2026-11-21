namespace Ivalice.Party.Tests;

public class PlayerLimitSessionTests
{
    private const long Start = 639000000000000001;

    [Theory]
    [InlineData(102, Start, true)]
    [InlineData(103, Start, false)]
    [InlineData(102, Start + 1, false)]
    public void Ready_requires_the_same_game_process_and_start_time(int processId, long startedAt, bool expected)
    {
        const string report = """{"processId":102,"startTimeUtcTicks":639000000000000001,"ready":true}""";
        Assert.Equal(expected, PlayerLimitSession.IsReady(report, processId, startedAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"processId":102,"startTimeUtcTicks":639000000000000001,"ready":false}""")]
    [InlineData("""{"processId":102,"startTimeUtcTicks":639000000000000001,"ready":"yes"}""")]
    public void A_missing_damaged_or_inactive_report_leaves_extended_previews_off(string? report)
    {
        Assert.False(PlayerLimitSession.IsReady(report, 102, Start));
    }
}
