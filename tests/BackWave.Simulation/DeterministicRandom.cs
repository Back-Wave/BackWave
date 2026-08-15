namespace BackWave.Tests.Simulation;

/// <summary>
/// PCG32. The CLR's Random is implementation-defined across runtime versions; simulation
/// seeds must replay identically forever, so the Simulator owns its generator.
/// </summary>
internal sealed class DeterministicRandom(ulong seed)
{
    private ulong _state = seed + 0x9E3779B97F4A7C15UL;

    public uint NextUInt()
    {
        _state = _state * 6364136223846793005UL + 1442695040888963407UL;
        var xorshifted = (uint)(((_state >> 18) ^ _state) >> 27);
        var rotation = (int)(_state >> 59);
        return (xorshifted >> rotation) | (xorshifted << (-rotation & 31));
    }

    public double NextDouble() => NextUInt() * (1.0 / 4294967296.0);

    public int Next(int maxExclusive) => (int)(NextUInt() % (uint)maxExclusive);

    public TimeSpan NextTimeSpan(TimeSpan maxInclusive)
        => TimeSpan.FromTicks((long)(NextDouble() * maxInclusive.Ticks));

    public Guid NextGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < 4; i++)
        {
            BitConverter.TryWriteBytes(bytes[(i * 4)..], NextUInt());
        }
        return new Guid(bytes);
    }
}
