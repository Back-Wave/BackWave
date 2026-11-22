namespace BackWave.Storage;

internal readonly record struct CommitHorizon(int Epoch, long? Candidate, long Horizon)
{
    internal int PreviousEpoch => 1 - Epoch;

    internal CommitHorizon Advance(long maxPosition, bool previousEpochFree, bool currentEpochFree)
        => (previousEpochFree, currentEpochFree) switch
        {
            (true, true) => this with { Horizon = Math.Max(Horizon, maxPosition) },
            (true, false) => new(PreviousEpoch, maxPosition, Math.Max(Horizon, Candidate ?? Horizon)),
            _ => this,
        };
}
