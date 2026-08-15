namespace BackWave.Pro.Licensing;

/// <summary>
/// The outcome of evaluating a BackWave Pro license at startup. Pro never changes behavior based on
/// this value — every feature works identically in every state. The state only drives a startup log
/// warning and the dashboard's unlicensed banner, so an operator knows whether the deployment is
/// licensed. BackWave Pro is free to use for organizations under $1M in annual revenue; above that a
/// license is required, but enforcement is honor-system: an unlicensed process still runs in full.
/// </summary>
public enum LicenseState
{
    /// <summary>
    /// The license string was present, its signature verified, and its subscription term is still
    /// current (today falls on or before the term's last day). No warning and no banner are shown.
    /// </summary>
    Valid,

    /// <summary>
    /// No license string was supplied. The expected state for free use (under $1M revenue) and for
    /// any deployment that has not yet purchased a license.
    /// </summary>
    Missing,

    /// <summary>
    /// A license string was supplied but could not be trusted: it was not well-formed, or its
    /// signature did not verify against the embedded public key (tampered, truncated, or issued by an
    /// unknown signer).
    /// </summary>
    Malformed,

    /// <summary>
    /// The license is genuine and well-formed, but its subscription term has ended as of today — the
    /// current date is past the term's last day. Renewing the subscription clears this state. Like
    /// every other state, it changes nothing at runtime: Pro features keep working in full.
    /// </summary>
    OutOfTerm,
}
