using System.Runtime.InteropServices;
using BackWave.Benchmarks.Environment;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// The credibility guard (ADR 0027 §8): a number is publishable only in official mode on native x86-64.
/// These pin the full official-vs-local × native-vs-Rosetta matrix so an indicative laptop figure can
/// never be marked official.
/// </summary>
public sealed class EnvironmentManifestTests
{
    [Fact]
    public void Local_mode_is_never_publishable_even_on_native_x64()
    {
        Assert.False(EnvironmentManifest.DerivePublishable(RunMode.Local, Architecture.X64, isRosettaEmulated: false));
    }

    [Fact]
    public void Official_mode_on_native_x64_is_publishable()
    {
        Assert.True(EnvironmentManifest.DerivePublishable(RunMode.Official, Architecture.X64, isRosettaEmulated: false));
    }

    [Fact]
    public void Official_mode_on_apple_silicon_arm64_is_not_publishable()
    {
        Assert.False(EnvironmentManifest.DerivePublishable(RunMode.Official, Architecture.Arm64, isRosettaEmulated: false));
    }

    [Fact]
    public void Official_mode_under_rosetta_is_not_publishable_despite_reporting_x64()
    {
        // Rosetta translates an x64 process on Apple Silicon — ProcessArchitecture reads X64 but the
        // silicon is not native, so it must stay unpublishable.
        Assert.False(EnvironmentManifest.DerivePublishable(RunMode.Official, Architecture.X64, isRosettaEmulated: true));
    }

    [Fact]
    public void Local_mode_under_rosetta_is_not_publishable()
    {
        Assert.False(EnvironmentManifest.DerivePublishable(RunMode.Local, Architecture.X64, isRosettaEmulated: true));
    }

    [Fact]
    public void Capture_stamps_the_live_environment_and_derives_the_flag()
    {
        var manifest = EnvironmentManifest.Capture(RunMode.Local, "PostgreSQL", "17.0");

        Assert.Equal(RunMode.Local, manifest.Mode);
        Assert.Equal("PostgreSQL", manifest.DbEngine);
        Assert.Equal("17.0", manifest.DbVersion);
        Assert.False(manifest.Publishable); // local mode is never publishable
        Assert.Equal(
            EnvironmentManifest.DerivePublishable(RunMode.Local, manifest.ProcessArchitecture, manifest.IsRosettaEmulated),
            manifest.Publishable);
    }

    // ── Official-mode guard: same matrix, but it must REFUSE rather than just derive false ─────────────

    [Fact]
    public void Guard_allows_local_mode_on_any_architecture()
    {
        // Local mode is indicative-only and never gated, even on Apple Silicon or under Rosetta.
        OfficialModeGuard.Assert(RunMode.Local, Architecture.Arm64, isRosettaEmulated: false);
        OfficialModeGuard.Assert(RunMode.Local, Architecture.X64, isRosettaEmulated: true);
    }

    [Fact]
    public void Guard_allows_official_mode_on_native_x64()
    {
        // The one publishable cell: no throw.
        OfficialModeGuard.Assert(RunMode.Official, Architecture.X64, isRosettaEmulated: false);
    }

    [Fact]
    public void Guard_refuses_official_mode_on_apple_silicon_arm64()
    {
        var exception = Assert.Throws<OfficialModeNotSupportedException>(
            () => OfficialModeGuard.Assert(RunMode.Official, Architecture.Arm64, isRosettaEmulated: false));
        Assert.Contains("native x86-64", exception.Message);
    }

    [Fact]
    public void Guard_refuses_official_mode_under_rosetta_despite_reporting_x64()
    {
        var exception = Assert.Throws<OfficialModeNotSupportedException>(
            () => OfficialModeGuard.Assert(RunMode.Official, Architecture.X64, isRosettaEmulated: true));
        Assert.Contains("Rosetta", exception.Message);
    }

    [Fact]
    public void Guard_decision_matches_the_publishable_flag_for_every_official_cell()
    {
        // The guard throws on exactly the official cells the manifest would mark unpublishable, so an
        // official run that survives the gate is guaranteed to carry publishable: true.
        foreach (var arch in new[] { Architecture.X64, Architecture.Arm64 })
        {
            foreach (var rosetta in new[] { false, true })
            {
                var publishable = EnvironmentManifest.DerivePublishable(RunMode.Official, arch, rosetta);
                var threw = false;
                try
                {
                    OfficialModeGuard.Assert(RunMode.Official, arch, rosetta);
                }
                catch (OfficialModeNotSupportedException)
                {
                    threw = true;
                }

                Assert.Equal(publishable, !threw);
            }
        }
    }
}
