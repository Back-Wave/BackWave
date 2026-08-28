using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BackWave.Tests.Simulation;

/// <summary>
/// dst-0001 spike instrument: turns a whole simulation run into one hash so two builds can be
/// compared byte for byte. The hash covers the run counters, every final job in the order the
/// Simulator returned it, and every recorded Transition Log entry. Set
/// <c>BACKWAVE_FINGERPRINT_OUT</c> to a file path and run this test to dump the battery.
/// </summary>
public class SpikeFingerprintTests
{
    internal static readonly ulong[] BatterySeeds =
    [
        1UL, 2UL, 3UL, 1337UL, 0xDEADBEEFUL, 42_424_242UL, 987_654_321UL,
        0x5EED_5EED_5EEDUL, 2026_06_10UL, ulong.MaxValue,
        7UL, 8UL, 9UL, 11UL, 99UL, 123UL, 555UL, 4096UL,
    ];

    internal static string Fingerprint(SimulationResult result)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"seed={result.Seed}\n");
        text.Append(CultureInfo.InvariantCulture, $"steps={result.Steps}\n");
        text.Append(CultureInfo.InvariantCulture, $"crashes={result.Crashes}\n");
        text.Append(CultureInfo.InvariantCulture, $"stale={result.StaleOutcomes}\n");
        text.Append(CultureInfo.InvariantCulture, $"virtual={result.VirtualElapsed.Ticks}\n");
        text.Append(CultureInfo.InvariantCulture, $"polls={result.PollCount}\n");
        text.Append(CultureInfo.InvariantCulture, $"expired={result.LeasesExpired}\n");
        text.Append(CultureInfo.InvariantCulture, $"acklosses={result.AckLosses}\n");
        text.Append(CultureInfo.InvariantCulture, $"bufferdropped={result.OutcomeBufferDropped}\n");
        foreach (var job in result.FinalJobs)
        {
            text.Append(CultureInfo.InvariantCulture, $"job={job.JobId:N},{job.State},{job.Attempt}\n");
        }
        foreach (var entry in result.FinalTransitions)
        {
            foreach (var step in entry.Timeline)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"tr={entry.JobId:N},{step.Timestamp.UtcTicks},{step.State},{step.Attempt}\n");
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    [Fact]
    public void TheBattery_HasAStableFingerprintPerSeed()
    {
        var lines = new List<string>();
        foreach (var seed in BatterySeeds)
        {
            var result = new Simulator(new SimulationOptions { Seed = seed }).Run();
            lines.Add($"{seed}\t{Fingerprint(result)}");
        }

        var outPath = Environment.GetEnvironmentVariable("BACKWAVE_FINGERPRINT_OUT");
        if (!string.IsNullOrEmpty(outPath))
        {
            File.WriteAllLines(outPath, lines);
        }

        Assert.Equal(BatterySeeds.Length, lines.Count);
    }
}
