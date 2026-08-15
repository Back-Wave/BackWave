namespace BackWave.Storage;

/// <summary>
/// An optional store capability (detected at runtime as <c>store is IStoreFaultClassifier</c>, exactly
/// like <see cref="IWakeUpHintSource"/>) that lets a storage adapter classify provider-specific
/// exceptions the generic <c>DbException.IsTransient</c> flag does not cover.
/// <para>
/// Most networked database providers already raise their transient conditions (connection reset,
/// failover blip, deadlock victim, command timeout) with <c>IsTransient = true</c>, so the host
/// recognizes them without adapter-specific knowledge and such adapters need not implement this
/// interface. Implement it only when your provider leaves <c>IsTransient</c> unset for a fault you know
/// to be transient contention (for example, a residual busy/locked condition that survives a configured
/// busy-timeout) and which the worker should degrade-and-retry on the next tick rather than fail-stop.
/// Keeping that knowledge behind this seam lets the host stay provider-agnostic.
/// </para>
/// </summary>
public interface IStoreFaultClassifier
{
    /// <summary>
    /// Classifies a store-originated exception. Return <c>true</c> only for a transient store fault this
    /// adapter recognizes — one the worker should degrade-and-retry rather than fail-stop on. Return
    /// <c>false</c> for everything else (including invariant violations and unknown faults), which leaves
    /// the host's default classification untouched. Implementations MUST be a pure, side-effect-free
    /// inspection of the exception and MUST NOT mistake a permanent fault for a transient one, since a
    /// false positive turns a fatal error into an endless retry.
    /// </summary>
    /// <param name="exception">The exception raised by a store operation, to be classified.</param>
    /// <returns>
    /// <c>true</c> when the exception is a recognized transient store fault (retry on the next tick);
    /// <c>false</c> otherwise (defer to the host's default handling).
    /// </returns>
    bool IsTransientFault(Exception exception);
}
