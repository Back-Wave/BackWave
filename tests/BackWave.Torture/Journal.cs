using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackWave.Torture;

/// <summary>Journal op names — string constants so the JSONL reads plainly.</summary>
internal static class Ops
{
    public const string Enqueue = "enqueue";
    public const string Workflow = "workflow";
    public const string Claim = "claim";
    public const string Outcome = "outcome";
    public const string Heartbeat = "heartbeat";
    public const string Expire = "expire";
    public const string Cancel = "cancel";
    public const string Requeue = "requeue";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Limit = "limit";
    public const string UnexpectedException = "unexpected-exception";
    public const string TransientFault = "transient-fault";
    public const string ClientCrash = "client-crash";
}

/// <summary>
/// One observation in a client's journal. The journal is the client-side half of the oracle: two
/// clients observing the same attempt's execution is a violation even when the database's final
/// state looks clean, and that can only be seen from what each connection was told.
/// T0/T1 are UTC ticks bracketing the store call (call start / return); on one host they are
/// directly comparable across processes.
/// </summary>
internal sealed record JournalEntry
{
    public required string Client { get; init; }
    public required string Op { get; init; }
    public required long T0 { get; init; }
    public required long T1 { get; init; }
    public Guid? JobId { get; init; }
    public int? Attempt { get; init; }
    public string? Queue { get; init; }
    public string? Wire { get; init; }
    public Guid? WorkflowId { get; init; }
    public string? Result { get; init; }
    /// <summary>Lease expiry (UTC ticks) as the claim reported it.</summary>
    public long? LeaseExpiry { get; init; }
    public bool? CancelRequested { get; init; }
    /// <summary>True when the client simulated execution of this attempt (never true for designated-unroutable wires).</summary>
    public bool? Executed { get; init; }
    /// <summary>Tags this entry attached (enqueue Tags or an outcome's addedTags), as "key=value" / "label" strings.</summary>
    public string[]? Tags { get; init; }
    /// <summary>Gating parents recorded at enqueue, for cancel-provenance and orphan diagnosis.</summary>
    public Guid[]? Parents { get; init; }
    public string? Mode { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Thread-safe journal collector; serializes to JSONL so child processes can hand theirs to the parent.</summary>
internal sealed class Journal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentQueue<JournalEntry> _entries = new();

    public void Record(JournalEntry entry) => _entries.Enqueue(entry);

    public IReadOnlyList<JournalEntry> Entries => [.. _entries];

    public async Task WriteAsync(string path)
    {
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream);
        foreach (var entry in _entries)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonOptions));
        }
    }

    public static async Task<List<JournalEntry>> ReadAsync(string path)
    {
        var entries = new List<JournalEntry>();
        await foreach (var line in File.ReadLinesAsync(path))
        {
            if (line.Length == 0)
            {
                continue;
            }
            entries.Add(JsonSerializer.Deserialize<JournalEntry>(line, JsonOptions)!);
        }
        return entries;
    }
}
