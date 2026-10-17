using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BackWave.Diagnostics;

namespace BackWave.Storage;

// The one stored form of an OperatorAuditDetail, shared by every adapter that keeps the detail in a text
// column. A reschedule is stored as {"from":"<instant>","to":"<instant>"}, each instant in UTC with seven
// fraction digits and a Z, for example 2026-01-01T01:00:00.0000000Z. Written and read by hand with
// Utf8JsonWriter and JsonDocument, so it needs no reflection and stays trim- and AOT-safe.
//
// The action column says which detail a row holds, so the text carries no type tag. A null column is no
// detail: rows written before the column existed, and every action that keeps none. A detail that does
// not parse for its action is corrupt and halts the read rather than hand back invented times. Text on an
// action that keeps no detail here reads as none, so a node one version behind still reads a trail that a
// newer node added details to.
internal static class OperatorAuditDetailCodec
{
    private const string FromProperty = "from";
    private const string ToProperty = "to";
    // The round-trip "O" format of a UTC instant, spelled out so a read accepts only that form.
    private const string InstantFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";

    internal static string? Encode(OperatorAuditDetail? detail)
    {
        switch (detail)
        {
            case null:
                return null;
            case RescheduleAuditDetail reschedule:
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString(FromProperty, FormatInstant(reschedule.From));
                    writer.WriteString(ToProperty, FormatInstant(reschedule.To));
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.WrittenSpan);
            default:
                throw new ArgumentOutOfRangeException(nameof(detail), detail.GetType(), "Unknown operator audit detail.");
        }
    }

    internal static OperatorAuditDetail? Decode(OperatorAction action, string target, string? stored)
    {
        if (stored is null || action != OperatorAction.Reschedule)
        {
            return null;
        }
        if (TryDecodeReschedule(stored) is { } reschedule)
        {
            return reschedule;
        }
        throw Invariant.Halt(
            InvariantTrigger.MalformedAuditDetailStored,
            $"An audit record of {target} stores a {action} detail that does not parse: {stored}");
    }

    private static RescheduleAuditDetail? TryDecodeReschedule(string stored)
    {
        try
        {
            using var document = JsonDocument.Parse(stored);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && TryReadInstant(root, FromProperty, out var from)
                && TryReadInstant(root, ToProperty, out var to)
                ? new RescheduleAuditDetail(from, to)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadInstant(JsonElement root, string property, out DateTimeOffset instant)
    {
        instant = default;
        return root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParseExact(
                value.GetString(), InstantFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant);
    }

    private static string FormatInstant(DateTimeOffset instant)
        => instant.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);
}
