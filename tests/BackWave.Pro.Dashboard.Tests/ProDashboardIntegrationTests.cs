using BackWave.Dashboard;
using BackWave.Monitor;
using BackWave.Operations;
using BackWave.Pro;
using BackWave.Pro.Dashboard;
using BackWave.Pro.Licensing;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Pro.Dashboard.Tests;

/// <summary>
/// End-to-end through the mounted dashboard (issue 0154): registering the Pro dashboard surfaces
/// lights up the unlicensed banner via the free dashboard's extension points, with no change to the
/// free dashboard. The banner shows under a non-valid license and is gone under a valid one.
/// </summary>
public sealed class ProDashboardIntegrationTests
{
    // A Valid Pro license evaluated against the ephemeral test keypair. The shipped AddBackWavePro path
    // verifies against the production embedded key, whose private half lives outside the repo, so a
    // Valid license can't be minted for that path in-repo — this evaluates the test-signed license with
    // the test public key and is injected directly (AddBackWavePro's TryAdd respects a pre-registered one).
    private static ProLicense ValidDevLicense()
    {
        using var signingKey = TestKeys.SigningKey();
        // Term far in the future so the license is in-term regardless of the current date.
        var license = LicenseCrypto.Issue(
            new LicenseClaims { Licensee = "Acme Inc", Issued = new DateOnly(2026, 1, 1), Term = new DateOnly(9999, 1, 1), Band = "growth" },
            signingKey);
        using var verificationKey = TestKeys.VerificationKey();
        return ProLicense.Evaluate(license, verificationKey, new DateOnly(2026, 6, 18));
    }

    private static async Task<(WebApplication App, HttpClient Http)> StartAsync(string? license)
    {
        var store = new InMemoryJobStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new BackWaveMonitor(store, registry: null));
        builder.Services.AddSingleton(new BackWaveOperator(store));
        builder.Services.AddAntiforgery();
        // The Pro spine evaluates the license into DI; the Pro dashboard registers the banner through
        // the free dashboard's extension seam — note the free dashboard is mounted unchanged below.
        builder.Services.AddBackWavePro(license);
        builder.Services.AddBackWaveProDashboard();

        var app = builder.Build();
        app.UseBackWaveDashboard("/backwave");
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task ProPresent_WithoutLicense_ShowsTheUnlicensedBanner()
    {
        var (app, http) = await StartAsync(license: null);
        await using (app)
        {
            var html = await http.GetStringAsync("/backwave/");
            Assert.Contains("BackWave Pro is running without a license key", html);
        }
    }

    [Fact]
    public async Task ProPresent_WithValidLicense_ShowsNoBanner()
    {
        var store = new InMemoryJobStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new BackWaveMonitor(store, registry: null));
        builder.Services.AddSingleton(new BackWaveOperator(store));
        builder.Services.AddAntiforgery();
        // Inject a Valid license before AddBackWavePro; its TryAddSingleton respects the existing one.
        builder.Services.AddSingleton(ValidDevLicense());
        builder.Services.AddBackWavePro();
        builder.Services.AddBackWaveProDashboard();

        var app = builder.Build();
        app.UseBackWaveDashboard("/backwave");
        await app.StartAsync();
        await using (app)
        {
            var html = await app.GetTestClient().GetStringAsync("/backwave/");
            // Assert on the marker every non-valid banner shares, not just the Missing-state text: a
            // Malformed license renders a different heading and would slip past a Missing-only check.
            Assert.DoesNotContain("This banner is only a reminder", html);
        }
    }

    [Fact]
    public async Task TheRegistrationIsTheOnlyChange_FreeDashboardNeedsNoEdit()
    {
        // Proof of the seam: with the Pro dashboard NOT registered, the same free dashboard shows no
        // banner even with Pro present and unlicensed — the banner exists only because the extension
        // was registered, never because the free dashboard knows about Pro.
        var store = new InMemoryJobStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new BackWaveMonitor(store, registry: null));
        builder.Services.AddSingleton(new BackWaveOperator(store));
        builder.Services.AddAntiforgery();
        builder.Services.AddBackWavePro(license: null); // Pro present + unlicensed, but no Pro dashboard.

        var app = builder.Build();
        app.UseBackWaveDashboard("/backwave");
        await app.StartAsync();
        await using (app)
        {
            var html = await app.GetTestClient().GetStringAsync("/backwave/");
            Assert.DoesNotContain("BackWave Pro is running without a license key", html);
        }
    }
}
