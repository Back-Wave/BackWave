using System.Security.Cryptography;

namespace BackWave.Pro.Dashboard.Tests;

// Generates an ephemeral P-256 (NIST secp256r1) license keypair once per test run, entirely in memory.
// The private half signs test licenses; the public half verifies them. This is a distinct keypair from
// BackWave.Pro's shipped EmbeddedVerificationKey (the production key), so a test-signed license never
// validates against a shipped build - by design. Nothing is written to disk, so no signing key is ever
// committed to the repo.
internal static class TestKeys
{
    // One keypair for the whole process. The parameters carry both halves; each accessor imports a fresh
    // ECDsa from them, so callers own and dispose independent instances of a matched signer/verifier pair.
    private static readonly ECParameters KeyPair = GenerateKeyPair();

    private static ECParameters GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ecdsa.ExportParameters(includePrivateParameters: true);
    }

    // The private signing key. Signs licenses that the matching VerificationKey() accepts.
    public static ECDsa SigningKey() => ECDsa.Create(KeyPair);

    // The public verification key only - the private half is stripped, mirroring the shipped verifier,
    // which only ever holds the public key.
    public static ECDsa VerificationKey()
        => ECDsa.Create(new ECParameters { Curve = KeyPair.Curve, Q = KeyPair.Q });
}
