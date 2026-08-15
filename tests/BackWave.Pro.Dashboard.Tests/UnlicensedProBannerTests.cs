using BackWave.Pro.Dashboard;
using BackWave.Pro.Licensing;

namespace BackWave.Pro.Dashboard.Tests;

/// <summary>
/// The unlicensed-Pro banner (issue 0154): it shows whenever the Pro license is not valid and
/// disappears once it is valid. Banner production is presentation-only, so these unit tests drive
/// the extension's <see cref="UnlicensedProBanner.Banner"/> directly across the license states.
/// </summary>
public sealed class UnlicensedProBannerTests
{
    // The "today" the license is evaluated against; wall-clock expiry compares Term to this date.
    private static readonly DateOnly AsOf = new(2026, 6, 18);

    private static ProLicense EvaluateSigned(DateOnly term)
    {
        using var signingKey = TestKeys.SigningKey();
        var license = LicenseCrypto.Issue(
            new LicenseClaims { Licensee = "Acme Inc", Issued = new DateOnly(2026, 1, 1), Term = term, Band = "growth" },
            signingKey);
        using var verificationKey = TestKeys.VerificationKey();
        return ProLicense.Evaluate(license, verificationKey, AsOf);
    }

    [Fact]
    public void ValidLicense_ShowsNoBanner()
    {
        var license = EvaluateSigned(term: new DateOnly(2027, 6, 18));
        Assert.Equal(LicenseState.Valid, license.State);

        var banner = new UnlicensedProBanner(license).Banner("/backwave");

        Assert.Null(banner);
    }

    [Fact]
    public void MissingLicense_ShowsTheBanner()
    {
        var license = ProLicense.Evaluate(null);
        Assert.Equal(LicenseState.Missing, license.State);

        var banner = new UnlicensedProBanner(license).Banner("/backwave");

        Assert.NotNull(banner);
        Assert.Contains("BackWave Pro is running without a license key", banner);
        // A keyless deployment may be fully licensed (free under $1M) — the banner must say so.
        Assert.Contains("fully licensed", banner);
        // Presentation-only: it states it does not restrict anything.
        Assert.Contains("keep running in full", banner);
    }

    [Fact]
    public void OutOfTermLicense_ShowsTheBanner()
    {
        // Genuine, well-formed license whose subscription term has passed as of today → OutOfTerm.
        var license = EvaluateSigned(term: new DateOnly(2026, 1, 1));
        Assert.Equal(LicenseState.OutOfTerm, license.State);

        var banner = new UnlicensedProBanner(license).Banner("/backwave");

        Assert.NotNull(banner);
        Assert.Contains("subscription has ended", banner);
    }

    [Fact]
    public void MalformedLicense_ShowsTheBanner()
    {
        var license = ProLicense.Evaluate("not-a-real-license");
        Assert.Equal(LicenseState.Malformed, license.State);

        var banner = new UnlicensedProBanner(license).Banner("/backwave");

        Assert.NotNull(banner);
        Assert.Contains("could not be verified", banner);
    }
}
