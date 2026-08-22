using System.Runtime.InteropServices;
using BackWave.Benchmarks.Latency;

namespace BackWave.Benchmarks.Environment;

/// <summary>
/// The run mode a benchmark was produced under. Only <see cref="Official"/> on a declared
/// native-x86-64 host can ever yield a publishable number; everything else is indicative-only.
/// </summary>
public enum RunMode
{
    /// <summary>The maintainer's dev machine — indicative only, never published (ADR 0027 §8).</summary>
    Local,

    /// <summary>A pinned, documented native-x86-64 instance — the only publishable source.</summary>
    Official,
}

/// <summary>
/// The self-labelling environment stamp carried by every result so a number is reproducible and a
/// laptop figure can never be quoted as official by accident. The <see cref="Publishable"/> flag is
/// derived purely from the captured facts (run mode + architecture) — see <see cref="DerivePublishable"/>.
/// </summary>
public sealed record EnvironmentManifest
{
    /// <summary>Operating-system description (e.g. the RID-ish OS string).</summary>
    public required string Os { get; init; }

    /// <summary>Process architecture (X64, Arm64, …). Apple Silicon reports Arm64 natively, but X64 under Rosetta.</summary>
    public required Architecture ProcessArchitecture { get; init; }

    /// <summary>
    /// True when the x64 process is Rosetta-translated on Apple Silicon (so <see cref="ProcessArchitecture"/>
    /// reports X64 but the silicon is not native). This is the trap the publishable guard must close:
    /// a Rosetta-emulated SQL Server measures the emulator, not the engine (ADR 0027 §8).
    /// </summary>
    public required bool IsRosettaEmulated { get; init; }

    /// <summary>The .NET runtime version that produced the run.</summary>
    public required string DotnetVersion { get; init; }

    /// <summary>Logical processor count of the host.</summary>
    public required int ProcessorCount { get; init; }

    /// <summary>The storage engine under test (e.g. "PostgreSQL", "SQL Server").</summary>
    public required string DbEngine { get; init; }

    /// <summary>The storage engine's reported version string.</summary>
    public required string DbVersion { get; init; }

    /// <summary>The run mode (local vs official).</summary>
    public required RunMode Mode { get; init; }

    /// <summary>
    /// The delay the latency-profile dial added to every database round trip, in milliseconds. Zero means
    /// the dial was off, which is the only setting an official run may carry. Any other value marks the
    /// result a diagnostic and forces <see cref="Publishable"/> to false.
    /// </summary>
    public required double RoundTripDelayMs { get; init; }

    /// <summary>
    /// True only when this number may be published: <see cref="RunMode.Official"/> on a native-x86-64
    /// (<see cref="Architecture.X64"/>) host with the latency-profile dial off. Local mode, Apple Silicon,
    /// Rosetta emulation, and any added round-trip delay are always false. Derived, never set
    /// independently - the credibility guard (ADR 0027 §8).
    /// </summary>
    public required bool Publishable { get; init; }

    /// <summary>
    /// The single source of truth for the publishable rule: official mode AND a native-x86-64 process
    /// architecture (<see cref="Architecture.X64"/>) that is NOT Rosetta-emulated AND the latency-profile
    /// dial off. Anything else - local mode, any non-X64 arch (including Apple Silicon Arm64), a
    /// Rosetta-translated x64 process, or an added round-trip delay - is unpublishable.
    /// </summary>
    /// <param name="mode">The requested run mode.</param>
    /// <param name="processArchitecture">The live process architecture.</param>
    /// <param name="isRosettaEmulated">Whether the x64 process is Rosetta-translated on Apple Silicon.</param>
    /// <param name="latency">The latency-profile dial the run was produced under.</param>
    public static bool DerivePublishable(
        RunMode mode, Architecture processArchitecture, bool isRosettaEmulated, LatencyProfile latency)
        => mode == RunMode.Official
            && processArchitecture == Architecture.X64
            && !isRosettaEmulated
            && !latency.IsEngaged;

    /// <summary>
    /// Captures the live environment for <paramref name="mode"/> against the given DB engine/version,
    /// deriving <see cref="Publishable"/> from the captured architecture, Rosetta state, and latency dial.
    /// </summary>
    /// <param name="mode">The requested run mode.</param>
    /// <param name="dbEngine">The storage engine under test.</param>
    /// <param name="dbVersion">The storage engine's reported version string.</param>
    /// <param name="latency">The latency-profile dial the run was produced under.</param>
    public static EnvironmentManifest Capture(
        RunMode mode, string dbEngine, string dbVersion, LatencyProfile latency)
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        var rosetta = DetectRosetta();
        return new EnvironmentManifest
        {
            Os = RuntimeInformation.OSDescription,
            ProcessArchitecture = arch,
            IsRosettaEmulated = rosetta,
            DotnetVersion = RuntimeInformation.FrameworkDescription,
            ProcessorCount = System.Environment.ProcessorCount,
            DbEngine = dbEngine,
            DbVersion = dbVersion,
            Mode = mode,
            RoundTripDelayMs = latency.RoundTripMs,
            Publishable = DerivePublishable(mode, arch, rosetta, latency),
        };
    }

    // Rosetta translates an x64 process on Apple Silicon: the macOS `sysctl.proc_translated` flag reads 1
    // for such a process and 0 (or absent) for a native one. On any non-macOS host this is always false.
    private static bool DetectRosetta()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }

        try
        {
            nint size = sizeof(int);
            if (sysctlbyname("sysctl.proc_translated", out var translated, ref size, IntPtr.Zero, 0) != 0)
            {
                return false;
            }

            return translated == 1;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname(string name, out int oldp, ref nint oldlenp, IntPtr newp, nint newlen);
}
