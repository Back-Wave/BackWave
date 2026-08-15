using System.Text.Json.Serialization;

namespace BackWave.Pro.Licensing;

// The signed payload of a license string: the facts an issuer attests to. Serialized to compact
// JSON, signed, and carried as the first dot-segment of the license string (see ProLicense). Internal
// — consumers never construct or see this type; they see the parsed fields on ProLicense.
internal sealed record LicenseClaims
{
    // Who the license is issued to (the company). Surfaced for display only.
    public required string Licensee { get; init; }

    // The day the license was issued.
    public required DateOnly Issued { get; init; }

    // The last day of the subscription term. Once today is past this date the license reads OutOfTerm;
    // renewing extends it. Enforcement is soft either way — OutOfTerm only surfaces the notice.
    public required DateOnly Term { get; init; }

    // The self-reported revenue band that set the price. Changes price only, never which features run.
    public required string Band { get; init; }
}

// Source-generated (de)serialization so the licensing path carries no reflection-based JSON — keeps
// the assembly trim/AOT-clean. DateOnly serializes as ISO "yyyy-MM-dd".
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LicenseClaims))]
internal sealed partial class LicenseClaimsJsonContext : JsonSerializerContext;
