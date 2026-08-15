namespace BackWave.Observers;

/// <summary>
/// The result of an observer reaching for a job's payload. The payload is read lazily, so it may be
/// gone by the time the observer asks: under retention the job and its payload can be purged while
/// its transition is still being delivered. This is honest by design — the result reports
/// <see cref="Available"/> as false rather than fabricating bytes, so your observer must check
/// before reading <see cref="Bytes"/>.
/// </summary>
/// <param name="Available">Whether the payload was read; false means it could not be retrieved.</param>
/// <param name="Bytes">The job's stored payload bytes when <see cref="Available"/>; empty otherwise.</param>
public readonly record struct ObserverPayload(bool Available, ReadOnlyMemory<byte> Bytes)
{
    /// <summary>The payload was read.</summary>
    /// <param name="bytes">The job's stored payload bytes.</param>
    /// <returns>An available result carrying <paramref name="bytes"/>.</returns>
    public static ObserverPayload Present(ReadOnlyMemory<byte> bytes) => new(Available: true, bytes);

    /// <summary>
    /// The payload could not be read — the job was purged under retention, or no payload source was
    /// wired. Your observer must tolerate this, since lazy reads race the retention sweep.
    /// </summary>
    public static ObserverPayload NotAvailable { get; } = new(Available: false, default);
}

/// <summary>
/// The lazy payload accessor on an <see cref="ObserverContext"/>. The store read is deferred until
/// <see cref="GetAsync"/> is first called, so a delivery that never reaches for the payload pays no
/// read cost; the first read is memoized, so repeated reads within one invocation hit the store
/// once. An unwired accessor reports <see cref="ObserverPayload.NotAvailable"/>. Not safe for
/// concurrent reads — an observer callback is invoked for one delivery at a time.
/// </summary>
/// <param name="read">The deferred store read, or null for an accessor with no payload source.</param>
public sealed class ObserverPayloadAccessor(Func<CancellationToken, ValueTask<ObserverPayload>>? read)
{
    private ObserverPayload? _cached;

    /// <summary>An accessor with no payload source — always reports <see cref="ObserverPayload.NotAvailable"/>.</summary>
    public static ObserverPayloadAccessor Unavailable { get; } = new(read: null);

    /// <summary>
    /// Reads the payload, lazily and at most once. The first call performs the store read and caches
    /// it; later calls return the cached result. Reports the absent case when the job has been purged
    /// under retention or no payload source was wired.
    /// </summary>
    /// <param name="cancellationToken">Cancels the underlying store read.</param>
    /// <returns>
    /// The payload when it could be read, or a result whose <see cref="ObserverPayload.Available"/> is
    /// false when the job is gone or no source was wired.
    /// </returns>
    public async ValueTask<ObserverPayload> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached)
        {
            return cached;
        }
        var result = read is null ? ObserverPayload.NotAvailable : await read(cancellationToken);
        _cached = result;
        return result;
    }
}
