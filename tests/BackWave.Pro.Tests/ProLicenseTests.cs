using System.Security.Cryptography;
using BackWave.Pro.Licensing;

namespace BackWave.Pro.Tests;

public sealed class ProLicenseTests
{
    // A fixed "today" the tests evaluate against; wall-clock expiry compares Term to this date.
    private static readonly DateOnly Today = new(2026, 6, 18);

    private static string Sign(DateOnly term, DateOnly? issued = null, string licensee = "Acme Inc", string band = "growth")
    {
        using var key = TestKeys.SigningKey();
        return LicenseCrypto.Issue(
            new LicenseClaims { Licensee = licensee, Issued = issued ?? new DateOnly(2026, 1, 1), Term = term, Band = band },
            key);
    }

    private static ProLicense EvalAsOf(string? license, DateOnly asOf)
    {
        // Verify against the dev public key (the twin of the dev signing key), not the embedded key:
        // the embedded key is the production key whose private half lives outside the repo, so a
        // test-signed license can only be validated against the dev keypair here.
        using var verificationKey = TestKeys.VerificationKey();
        return ProLicense.Evaluate(license, verificationKey, asOf);
    }

    [Fact]
    public void Correctly_signed_in_term_key_resolves_Valid_with_claims()
    {
        var license = Sign(term: new DateOnly(2027, 6, 18), licensee: "Globex", band: "enterprise");

        var result = EvalAsOf(license, Today);

        Assert.Equal(LicenseState.Valid, result.State);
        Assert.Equal("Globex", result.Licensee);
        Assert.Equal(new DateOnly(2027, 6, 18), result.Term);
        Assert.Equal("enterprise", result.RevenueBand);
    }

    [Fact]
    public void The_shipped_embedded_key_rejects_a_dev_signed_license()
    {
        // The parameterless Evaluate verifies against the embedded (production) key. The dev signing key
        // is a different keypair, so a test-minted license never validates against a shipped build. The
        // production private half lives outside the repo, so a genuinely Valid embedded-path license
        // can't be minted here — this asserts the separation instead. Term far in the future so the
        // rejection is the signature, not expiry.
        var license = Sign(term: new DateOnly(9999, 1, 1));

        var result = ProLicense.Evaluate(license);

        Assert.Equal(LicenseState.Malformed, result.State);
    }

    [Fact]
    public void Absent_key_resolves_Missing()
    {
        Assert.Equal(LicenseState.Missing, EvalAsOf(null, Today).State);
        Assert.Equal(LicenseState.Missing, EvalAsOf("", Today).State);
        Assert.Equal(LicenseState.Missing, EvalAsOf("   ", Today).State);
    }

    [Fact]
    public void Garbage_key_resolves_Malformed()
    {
        Assert.Equal(LicenseState.Malformed, EvalAsOf("not-a-license", Today).State);
        Assert.Equal(LicenseState.Malformed, EvalAsOf("only-one-segment", Today).State);
        Assert.Equal(LicenseState.Malformed, EvalAsOf("a.b.c", Today).State);
        Assert.Equal(LicenseState.Malformed, EvalAsOf("!!!.@@@", Today).State);
    }

    [Fact]
    public void Tampered_key_resolves_Malformed()
    {
        var license = Sign(term: new DateOnly(2027, 6, 18));
        // Flip the final character of the signature segment — a one-bit change the signature catches.
        var tampered = license[..^1] + (license[^1] == 'A' ? 'B' : 'A');

        Assert.Equal(LicenseState.Malformed, EvalAsOf(tampered, Today).State);
    }

    [Fact]
    public void Key_signed_by_an_unknown_signer_resolves_Malformed()
    {
        using var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var license = LicenseCrypto.Issue(
            new LicenseClaims { Licensee = "Impostor", Issued = new DateOnly(2026, 1, 1), Term = new DateOnly(2027, 1, 1), Band = "growth" },
            foreignKey);

        Assert.Equal(LicenseState.Malformed, EvalAsOf(license, Today).State);
    }

    [Fact]
    public void Key_whose_term_has_passed_resolves_OutOfTerm_but_keeps_its_claims()
    {
        var license = Sign(term: new DateOnly(2025, 1, 1), licensee: "Initech", band: "growth");

        var result = EvalAsOf(license, Today);

        Assert.Equal(LicenseState.OutOfTerm, result.State);
        Assert.Equal("Initech", result.Licensee);
        Assert.Equal(new DateOnly(2025, 1, 1), result.Term);
    }

    [Fact]
    public void Evaluated_exactly_on_the_term_day_is_still_in_term()
    {
        var term = new DateOnly(2026, 6, 18);
        var license = Sign(term: term);

        Assert.Equal(LicenseState.Valid, EvalAsOf(license, term).State);
    }

    [Fact]
    public void The_same_license_reads_Valid_inside_its_term_and_OutOfTerm_after_it()
    {
        // The subscription (wall-clock) behavior the old build-date model deliberately prevented:
        // one and the same key flips to OutOfTerm purely because time passed.
        var term = new DateOnly(2026, 6, 18);
        var license = Sign(term: term);

        Assert.Equal(LicenseState.Valid, EvalAsOf(license, term.AddDays(-1)).State);
        Assert.Equal(LicenseState.OutOfTerm, EvalAsOf(license, term.AddDays(1)).State);
    }
}
