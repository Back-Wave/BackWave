using BackWave.Pro.Licensing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackWave.Pro;

// Emits the single startup warning when Pro is not validly licensed, then does nothing else. This is
// the entire runtime consequence of being unlicensed: a log line (and, with the Pro dashboard, a
// banner). Features never change, nothing throws, no job is affected.
internal sealed class ProLicenseWarningService(
    ProLicense license, ILogger<ProLicenseWarningService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Missing is worded differently from the failure states: a keyless deployment at an
        // organization under $1M annual gross revenue is fully licensed (the free grant needs no
        // key), so the log states the fact and lets the reader place themselves.
        switch (license.State)
        {
            case LicenseState.Missing:
                logger.LogWarning(
                    "BackWave Pro is running without a license key. Organizations under $1M annual gross " +
                    "revenue are fully licensed for free use and need no key. At or above $1M, an active " +
                    "subscription is required (backwave.app/pricing). Pro features run unchanged either way.");
                break;
            case LicenseState.Malformed:
                logger.LogWarning(
                    "The supplied BackWave Pro license key could not be verified. It may be tampered " +
                    "with or incomplete. Pro features run unchanged.");
                break;
            case LicenseState.OutOfTerm:
                logger.LogWarning(
                    "This BackWave Pro subscription has ended; renew to clear this notice. " +
                    "Pro features run unchanged.");
                break;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
