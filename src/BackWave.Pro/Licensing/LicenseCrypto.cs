using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace BackWave.Pro.Licensing;

// The on-the-wire format of a license string and the only place signing/verification live, so the
// issuing tool and the runtime verifier can never drift. A license string is two URL-safe Base64
// segments joined by a dot: "<payload>.<signature>". The payload is the compact-JSON LicenseClaims;
// the signature is ECDSA over the curve P-256 with SHA-256, in fixed-size IEEE P-1363 (r||s) form.
internal static class LicenseCrypto
{
    private const DSASignatureFormat SignatureFormat = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    // Sign claims into a license string. Used by the offline issuing tool and the test suite, never at
    // runtime — the product DLL only ever verifies.
    public static string Issue(LicenseClaims claims, ECDsa signingKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims, LicenseClaimsJsonContext.Default.LicenseClaims);
        var signature = signingKey.SignData(payload, HashAlgorithmName.SHA256, SignatureFormat);
        return $"{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(signature)}";
    }

    // Parse and cryptographically verify a license string against the verification key. Returns false
    // — never throws — for any malformed or untrusted input: wrong segment count, non-Base64 segments,
    // a signature that does not verify, or a payload that is not valid claims JSON.
    public static bool TryParse(string license, ECDsa verificationKey, out LicenseClaims? claims)
    {
        claims = null;

        var dot = license.IndexOf('.');
        if (dot <= 0 || dot != license.LastIndexOf('.') || dot == license.Length - 1)
        {
            return false;
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Base64Url.DecodeFromChars(license.AsSpan(0, dot));
            signature = Base64Url.DecodeFromChars(license.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return false;
        }

        if (!verificationKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, SignatureFormat))
        {
            return false;
        }

        try
        {
            claims = JsonSerializer.Deserialize(payload, LicenseClaimsJsonContext.Default.LicenseClaims);
        }
        catch (JsonException)
        {
            return false;
        }

        return claims is not null;
    }
}
