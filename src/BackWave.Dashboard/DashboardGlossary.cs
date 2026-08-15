using BackWave.Storage;

namespace BackWave.Dashboard;

/// <summary>
/// Glossary-normative presentation helpers shared by the Razor views. CONTEXT.md is
/// normative for UI copy, so state spellings and blurbs live in one place — the dashboard
/// is where most users first meet these terms. Also maps each state to a design tone
/// (the badge/progress colour vocabulary: wave = in flight, green = done, red = failed).
/// </summary>
internal static class DashboardGlossary
{
    /// <summary>Reading order for state columns: lifecycle left-to-right, terminals last.</summary>
    public static readonly JobState[] StateOrder =
    [
        JobState.Scheduled, JobState.AwaitingParent, JobState.Leased,
        JobState.Succeeded, JobState.Cancelled, JobState.DeadLettered, JobState.Quarantined,
    ];

    /// <summary>Glossary spellings, exactly.</summary>
    public static string StateName(JobState state) => state switch
    {
        JobState.Scheduled => "Scheduled",
        JobState.AwaitingParent => "Awaiting Parent",
        JobState.Leased => "Leased",
        JobState.Succeeded => "Succeeded",
        JobState.Cancelled => "Cancelled",
        JobState.DeadLettered => "Dead-Lettered",
        JobState.Quarantined => "Quarantined",
        _ => state.ToString(),
    };

    /// <summary>Design tone for a state's badge ("wave", "green", "red", "amber", "navy").</summary>
    public static string Tone(JobState state) => state switch
    {
        JobState.Scheduled => "amber",
        JobState.AwaitingParent => "navy",
        JobState.Leased => "wave",
        JobState.Succeeded => "green",
        JobState.Cancelled => "navy",
        JobState.DeadLettered => "red",
        JobState.Quarantined => "red",
        _ => "navy",
    };

    /// <summary>UTC, sortable, second precision — the dashboard's one time format.</summary>
    public static string Instant(DateTimeOffset instant) => instant.ToUniversalTime().ToString("u");

    /// <summary>
    /// A short human-readable gloss of a canonical 6-field cron expression (seconds first) — e.g.
    /// "Every hour", "Every 5 minutes", "Daily at 02:00", "Weekly on Monday at 08:00" — or
    /// <see langword="null"/> when the expression is not one of the common shapes and only the raw
    /// cron should be shown. Presentation sugar only; the stored cron stays authoritative. Kept
    /// deliberately conservative: anything with a non-zero seconds field, a month restriction, or a
    /// list/range in a time field falls through to null rather than risk a misleading gloss.
    /// </summary>
    public static string? CronFriendly(string canonical)
    {
        var f = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 6) return null;
        var (sec, min, hour, dom, month, dow) = (f[0], f[1], f[2], f[3], f[4], f[5]);

        // Only gloss "at second 0" schedules; a seconds pattern or month restriction stays raw.
        if (sec != "0" || month != "*") return null;

        // Every minute / every N minutes: "0 * * * * *" or "0 */N * * * *"
        if (hour == "*" && dom == "*" && dow == "*")
        {
            if (min == "*") return "Every minute";
            if (TryStep(min, out var stepMin)) return $"Every {stepMin} minutes";
        }

        // Every hour / every N hours (on the hour): "0 0 * * * *" or "0 0 */N * * *"
        if (min == "0" && dom == "*" && dow == "*")
        {
            if (hour == "*") return "Every hour";
            if (TryStep(hour, out var stepHour)) return $"Every {stepHour} hours";
        }

        // Every hour at a fixed minute: "0 M * * * *"
        if (hour == "*" && dom == "*" && dow == "*" && TryFixed(min, 0, 59, out var atMin))
            return $"Every hour at :{atMin:D2}";

        // Fixed time of day → daily / weekly / monthly.
        if (TryFixed(min, 0, 59, out var m) && TryFixed(hour, 0, 23, out var h))
        {
            var at = $"{h:D2}:{m:D2}";
            if (dom == "*" && dow == "*") return $"Daily at {at}";
            if (dom == "*" && TryFixed(dow, 0, 7, out var d)) return $"Weekly on {DayName(d)} at {at}";
            if (dow == "*" && TryFixed(dom, 1, 31, out var day)) return $"Monthly on the {Ordinal(day)} at {at}";
        }

        return null;

        static bool TryStep(string field, out int step)
            => (step = field.StartsWith("*/", StringComparison.Ordinal)
                && int.TryParse(field[2..], out var s) && s > 1 ? s : 0) > 0;

        static bool TryFixed(string field, int min, int max, out int value)
            => int.TryParse(field, out value) && value >= min && value <= max;

        static string DayName(int dow) => (dow % 7) switch
        {
            0 => "Sunday", 1 => "Monday", 2 => "Tuesday", 3 => "Wednesday",
            4 => "Thursday", 5 => "Friday", _ => "Saturday",
        };

        static string Ordinal(int n) => (n % 100 is >= 11 and <= 13 ? 0 : n % 10) switch
        {
            1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th",
        };
    }

    /// <summary>
    /// A Job Tag's pill text: a Label renders as its bare value; a Keyed Tag renders as
    /// <c>key:value</c>. This is a <b>display</b> choice only — the Tag is stored structurally and the
    /// colon is never parsed, so a Label whose value itself contains a colon shows that colon as-is.
    /// </summary>
    public static string TagLabel(JobTag tag) => tag.IsLabel ? tag.Value : $"{tag.Key}:{tag.Value}";

    /// <summary>Terminal states are settled; only non-terminal jobs can be Cancelled.</summary>
    public static bool IsTerminal(JobState state) => state is
        JobState.Succeeded or JobState.Cancelled or JobState.DeadLettered or JobState.Quarantined;

    /// <summary>A Workflow's derived status spelled exactly: always a projection of its
    /// members, never stored.</summary>
    public static string WorkflowStatusName(WorkflowStatus status) => status switch
    {
        WorkflowStatus.Running => "Running",
        WorkflowStatus.Failed => "Failed",
        WorkflowStatus.Cancelled => "Cancelled",
        WorkflowStatus.Succeeded => "Succeeded",
        _ => status.ToString(),
    };

    /// <summary>One-line blurb for a Workflow's derived status — the failure-dominates precedence, plainly.</summary>
    public static string WorkflowStatusBlurb(WorkflowStatus status) => status switch
    {
        WorkflowStatus.Running => "at least one member is still non-terminal",
        WorkflowStatus.Failed => "all members terminal, at least one Dead-Lettered or Quarantined (failure dominates)",
        WorkflowStatus.Cancelled => "all members terminal, no failures, at least one Cancelled (e.g. an operator cancel)",
        WorkflowStatus.Succeeded => "every member Succeeded",
        _ => "",
    };

    /// <summary>Design tone for a Workflow status badge — wave for in-flight, green done, red failure, navy cancelled.</summary>
    public static string WorkflowStatusTone(WorkflowStatus status) => status switch
    {
        WorkflowStatus.Running => "wave",
        WorkflowStatus.Failed => "red",
        WorkflowStatus.Cancelled => "navy",
        WorkflowStatus.Succeeded => "green",
        _ => "navy",
    };
}
