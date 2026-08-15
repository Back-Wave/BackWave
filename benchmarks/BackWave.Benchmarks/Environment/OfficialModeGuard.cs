using System.Runtime.InteropServices;

namespace BackWave.Benchmarks.Environment;

/// <summary>
/// Thrown when <see cref="RunMode.Official"/> is requested on a host that cannot produce a publishable
/// number — any non-x86-64 architecture (including Apple Silicon Arm64) or a Rosetta-translated x64
/// process. Official numbers must come from a native-x86-64 instance (ADR 0027 §8); a Rosetta-emulated
/// run would measure the emulator, so the harness refuses loudly rather than silently emitting an
/// official-looking-but-unpublishable result.
/// </summary>
public sealed class OfficialModeNotSupportedException : InvalidOperationException
{
    /// <summary>Creates the exception with a caller-supplied explanation of why the host is ineligible.</summary>
    public OfficialModeNotSupportedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The credibility gate at the front of an official run (ADR 0027 §8). <see cref="EnvironmentManifest"/>
/// already <em>derives</em> <c>publishable: false</c> on a non-native host, but that alone would let an
/// official run finish quietly with an unpublishable number, wasting the run and inviting a "why isn't it
/// publishable?" surprise. This guard asserts the native-x86-64 precondition <em>up front</em> so official
/// mode either runs toward a genuinely publishable number or refuses immediately with an actionable message.
/// Local mode is never gated.
/// </summary>
public static class OfficialModeGuard
{
    /// <summary>
    /// Asserts the live process is eligible for the requested <paramref name="mode"/>. A no-op for
    /// <see cref="RunMode.Local"/>; for <see cref="RunMode.Official"/> it captures the live architecture and
    /// Rosetta state and throws unless they are native x86-64.
    /// </summary>
    /// <param name="mode">The requested run mode.</param>
    /// <exception cref="OfficialModeNotSupportedException">
    /// Official mode requested on a non-x86-64 or Rosetta-emulated host.
    /// </exception>
    public static void Assert(RunMode mode)
        => Assert(mode, RuntimeInformation.ProcessArchitecture, DetectRosetta());

    /// <summary>
    /// The pure form of the gate, taking the architecture and Rosetta state explicitly so the matrix is
    /// unit-testable. A no-op for <see cref="RunMode.Local"/>; throws for an official run that is not native
    /// x86-64 — exactly the cases where <see cref="EnvironmentManifest.DerivePublishable"/> would return false.
    /// </summary>
    /// <param name="mode">The requested run mode.</param>
    /// <param name="processArchitecture">The live process architecture.</param>
    /// <param name="isRosettaEmulated">Whether the x64 process is Rosetta-translated on Apple Silicon.</param>
    /// <exception cref="OfficialModeNotSupportedException">
    /// Official mode requested on a non-x86-64 or Rosetta-emulated host.
    /// </exception>
    public static void Assert(RunMode mode, Architecture processArchitecture, bool isRosettaEmulated)
    {
        if (mode != RunMode.Official)
        {
            return;
        }

        if (EnvironmentManifest.DerivePublishable(mode, processArchitecture, isRosettaEmulated))
        {
            return;
        }

        var reason = isRosettaEmulated
            ? $"this x64 process is Rosetta-translated on Apple Silicon (arch reports {processArchitecture})"
            : $"the process architecture is {processArchitecture}, not native x86-64";

        throw new OfficialModeNotSupportedException(
            $"Official mode requires a native x86-64 host, but {reason}. " +
            "Official, publishable numbers must come from the pinned native-x86-64 instance; " +
            "use --mode local on this machine for indicative-only runs.");
    }

    // Rosetta translates an x64 process on Apple Silicon: the macOS `sysctl.proc_translated` flag reads 1
    // for such a process and 0 (or absent) for a native one. On any non-macOS host this is always false.
    // Mirrors EnvironmentManifest's own detection so the guard and the derived flag agree on the same host.
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

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname(string name, out int oldp, ref nint oldlenp, IntPtr newp, nint newlen);
}
