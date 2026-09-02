using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The pinned determinism battery. <see cref="Fingerprint"/> reduces a whole simulation run to one
/// hash, so two builds compare byte for byte, and <see cref="TheBattery_MatchesItsPinnedFingerprint"/>
/// holds eighteen seeds against the values recorded here.
/// <para>
/// The hash covers the counters this method names one by one, every final job in the order the
/// Simulator returned it, and every recorded Transition Log entry. Naming the counters is deliberate.
/// A render that reflects over <c>SimulationResult</c> re-pins all eighteen hashes whenever the type
/// gains a member, even a member that is zero on every seed, and that noise hides the drift the pin
/// exists to catch.
/// </para>
/// <para>
/// A failure here is not automatically a defect. It says a run moved that was not meant to move.
/// Find out which change moved it, then either revert that change or re-pin the line and say why in
/// the commit message. Set <c>BACKWAVE_FINGERPRINT_OUT</c> to a file path to dump fresh values.
/// </para>
/// </summary>
public class SimulationFingerprintTests
{
    /// <summary>
    /// Every pinned seed and the fingerprint it produces on default options. The ten leading seeds are
    /// the ones the wider battery uses. The eight that follow were added as regressions were found.
    /// </summary>
    internal static readonly (ulong Seed, string Fingerprint)[] Battery =
    [
        (1UL, "503f252023d13ffda879b4e92a25d726278ff11995879227906f7905ccb8a10e"),
        (2UL, "83e417912d65ba6d71b9c54d553e8904a03427ad237aae757e3019251ae80c54"),
        (3UL, "aaeb9436fd6ac8f559c1407351693e9275bc3efdcf09c6ce5729a7fb5feb4bc0"),
        (1337UL, "4bfa7a596ff6552560eb6d538dad73888f37e2d957a1d698fa05f1a096a8eaf9"),
        (0xDEADBEEFUL, "6c0df0d64773d5e08147904c91e6d150fb3d7f38f8cbfa2fb617195d50fe202d"),
        (42_424_242UL, "2143df7453dd170c8afa98c71073a19dca9866d175061cf49ba62998ce92796c"),
        (987_654_321UL, "5797a0b70c32cce5fe1c8bbd429b3ecb6e89dbdfd8dcba554335537a5fcdaf25"),
        (0x5EED_5EED_5EEDUL, "e600d31963828be537fec93c375768d70ded504af11f9cb445bfe8ae2c4aca79"),
        (2026_06_10UL, "00cb132511e9aa28dc57ca7a3a9387086428f36b627718b6a05387da408abc77"),
        (ulong.MaxValue, "0b36994f84173a18512a94dd05bb65b243db655c173b6f15b575bae41924a669"),
        (7UL, "bf6b40332eb51a98265fa37f81ebe6c0250d85542fabe9c726d9535d3fde0c31"),
        (8UL, "7d7b5f3e57b7e5c28ae3f7bf7109928f0efee1a7257e4dc0e8a0a830d5a05a05"),
        (9UL, "4fb35ea8c4fecfdc11547633ef1599dff8884ab804edbf743f2d46fb6a4f8d8a"),
        (11UL, "3bdf063bf40f504be46b9747c087b66cc83a65ddc55096c7bfef829076d40e2d"),
        (99UL, "3863ca1bbf7bb6f518b8aacb87876a6016eb7334da208dec7af03072ea0ceeda"),
        (123UL, "ed093aaf90d92cb7141ce8d97659c877a39e9ee82555347a03b987de5ce1aee6"),
        (555UL, "9dce4e583810f8669a224ebd6e9d1d6303b652c4f762ee8771d1bdb7b6d6882d"),
        (4096UL, "a89da6a9db1d02a0e1fb272630894b2fe4d10164173e3fbbf776ee35ba048b4b"),
    ];

    internal static ulong[] BatterySeeds => [.. Battery.Select(entry => entry.Seed)];

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

    /// <summary>
    /// The hard constraint the whole simulation effort carries: a change that is not meant to move a
    /// run must leave every one of these eighteen hashes alone. Compares the whole list in one
    /// assertion, so a failure names every seed that drifted rather than only the first.
    /// </summary>
    [Fact]
    public void TheBattery_MatchesItsPinnedFingerprint()
    {
        var actual = new List<string>();
        foreach (var (seed, _) in Battery)
        {
            var result = new Simulator(new SimulationOptions { Seed = seed }).Run();
            actual.Add($"{seed}\t{Fingerprint(result)}");
        }

        var outPath = Environment.GetEnvironmentVariable("BACKWAVE_FINGERPRINT_OUT");
        if (!string.IsNullOrEmpty(outPath))
        {
            File.WriteAllLines(outPath, actual);
        }

        Assert.Equal(Battery.Select(entry => $"{entry.Seed}\t{entry.Fingerprint}"), actual);
    }
}
