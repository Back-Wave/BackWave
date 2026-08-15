using System.Security.Cryptography;

namespace BackWave.Pro.Licensing;

// The public key license signatures are verified against, embedded so verification is fully offline —
// no network call, ever. Only the public half ships; the matching private signing key is a release
// secret held outside the repo.
//
// The PEM below is the PRODUCTION verification key. Its matching private signing key is a release
// secret held outside the repo (1Password) and is used only to issue licenses. The test suites do NOT
// use this key: they generate an ephemeral in-memory keypair per run and sign/verify against that, so
// a test-signed license does NOT validate against a shipped build (by design) and no signing key is
// ever committed.
//
// Enforcement is honor-system soft-fail (a forged key only suppresses the unlicensed warning + banner).
// The GuardAgainstDevLicenseKey target in BackWave.Pro.csproj fails `dotnet pack` if this key is ever
// reverted to the historical development key (whose private half was once committed) — so a build can
// never ship a key whose private half is public.
// To rotate: regenerate a P-256 keypair with the license tool, keep the private half secret, and
// replace the PEM below with the new public half.
internal static class EmbeddedVerificationKey
{
    private const string Spki =
        """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsCRoZBE66IJOrpMuSRvbuJLY2xsD
        xXuqwXOuBDfuJZ+osy5Z7ThDENAYVxCjOYoZ0LBDDwQ1wMJm7epak1GCyg==
        -----END PUBLIC KEY-----
        """;

    // A fresh ECDsa over the embedded key. Callers own the returned instance and dispose it.
    public static ECDsa Create()
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(Spki);
        return ecdsa;
    }
}
