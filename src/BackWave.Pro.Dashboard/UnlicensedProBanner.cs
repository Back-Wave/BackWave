using System.Net;
using BackWave.Dashboard;
using BackWave.Pro.Licensing;

namespace BackWave.Pro.Dashboard;

/// <summary>
/// A dashboard extension that surfaces an unlicensed-Pro banner across the BackWave dashboard. The
/// banner appears whenever BackWave Pro is installed and its license is not valid — missing,
/// malformed, or out of term — and disappears once a valid license is in place. It is a
/// presentation-only reminder: BackWave Pro features run identically regardless of license state, so
/// the banner never changes what the dashboard or the product can do. Register it with
/// <see cref="BackWaveProDashboardServiceCollectionExtensions.AddBackWaveProDashboard"/>.
/// </summary>
/// <remarks>
/// BackWave Pro is free to use for organizations under $1M in annual revenue; above that a license is
/// required, on the honor system. The banner exists so an operator can see at a glance whether a
/// deployment is licensed — nothing more.
/// </remarks>
public sealed class UnlicensedProBanner : IDashboardExtension
{
    private readonly ProLicense _license;

    /// <summary>
    /// Creates the banner extension over the evaluated BackWave Pro license. The license is normally
    /// supplied by the service container, which holds the single instance evaluated at startup.
    /// </summary>
    /// <param name="license">The evaluated BackWave Pro license whose state drives the banner.</param>
    /// <exception cref="ArgumentNullException"><paramref name="license"/> is <see langword="null"/>.</exception>
    public UnlicensedProBanner(ProLicense license)
    {
        ArgumentNullException.ThrowIfNull(license);
        _license = license;
    }

    /// <summary>
    /// Produces the unlicensed-Pro banner markup, or nothing when the license is valid. Returns
    /// <see langword="null"/> for a valid license, so a licensed deployment renders no banner and the
    /// dashboard is identical to its un-extended self; returns the banner HTML for any non-valid
    /// state.
    /// </summary>
    /// <param name="basePath">
    /// The path the dashboard is mounted at; accepted so the banner participates in the extension
    /// contract, though the current banner links nowhere and does not use it.
    /// </param>
    /// <returns>The banner HTML when the license is not valid; otherwise <see langword="null"/>.</returns>
    public string? Banner(string basePath)
    {
        if (_license.State == LicenseState.Valid)
        {
            return null;
        }

        var (heading, detail) = DescribeState(_license.State);
        var headingHtml = WebUtility.HtmlEncode(heading);
        var detailHtml = WebUtility.HtmlEncode(detail);
        // Self-contained, inline-styled markup: the dashboard inlines its whole design system and
        // ships no static assets, so an extension does the same. Amber tokens read as a warning in
        // both light and dark themes (they are theme-aware design-system variables).
        return $"""
            <div role="status" style="margin-bottom:16px;padding:12px 16px;border:1px solid var(--pen-amber);border-radius:8px;background:var(--pen-amber-tint);color:var(--text-body);font:var(--type-body);">
                <strong>{headingHtml}</strong> {detailHtml}
                Pro features keep running in full. This banner is only a reminder.
            </div>
            """;
    }

    // Missing gets a distinct heading: a keyless deployment at an organization under $1M annual
    // gross revenue is fully licensed (the free grant needs no key), so the banner must not flatly
    // declare it "unlicensed". It states the fact and lets the reader place themselves.
    private static (string Heading, string Detail) DescribeState(LicenseState state) => state switch
    {
        LicenseState.Missing => (
            "BackWave Pro is running without a license key.",
            "Under $1M in annual gross revenue, this use is fully licensed and needs no key. At or above $1M, an active subscription is required (backwave.app/pricing)."),
        LicenseState.Malformed => (
            "BackWave Pro license key problem.",
            "The supplied license could not be verified. It may be tampered with or incomplete."),
        LicenseState.OutOfTerm => (
            "BackWave Pro subscription ended.",
            "This BackWave Pro subscription has ended; renew to clear this notice."),
        // Valid is handled before this method is reached; included for exhaustiveness.
        _ => ("", ""),
    };
}
