namespace BackWave.Storage;

/// <summary>
/// The named size and batch limits a store enforces. With one exception (failure detail), these are
/// enforced with clear errors rather than silent truncation.
/// </summary>
public sealed record StoreBounds
{
    /// <summary>The largest serialized job payload accepted, in bytes. An over-limit enqueue is rejected.</summary>
    public int MaxPayloadBytes { get; init; } = 65_536;

    /// <summary>The longest accepted payload-type wire name, in characters. An over-limit enqueue is rejected.</summary>
    public int MaxWireNameLength { get; init; } = 128;

    /// <summary>The most jobs a single claim call may return. A larger request is clamped down to this.</summary>
    public int MaxClaimBatch { get; init; } = 32;

    /// <summary>The most parents a single job may declare. An enqueue exceeding it is rejected.</summary>
    public int MaxParentsPerJob { get; init; } = 16;

    /// <summary>The largest page size a monitor listing may request. A larger request is clamped down to this.</summary>
    public int MaxMonitorPageSize { get; init; } = 200;

    /// <summary>The most jobs a single purge call removes. A larger request is clamped down to this.</summary>
    public int MaxPurgeBatch { get; init; } = 500;

    /// <summary>
    /// How many recently skipped ticks a recurring schedule retains (from no-overlap or catch-up),
    /// newest last. Older skips age out so the column cannot grow without limit.
    /// </summary>
    public int MaxRecordedSkippedTicks { get; init; } = 32;

    /// <summary>
    /// How many transition-log entries a single job retains over its life, newest kept. When a state
    /// change would exceed this cap, the oldest transition is dropped — keeping the history bounded
    /// for a job that churns through many retries.
    /// </summary>
    public int MaxTransitionsPerJob { get; init; } = 64;

    /// <summary>
    /// The cap on a stored failure-detail string (the exception type, message, and stack captured on a
    /// failing attempt), in bytes. Unlike the other bounds, this one truncates rather than rejects:
    /// failure detail is write-only diagnostics, never a semantic input, so a clipped stack is
    /// preferable to a refused outcome.
    /// </summary>
    public int MaxFailureDetailBytes { get; init; } = 8_192;

    /// <summary>
    /// The cap on a stored job-output blob, in bytes. Unlike <see cref="MaxFailureDetailBytes"/>, this
    /// one rejects rather than truncates: output is functional data a descendant deserializes, and a
    /// clipped serialized blob cannot be deserialized, so over-limit output fails the outcome write
    /// loudly instead of silently corrupting the reader. Defaults to the payload cap, the natural size
    /// class for one job's result.
    /// </summary>
    public int MaxOutputBytes { get; init; } = 65_536;

    /// <summary>
    /// Clamps a failure-detail string to <see cref="MaxFailureDetailBytes"/> UTF-8 bytes, truncating
    /// (never rejecting) so every store bounds it identically. Null and already-short detail pass
    /// through untouched. Truncation is byte-exact and never splits a UTF-8 code point, so the result
    /// always round-trips as valid text.
    /// </summary>
    /// <param name="detail">The failure detail to clamp, or null.</param>
    /// <returns>The original string when null or within the cap; otherwise a truncated copy that ends on a complete code point.</returns>
    public string? ClampFailureDetail(string? detail)
    {
        if (detail is null)
        {
            return null;
        }
        var bytes = System.Text.Encoding.UTF8.GetByteCount(detail);
        if (bytes <= MaxFailureDetailBytes)
        {
            return detail;
        }
        var buffer = System.Text.Encoding.UTF8.GetBytes(detail);
        var length = MaxFailureDetailBytes;
        // Back up off a continuation byte (10xxxxxx) so we never cut mid-code-point.
        while (length > 0 && (buffer[length] & 0b1100_0000) == 0b1000_0000)
        {
            length--;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    /// <summary>The default bounds, used when a store is configured without explicit ones.</summary>
    public static StoreBounds Default { get; } = new();
}
