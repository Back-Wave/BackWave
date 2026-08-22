using BackWave.Benchmarks.Targets;

namespace BackWave.Benchmarks.Latency;

/// <summary>
/// Puts the latency proxy in the path between the harness and its database.
/// <para>
/// Every target reads its DSN from an environment variable when it is constructed, so the dial is
/// installed by starting a proxy in front of the real endpoint and rewriting that variable to the proxy's
/// loopback port. No target, store, or adapter changes: the delay is charged on the wire, which is the
/// only place that prices a round trip rather than a call.
/// </para>
/// </summary>
public static class LatencyProfileInstaller
{
    /// <summary>
    /// Installs the dial for <paramref name="target"/> and returns the running proxy, or
    /// <see langword="null"/> when the dial is off. A disabled profile touches nothing at all: no listener
    /// is opened and no connection string is rewritten.
    /// </summary>
    /// <param name="target">The <c>--target</c> name whose DSN should be routed through the proxy.</param>
    /// <param name="profile">The dial setting.</param>
    /// <exception cref="InvalidOperationException">The dial is engaged but the target's DSN is not set.</exception>
    public static LatencyProxy? Install(string target, LatencyProfile profile)
        => Install(
            target,
            profile,
            System.Environment.GetEnvironmentVariable,
            (name, value) => System.Environment.SetEnvironmentVariable(name, value));

    /// <summary>
    /// The form that takes the environment as two delegates, so the install can be exercised without
    /// mutating the process environment.
    /// </summary>
    /// <param name="target">The <c>--target</c> name whose DSN should be routed through the proxy.</param>
    /// <param name="profile">The dial setting.</param>
    /// <param name="readSetting">Reads an environment variable.</param>
    /// <param name="writeSetting">Writes an environment variable.</param>
    /// <exception cref="InvalidOperationException">The dial is engaged but the target's DSN is not set.</exception>
    public static LatencyProxy? Install(
        string target,
        LatencyProfile profile,
        Func<string, string?> readSetting,
        Action<string, string> writeSetting)
    {
        if (!profile.IsEngaged)
        {
            return null;
        }

        var descriptor = BenchmarkTargetRegistry.Describe(target);
        var connectionString = readSetting(descriptor.ConnectionStringEnvVar);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // The built-in default DSN is never routed: the dial rewrites a connection string, and rewriting
            // one the operator never stated would hide which database the diagnostic run actually hit.
            throw new InvalidOperationException(
                $"The latency profile reroutes the '{descriptor.Name}' connection string, so " +
                $"${descriptor.ConnectionStringEnvVar} must be set explicitly before the dial is engaged.");
        }

        var upstream = ConnectionStringEndpoint.Read(descriptor.Driver, connectionString);
        var proxy = LatencyProxy.Start(upstream.Host, upstream.Port, profile.RoundTrip);
        writeSetting(
            descriptor.ConnectionStringEnvVar,
            ConnectionStringEndpoint.Rewrite(
                descriptor.Driver, connectionString, new DatabaseEndpoint("127.0.0.1", proxy.Port)));
        return proxy;
    }
}
