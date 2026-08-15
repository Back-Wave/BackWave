using System.Security.Cryptography;

namespace BackWave.Pro.Licensing;

/// <summary>
/// The result of evaluating a BackWave Pro license at startup: its <see cref="State"/> and, when the
/// license verified, the facts it attests to. Evaluation is fully offline — a signature check against a
/// public key embedded in BackWave Pro, with no network call. The result is informational only: Pro
/// features behave identically regardless of state. BackWave Pro is free to use for organizations under
/// $1M in annual revenue; above that a license is required, on the honor system.
/// </summary>
public sealed class ProLicense
{
    private ProLicense(LicenseState state, LicenseClaims? claims)
    {
        State = state;
        Licensee = claims?.Licensee;
        Issued = claims?.Issued;
        Term = claims?.Term;
        RevenueBand = claims?.Band;
    }

    /// <summary>The outcome of evaluating the supplied license string.</summary>
    public LicenseState State { get; }

    /// <summary>
    /// The organization the license was issued to, or <see langword="null"/> when no license verified
    /// (state <see cref="LicenseState.Missing"/> or <see cref="LicenseState.Malformed"/>).
    /// </summary>
    public string? Licensee { get; }

    /// <summary>
    /// The day the license was issued, or <see langword="null"/> when no license verified.
    /// </summary>
    public DateOnly? Issued { get; }

    /// <summary>
    /// The last day of the license's subscription term, or <see langword="null"/> when no license
    /// verified. The license reads <see cref="LicenseState.Valid"/> through this day and
    /// <see cref="LicenseState.OutOfTerm"/> once the current date passes it.
    /// </summary>
    public DateOnly? Term { get; }

    /// <summary>
    /// The self-reported revenue band recorded on the license, or <see langword="null"/> when no
    /// license verified. The band sets price only; it never changes which features run.
    /// </summary>
    public string? RevenueBand { get; }

    /// <summary>
    /// Evaluates a license string against the public key embedded in BackWave Pro and the current
    /// date, with no network access. A <see langword="null"/> or blank string yields
    /// <see cref="LicenseState.Missing"/>; an untrusted or unparseable string yields
    /// <see cref="LicenseState.Malformed"/>; a genuine license whose subscription term has ended (as of
    /// today) yields <see cref="LicenseState.OutOfTerm"/>; otherwise <see cref="LicenseState.Valid"/>.
    /// </summary>
    /// <param name="license">The license string to evaluate, or <see langword="null"/> for free use.</param>
    /// <returns>The evaluated license: its state and, when it verified, its attested facts.</returns>
    public static ProLicense Evaluate(string? license)
    {
        using var verificationKey = EmbeddedVerificationKey.Create();
        return Evaluate(license, verificationKey, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    // Testable core: the verification key and as-of date are injected so the suite can exercise
    // OutOfTerm against a fixed date and verify with the committed test key.
    internal static ProLicense Evaluate(string? license, ECDsa verificationKey, DateOnly asOf)
    {
        if (string.IsNullOrWhiteSpace(license))
        {
            return new ProLicense(LicenseState.Missing, null);
        }

        if (!LicenseCrypto.TryParse(license, verificationKey, out var claims) || claims is null)
        {
            return new ProLicense(LicenseState.Malformed, null);
        }

        return asOf > claims.Term
            ? new ProLicense(LicenseState.OutOfTerm, claims)
            : new ProLicense(LicenseState.Valid, claims);
    }
}
