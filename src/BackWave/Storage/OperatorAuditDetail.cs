namespace BackWave.Storage;

/// <summary>
/// What an operator action changed, kept on its <see cref="OperatorAuditRecord"/> next to who did it and
/// when. Each action that keeps a detail has its own sealed subtype, so a reader matches on the type and
/// reads typed values rather than parsing text. An action that keeps no detail has a null
/// <see cref="OperatorAuditRecord.Detail"/>.
/// </summary>
/// <remarks>
/// The set of subtypes is closed: only BackWave defines them, so a match over the known subtypes is
/// complete. Today <see cref="RescheduleAuditDetail"/> is the only one.
/// </remarks>
public abstract record OperatorAuditDetail
{
    // Closed hierarchy: a subtype from outside BackWave would have no stored form.
    private protected OperatorAuditDetail()
    {
    }
}

/// <summary>
/// The detail of an <see cref="OperatorAction.Reschedule"/>: the due time the job had just before the
/// move and the due time the move gave it. A second reschedule of the same job starts where the first
/// one ended, so its <see cref="From"/> is the first one's <see cref="To"/>.
/// </summary>
/// <param name="From">The job's committed due time just before the reschedule.</param>
/// <param name="To">The due time the reschedule gave the job.</param>
public sealed record RescheduleAuditDetail(DateTimeOffset From, DateTimeOffset To) : OperatorAuditDetail;
