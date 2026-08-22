namespace BackWave.Benchmarks.Latency;

/// <summary>
/// The round-trip delay dial: a known, uniform cost added to every database round trip the harness makes,
/// so a run can be repeated at a realistic hop cost without provisioning a second machine.
/// <para>
/// It exists because round-trip <em>count</em> is not equal across storage adapters. On loopback a hop is
/// nearly free, so an adapter that spends thirty round trips where another spends one still looks healthy.
/// Turning the dial up prices those hops and makes the difference visible.
/// </para>
/// <para>
/// It is a diagnostic mode, never a publication mode. A run with the dial engaged is refused in official
/// mode, is stamped into the environment manifest, and can never carry a publishable number.
/// </para>
/// </summary>
public sealed record LatencyProfile
{
    /// <summary>
    /// The floor for an engaged dial. The proxy waits with the task timer, which rounds a delay down to
    /// whole milliseconds, so a sub-millisecond setting would silently become no delay at all.
    /// </summary>
    public static readonly TimeSpan MinimumRoundTrip = TimeSpan.FromMilliseconds(1);

    /// <summary>The dial in its off position: no proxy, no added delay, the default for every run.</summary>
    public static readonly LatencyProfile Disabled = new();

    /// <summary>The delay added to each database round trip. <see cref="TimeSpan.Zero"/> when the dial is off.</summary>
    public TimeSpan RoundTrip { get; private init; } = TimeSpan.Zero;

    /// <summary>The delay in milliseconds, as recorded in the result output.</summary>
    public double RoundTripMs => RoundTrip.TotalMilliseconds;

    /// <summary>True when a delay is actually being added, which makes the run a diagnostic one.</summary>
    public bool IsEngaged => RoundTrip > TimeSpan.Zero;

    /// <summary>
    /// Builds a profile from a millisecond setting. Zero returns <see cref="Disabled"/>. Anything above
    /// zero but below <see cref="MinimumRoundTrip"/> is rejected rather than rounded down, because rounding
    /// it down would hand back a run that looks delayed and is not.
    /// </summary>
    /// <param name="milliseconds">The delay to add to each round trip, in milliseconds.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, or is between zero and the floor.</exception>
    public static LatencyProfile OfMilliseconds(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(milliseconds), milliseconds, "The round-trip delay cannot be negative.");
        }

        if (milliseconds == 0)
        {
            return Disabled;
        }

        var roundTrip = TimeSpan.FromMilliseconds(milliseconds);
        if (roundTrip < MinimumRoundTrip)
        {
            throw new ArgumentOutOfRangeException(
                nameof(milliseconds),
                milliseconds,
                $"The round-trip delay must be 0 (off) or at least {MinimumRoundTrip.TotalMilliseconds:0}ms. " +
                "The proxy waits on the task timer, which would round a smaller value down to no delay.");
        }

        return new LatencyProfile { RoundTrip = roundTrip };
    }
}
