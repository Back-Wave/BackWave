using System.Runtime.InteropServices;
using System.Text.Json;
using BackWave.Benchmarks;
using BackWave.Benchmarks.Environment;
using BackWave.Benchmarks.Latency;
using BackWave.Benchmarks.Metrics;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// The latency-profile dial (bench-0265). Two things have to hold. The dial is off unless an operator asks
/// for it, so the official path is untouched. And a run with the dial engaged is marked as a diagnostic in
/// the result it writes, so no such number can be mistaken for an official one.
/// </summary>
public sealed class LatencyProfileTests
{
    [Fact]
    public void The_dial_is_off_unless_the_operator_asks_for_it()
    {
        // Not asserted from a constant: the parsed options are handed to the installer with an environment
        // that throws if it is read or written, and a listener that would show up as a non-null proxy. A
        // default run therefore provably starts nothing and reroutes nothing.
        string[][] runsWithoutTheDial =
        [
            [],
            ["--target", "oracle", "--jobs", "5000", "--mode", "local"],
            ["--target", "postgres", "--arrival", "sustained", "--rate", "500", "--out", "r.json"],
        ];

        foreach (var args in runsWithoutTheDial)
        {
            var options = BenchmarkOptions.Parse(args);

            Assert.False(options.Latency.IsEngaged);
            Assert.Equal(0d, options.Latency.RoundTripMs);
            Assert.Null(LatencyProfileInstaller.Install(
                options.Target,
                options.Latency,
                name => throw new Xunit.Sdk.XunitException($"the dial read ${name} with no --rtt-ms given"),
                (name, _) => throw new Xunit.Sdk.XunitException($"the dial rewrote ${name} with no --rtt-ms given")));
        }
    }

    [Fact]
    public void Parse_reads_the_dial_in_milliseconds()
    {
        var options = BenchmarkOptions.Parse(["--target", "oracle", "--rtt-ms", "3"]);

        Assert.True(options.Latency.IsEngaged);
        Assert.Equal(3d, options.Latency.RoundTripMs);
    }

    [Fact]
    public void A_sub_millisecond_dial_is_refused_rather_than_rounded_down_to_nothing()
    {
        // The proxy waits on the task timer, which truncates to whole milliseconds. Accepting 0.5ms would
        // hand back a run that reads as delayed and was not - the exact silent loss this dial must not have.
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => LatencyProfile.OfMilliseconds(0.5));

        Assert.Contains("at least 1ms", refusal.Message);
        Assert.False(LatencyProfile.Disabled.IsEngaged);
    }

    [Fact]
    public void Official_mode_refuses_a_run_with_the_dial_engaged()
    {
        // Refused up front, on the one host that would otherwise be eligible: native x86-64, no emulation.
        var refusal = Assert.Throws<OfficialModeNotSupportedException>(() => OfficialModeGuard.Assert(
            RunMode.Official, LatencyProfile.OfMilliseconds(2), Architecture.X64, isRosettaEmulated: false));

        Assert.Contains("latency profile", refusal.Message);
        Assert.Contains("--rtt-ms", refusal.Message);
    }

    [Fact]
    public void The_dial_refuses_to_reroute_a_connection_string_the_operator_never_stated()
    {
        // Targets carry a built-in default DSN. Rerouting one nobody stated would send a diagnostic run at
        // a database the operator did not name, and the result would still claim the target's name.
        var refusal = Assert.Throws<InvalidOperationException>(() => LatencyProfileInstaller.Install(
            "oracle",
            LatencyProfile.OfMilliseconds(1),
            _ => null,
            (name, _) => throw new Xunit.Sdk.XunitException($"the dial rewrote ${name} from an unset DSN")));

        Assert.Contains("BACKWAVE_ORACLE_DSN", refusal.Message);
    }

    [Fact]
    public void An_engaged_dial_marks_the_result_diagnostic_and_records_the_setting()
    {
        var result = ResultUnder(LatencyProfile.OfMilliseconds(7));

        Assert.True(result.Diagnostic);
        Assert.Equal(7d, result.Manifest.RoundTripDelayMs);
        Assert.False(result.Publishable);

        var clean = ResultUnder(LatencyProfile.Disabled);
        Assert.False(clean.Diagnostic);
        Assert.Equal(0d, clean.Manifest.RoundTripDelayMs);
    }

    [Fact]
    public void The_diagnostic_mark_reaches_the_json_the_run_writes()
    {
        // The mark is only worth anything if it is in the artifact a reader transcribes from, so this
        // checks the serialized document rather than the in-memory record.
        var json = JsonSerializer.SerializeToElement(ResultUnder(LatencyProfile.OfMilliseconds(7)));

        Assert.True(json.GetProperty("Diagnostic").GetBoolean());
        Assert.Equal(7d, json.GetProperty("Manifest").GetProperty("RoundTripDelayMs").GetDouble());
        Assert.False(json.GetProperty("Publishable").GetBoolean());
    }

    private static BenchmarkResult ResultUnder(LatencyProfile latency)
    {
        var spec = new WorkloadSpec { JobCount = 10 };
        return new BenchmarkResult
        {
            Target = "BackWave/Oracle",
            Engine = "Oracle",
            Workload = WorkloadSummary.From(spec),
            Manifest = EnvironmentManifest.Capture(RunMode.Local, "Oracle", "23.9", latency),
            TuningDials = new Dictionary<string, string>(),
            WarmupRuns = 1,
            MeasuredRuns = 3,
            ThroughputJobsPerSecond = new Distribution(1, 2, 3),
            EndToEndP50Ms = new Distribution(1, 2, 3),
            EndToEndP99Ms = new Distribution(1, 2, 3),
            EnqueueP50Ms = new Distribution(1, 2, 3),
            EnqueueP99Ms = new Distribution(1, 2, 3),
            Resources = ResourceMetrics.None,
        };
    }
}
