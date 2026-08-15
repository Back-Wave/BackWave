namespace BackWave.Jobs;

/// <summary>
/// Marks a job for source generation. On a payload record, the generator emits its serialization
/// and registry entry (the handler is the <see cref="IJobHandler{TJob}"/> implementation found in
/// the same compilation). On a method, the generator also writes the payload record and handler
/// boilerplate from the method's signature. The Wire Name is mandatory and explicit — never derived
/// from CLR names — so a renamed type or method never silently changes the identity stored with a
/// job.
/// </summary>
/// <param name="wireName">
/// The stable Wire Name identifying this job type on the wire and in storage. Must be unique across
/// all jobs in the application.
/// </param>
/// <example>
/// Class form — attribute the payload record; the handler in the same compilation is paired automatically:
/// <code>
/// [Job("order-charged")]
/// public sealed record OrderCharged(Guid OrderId);
///
/// public sealed class OrderChargedHandler : IJobHandler&lt;OrderCharged&gt;
/// {
///     public Task HandleAsync(OrderCharged job, JobContext context, CancellationToken cancellationToken)
///         => /* ... */;
/// }
/// </code>
/// Method form — attribute a handler method; the generator writes the payload record and handler:
/// <code>
/// [Job("send-receipt")]
/// public Task SendReceipt(Guid orderId, CancellationToken cancellationToken)
///     => /* ... */;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class JobAttribute(string wireName) : Attribute
{
    /// <summary>
    /// The stable identifier for this job type on the wire and in storage. Mandatory and explicit;
    /// never derived from the CLR type or method name.
    /// </summary>
    public string WireName { get; } = wireName;

    /// <summary>The Queue jobs of this type go to unless overridden at enqueue time.</summary>
    public string Queue { get; set; } = "default";

    /// <summary>
    /// Default Tag <b>Labels</b> every job of this type starts with — additive only: these always
    /// union into the per-enqueue Tags, never subtracted. Only Labels (bare strings) are expressible
    /// here, because the attribute takes compile-time constants; a key/value Tag cannot be encoded as
    /// a single constant. A caller can still attach a key/value Tag at enqueue time.
    /// </summary>
    public string[] Labels { get; set; } = [];
}
