using BackWave.Dashboard;

namespace BackWave.Dashboard.Tests;

public class CronFriendlyTests
{
    [Theory]
    // Minutes.
    [InlineData("0 * * * * *", "Every minute")]
    [InlineData("0 */5 * * * *", "Every 5 minutes")]
    [InlineData("0 30 * * * *", "Every hour at :30")]
    // Hours.
    [InlineData("0 0 * * * *", "Every hour")]
    [InlineData("0 0 */3 * * *", "Every 3 hours")]
    // Daily / weekly / monthly at a fixed time.
    [InlineData("0 0 2 * * *", "Daily at 02:00")]
    [InlineData("0 0 8 * * 1", "Weekly on Monday at 08:00")]
    [InlineData("0 0 8 * * 0", "Weekly on Sunday at 08:00")]
    [InlineData("0 0 8 * * 7", "Weekly on Sunday at 08:00")]
    [InlineData("0 30 6 1 * *", "Monthly on the 1st at 06:30")]
    [InlineData("0 0 0 23 * *", "Monthly on the 23rd at 00:00")]
    public void Glosses_common_shapes(string canonical, string expected)
        => Assert.Equal(expected, DashboardGlossary.CronFriendly(canonical));

    [Theory]
    [InlineData("30 * * * * *")]      // non-zero seconds field
    [InlineData("0 0 8 * 6 *")]       // month-restricted
    [InlineData("0 0 8 1-5 * *")]     // day-of-month range
    [InlineData("0 0 8,20 * * *")]    // hour list
    [InlineData("0 0 8 * * 1-5")]     // weekday range
    [InlineData("not a cron")]        // malformed
    public void Falls_through_to_null_for_uncommon_or_malformed(string canonical)
        => Assert.Null(DashboardGlossary.CronFriendly(canonical));
}
