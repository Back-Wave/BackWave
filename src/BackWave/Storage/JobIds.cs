namespace BackWave.Storage;

/// <summary>Deterministic job identifiers every store must derive identically.</summary>
public static class JobIds
{
    /// <summary>
    /// Derives the job id for the instance a schedule mints at a given tick. The result is a pure
    /// function of <paramref name="scheduleId"/> and <paramref name="tick"/>: the same pair always
    /// yields the same id. This is what makes minting exactly-once across nodes — two minters racing
    /// to mint the same tick produce the identical id and collide on the primary key, so only one
    /// instance is ever inserted instead of duplicates.
    /// </summary>
    /// <param name="scheduleId">The recurring schedule the instance belongs to.</param>
    /// <param name="tick">The due instant of the occurrence being minted.</param>
    /// <returns>The deterministic job id for that schedule and tick.</returns>
    public static Guid ForMintedTick(string scheduleId, DateTimeOffset tick)
    {
        var hash = 14695981039346656037UL; // FNV-1a 64
        foreach (var c in scheduleId)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, hash);
        BitConverter.TryWriteBytes(bytes[8..], tick.UtcTicks);
        return new Guid(bytes);
    }
}
