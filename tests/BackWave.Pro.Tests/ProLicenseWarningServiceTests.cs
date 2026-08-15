using BackWave.Pro.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackWave.Pro.Tests;

public sealed class ProLicenseWarningServiceTests
{
    private static string ValidLicense()
    {
        using var key = TestKeys.SigningKey();
        return LicenseCrypto.Issue(
            new LicenseClaims { Licensee = "Acme", Issued = new DateOnly(2026, 1, 1), Term = new DateOnly(9999, 1, 1), Band = "growth" },
            key);
    }

    [Fact]
    public async Task An_unlicensed_state_logs_exactly_one_startup_warning()
    {
        var logger = new CapturingLogger<ProLicenseWarningService>();
        var service = new ProLicenseWarningService(ProLicense.Evaluate(license: null), logger);

        await service.StartAsync(CancellationToken.None);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("without a license key", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_valid_license_logs_nothing_and_does_not_throw()
    {
        // Evaluate against the dev public key: the shipped embedded key is the production key, whose
        // private half is outside the repo, so a Valid license can only be produced with the dev keypair.
        using var verificationKey = TestKeys.VerificationKey();
        var license = ProLicense.Evaluate(ValidLicense(), verificationKey, new DateOnly(2026, 6, 18));
        var logger = new CapturingLogger<ProLicenseWarningService>();
        var service = new ProLicenseWarningService(license, logger);

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void AddBackWavePro_registers_the_license_and_a_single_warning_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBackWavePro(license: null);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(LicenseState.Missing, provider.GetRequiredService<ProLicense>().State);
        Assert.Single(provider.GetServices<IHostedService>().OfType<ProLicenseWarningService>());
    }
}
