using BackWave.Storage;

namespace BackWave.Jobs;

/// <summary>
/// The result of pulling a transitive Dependency ancestor's output: the ancestor's already-decided
/// <see cref="AncestorState"/> alongside the deserialized <see cref="Output"/>, so a dependent can
/// branch on "did the thing I depend on succeed?" without a second read. This only surfaces settled
/// facts — reading it never causes any node to be created, skipped, or reordered.
/// <para>
/// <b>Absence is a normal result</b>: a failed, cancelled, or discarded ancestor — or a succeeded one
/// that emitted nothing — yields <see cref="HasOutput"/> = false and a null <see cref="Output"/>,
/// never a throw. Output is never guaranteed present, so check <see cref="HasOutput"/> (or branch on
/// <see cref="AncestorState"/>) before using <see cref="Output"/>.
/// </para>
/// </summary>
/// <typeparam name="T">The output value type the ancestor produced.</typeparam>
/// <param name="AncestorState">The ancestor's current (terminal) job state.</param>
/// <param name="HasOutput">True only when the ancestor persisted a non-null output blob.</param>
/// <param name="Output">The deserialized output, or <c>default</c> when <see cref="HasOutput"/> is false.</param>
public sealed record DependencyOutput<T>(JobState AncestorState, bool HasOutput, T? Output);
