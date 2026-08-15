using Microsoft.Extensions.Logging;

namespace BackWave.Pro;

// BackWave Pro's own source-generated [LoggerMessage] catalog for the workflow-specific lifecycle events
// the free Core cannot see - today the conditional-gate decision. Kept in Pro (rather than the Core
// BackWave catalog) so the workflow vocabulary ships only with the Pro package. The [LoggerMessage]
// generator guards each call on IsEnabled, so a disabled level (or a NullLogger) formats nothing.
internal static partial class WorkflowLog
{
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Workflow gate '{gate}' entered the '{arm}' arm; cancelling {cancelled_count} step(s) of the "
            + "not-taken arm.")]
    internal static partial void GateDecided(ILogger logger, string gate, string arm, int cancelled_count);
}
