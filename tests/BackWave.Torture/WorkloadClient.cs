using BackWave.Core;
using BackWave.Storage;

namespace BackWave.Torture;

/// <summary>
/// One synthetic client: a seeded-PRNG loop over the store surface with engineered collision
/// pressure. It plays the roles the production shell would (claimer, reporter, heartbeater, lease
/// sweeper) *and* the operator (pause/cancel/limit-set), but with adversarial timing. Every store
/// interaction is journaled; the journal is half the oracle.
///
/// Workload discipline the oracles rely on (violating these here would make the audit unsound):
/// - designated-unroutable wires are ALWAYS reported Unroutable and never "executed";
/// - a Cancelled outcome is only reported when the store said CancelRequested;
/// - Failure retries respect the attempt ceiling (NextDueTime null at the ceiling);
/// - the governed queue's limit is never touched (it is set once, before the workload).
/// </summary>
internal sealed class WorkloadClient(
    int index, IJobStore store, KeySpace keys, Journal journal, TortureOptions options, DateTimeOffset started)
{
    private readonly Random _rng = new(unchecked((int)SplitMix64.Next(keys.Seed ^ (ulong)(index + 1))));
    private readonly string _client = $"c{index:D2}";
    private readonly string _workerId = $"torture-{keys.Seed:x8}-c{index:D2}";
    private readonly List<Guid> _recentJobs = [];
    private readonly RetryDisposition _disposition =
        new RetryPolicy { MaxAttempts = options.MaxAttempts, Backoff = _ => TimeSpan.FromMilliseconds(300) }
            .ToDisposition();

    private long _ops;

    public long OpsIssued => _ops;

    public async Task RunAsync(CancellationToken timebox)
    {
        while (!timebox.IsCancellationRequested)
        {
            var roll = _rng.Next(100);
            try
            {
                var op = roll switch
                {
                    < 30 => EnqueueAsync(),
                    < 55 => ClaimAndProcessAsync(),
                    < 63 => ExpireLeasesAsync(),
                    < 70 => CancelAsync(),
                    < 75 => RequeueAsync(),
                    < 80 => PauseOrResumeAsync(),
                    < 85 => SetLimitAsync(),
                    < 92 => WorkflowAsync(),
                    _ => StrayHeartbeatAsync(),
                };
                await op;
            }
            catch (OperationCanceledException) when (timebox.IsCancellationRequested)
            {
                break;
            }
            _ops++;
        }
    }

    // ---- ops ----------------------------------------------------------------------------------

    private async Task EnqueueAsync()
    {
        var collide = _rng.Next(100) < 35;
        if (collide)
        {
            // Barrier alignment: every client's collision-pool enqueue fires on a shared wall-clock
            // boundary, so the same id's FIRST inserts race across connections — without this, the
            // check-then-insert window (sub-ms) is essentially never hit by natural timing.
            await AlignToBoundaryAsync();
        }
        var jobId = collide ? keys.CollisionJobId(WindowIndex(perSecond: 1.5, width: 4)) : Guid.NewGuid();
        var queue = PickQueue();
        var unroutable = _rng.Next(100) < 8;
        var wire = unroutable
            ? keys.UnroutableWires[_rng.Next(keys.UnroutableWires.Count)]
            : keys.RoutableWires[_rng.Next(keys.RoutableWires.Count)];
        var tags = _rng.Next(100) < 50 ? PickTags() : JobTags.Empty;
        var parents = _rng.Next(100) < 15 && _recentJobs.Count > 0
            ? new[] { _recentJobs[_rng.Next(_recentJobs.Count)] }
            : [];
        var payload = new byte[_rng.Next(8, 64)];
        _rng.NextBytes(payload);

        var job = new NewJob(jobId, wire, payload, queue, Now().AddMilliseconds(_rng.Next(100) < 70 ? _rng.Next(500) : _rng.Next(2500)))
        {
            Parents = parents,
            Mode = _rng.Next(100) < 70 ? DependencyMode.OnSuccess : DependencyMode.OnAnyTerminal,
            Tags = tags,
        };

        var entry = await Call(Ops.Enqueue, async () =>
        {
            var t0 = Ticks();
            var result = await store.EnqueueAsync(job, Now());
            return new JournalEntry
            {
                Client = _client, Op = Ops.Enqueue, T0 = t0, T1 = Ticks(),
                JobId = jobId, Queue = queue, Wire = wire, Result = result.ToString(),
                Tags = TagStrings(tags), Parents = parents.Length > 0 ? parents : null,
                Mode = parents.Length > 0 ? job.Mode.ToString() : null,
            };
        });
        if (entry?.Result == nameof(EnqueueResult.Ok))
        {
            Remember(jobId);
        }
    }

    private async Task ClaimAndProcessAsync()
    {
        var queues = PickClaimQueues();
        var leaseDuration = TimeSpan.FromMilliseconds(_rng.Next(2000, 8000));
        var request = new ClaimRequest(_workerId, queues, _rng.Next(1, 9), leaseDuration, Now());

        var claimed = new List<JobRecord>();
        await Call(Ops.Claim, async () =>
        {
            var t0 = Ticks();
            var records = await store.ClaimAsync(request);
            var t1 = Ticks();
            foreach (var record in records)
            {
                claimed.Add(record);
                journal.Record(new JournalEntry
                {
                    Client = _client, Op = Ops.Claim, T0 = t0, T1 = t1,
                    JobId = record.JobId, Attempt = record.Attempt, Queue = record.Queue, Wire = record.WireName,
                    LeaseExpiry = record.LeaseExpiry?.UtcTicks, CancelRequested = record.CancelRequested,
                });
            }
            return null;
        });

        if (claimed.Count == 0)
        {
            return;
        }

        // Simulate execution, then report — sometimes singly, sometimes as one batch, sometimes
        // abandoning the lease entirely so ExpireLeases has real work.
        var batch = new List<OutcomeReport>();
        foreach (var record in claimed)
        {
            var cancelRequested = record.CancelRequested;

            if (_rng.Next(100) < 15)
            {
                var heartbeat = await HeartbeatAsync(record);
                cancelRequested |= heartbeat;
            }

            if (_rng.Next(100) < 8)
            {
                continue; // abandon: no outcome, the lease lapses
            }

            var executed = false;
            JobOutcome outcome;
            string[]? tagStrings = null;
            JobTags? addedTags = null;
            if (keys.IsUnroutable(record.WireName))
            {
                outcome = new JobOutcome.Unroutable("torture: designated unroutable");
            }
            else if (cancelRequested)
            {
                outcome = new JobOutcome.Cancelled("torture: cooperative cancel");
            }
            else
            {
                executed = true;
                await Task.Delay(_rng.Next(30));
                if (_rng.Next(100) < 70)
                {
                    outcome = new JobOutcome.Success();
                }
                else
                {
                    outcome = new JobOutcome.Failure(
                        record.Attempt < options.MaxAttempts ? Now().AddMilliseconds(_rng.Next(200, 1200)) : null,
                        "torture: injected failure");
                }
                if (_rng.Next(100) < 40)
                {
                    var picked = PickTags();
                    addedTags = picked;
                    tagStrings = TagStrings(picked);
                }
            }

            if (_rng.Next(100) < 40)
            {
                batch.Add(new OutcomeReport(record.JobId, _workerId, record.Attempt, outcome) { AddedTags = addedTags });
                continue;
            }

            await Call(Ops.Outcome, async () =>
            {
                var t0 = Ticks();
                var result = await store.ReportOutcomeAsync(
                    record.JobId, _workerId, record.Attempt, outcome, Now(),
                    failureDetail: outcome is JobOutcome.Failure ? "torture failure detail" : null,
                    addedTags: addedTags,
                    output: outcome is JobOutcome.Success && _rng.Next(100) < 20 ? new byte[] { 0xBA, 0xC4, 0x0A, 0x0E } : null);
                return new JournalEntry
                {
                    Client = _client, Op = Ops.Outcome, T0 = t0, T1 = Ticks(),
                    JobId = record.JobId, Attempt = record.Attempt, Queue = record.Queue,
                    Result = result.ToString(), Executed = executed, Tags = tagStrings,
                    Detail = outcome.GetType().Name,
                };
            });
        }

        if (batch.Count > 0)
        {
            await Call(Ops.Outcome, async () =>
            {
                var t0 = Ticks();
                var results = await store.ReportOutcomesAsync(batch, Now());
                var t1 = Ticks();
                foreach (var result in results)
                {
                    var report = batch.First(b => b.JobId == result.JobId);
                    journal.Record(new JournalEntry
                    {
                        Client = _client, Op = Ops.Outcome, T0 = t0, T1 = t1,
                        JobId = result.JobId, Attempt = report.Attempt,
                        Result = result.Result.ToString(),
                        Executed = report.Outcome is JobOutcome.Success or JobOutcome.Failure,
                        Tags = report.AddedTags is { } added ? TagStrings(added) : null,
                        Detail = report.Outcome.GetType().Name,
                    });
                }
                return null;
            });
        }
    }

    private async Task<bool> HeartbeatAsync(JobRecord record)
    {
        var cancelRequested = false;
        await Call(Ops.Heartbeat, async () =>
        {
            var duration = TimeSpan.FromMilliseconds(_rng.Next(2000, 8000));
            var now = Now();
            var t0 = Ticks();
            var results = await store.HeartbeatAsync(_workerId, [record.JobId], duration, now);
            var result = results.Count > 0 ? results[0] : null;
            cancelRequested = result?.CancelRequested ?? false;
            return new JournalEntry
            {
                Client = _client, Op = Ops.Heartbeat, T0 = t0, T1 = Ticks(),
                JobId = record.JobId, Attempt = record.Attempt,
                Result = result is null ? "Missing" : (result.Renewed ? "Renewed" : "Lost"),
                // A renewal RESETS the expiry — possibly earlier than the claim's. The interval
                // audit needs it to keep its "definitely live until" bound sound.
                LeaseExpiry = result?.Renewed == true ? (now + duration).UtcTicks : null,
                CancelRequested = cancelRequested,
            };
        });
        return cancelRequested;
    }

    private Task StrayHeartbeatAsync()
        // Heartbeats for leases this worker usually does not hold — pure fence pressure. It CAN hit
        // a lease this same worker does hold, and then it renews (and possibly shortens) it, so the
        // new expiry must be journaled for the interval audit.
        => Call(Ops.Heartbeat, async () =>
        {
            var jobId = PickKnownJobId();
            var duration = TimeSpan.FromSeconds(4);
            var now = Now();
            var t0 = Ticks();
            var results = await store.HeartbeatAsync(_workerId, [jobId], duration, now);
            var renewed = results.Count > 0 && results[0].Renewed;
            return new JournalEntry
            {
                Client = _client, Op = Ops.Heartbeat, T0 = t0, T1 = Ticks(),
                JobId = jobId, Result = renewed ? "Renewed" : "Lost",
                LeaseExpiry = renewed ? (now + duration).UtcTicks : null,
                Detail = "stray",
            };
        });

    private Task ExpireLeasesAsync()
        => Call(Ops.Expire, async () =>
        {
            var t0 = Ticks();
            var swept = await store.ExpireLeasesAsync(Now(), 100, keys.AllQueues, _disposition);
            return new JournalEntry
            {
                Client = _client, Op = Ops.Expire, T0 = t0, T1 = Ticks(), Result = swept.ToString(),
            };
        });

    private Task CancelAsync()
        => Call(Ops.Cancel, async () =>
        {
            var jobId = PickKnownJobId();
            var t0 = Ticks();
            var result = await store.CancelJobAsync(jobId, _workerId, Now());
            return new JournalEntry
            {
                Client = _client, Op = Ops.Cancel, T0 = t0, T1 = Ticks(),
                JobId = jobId, Result = result.ToString(),
            };
        });

    private Task RequeueAsync()
        => Call(Ops.Requeue, async () =>
        {
            var jobId = PickKnownJobId();
            var t0 = Ticks();
            var result = await store.RequeueAsync(jobId, _workerId, Now());
            return new JournalEntry
            {
                Client = _client, Op = Ops.Requeue, T0 = t0, T1 = Ticks(),
                JobId = jobId, Result = result.ToString(),
            };
        });

    private Task PauseOrResumeAsync()
    {
        var queue = keys.ConfigQueues[_rng.Next(keys.ConfigQueues.Count)];
        var pause = _rng.Next(2) == 0;
        return Call(pause ? Ops.Pause : Ops.Resume, async () =>
        {
            var t0 = Ticks();
            if (pause)
            {
                await store.PauseQueueAsync(queue, _workerId, Now());
            }
            else
            {
                await store.ResumeQueueAsync(queue, _workerId, Now());
            }
            return new JournalEntry
            {
                Client = _client, Op = pause ? Ops.Pause : Ops.Resume, T0 = t0, T1 = Ticks(), Queue = queue,
            };
        });
    }

    private Task SetLimitAsync()
    {
        // Never the governed queue — its limit is static so the I3 overlap audit stays sound.
        var queue = keys.ConfigQueues[_rng.Next(keys.ConfigQueues.Count)];
        int? limit = _rng.Next(100) < 25 ? null : _rng.Next(1, 5);
        return Call(Ops.Limit, async () =>
        {
            var t0 = Ticks();
            await store.SetConcurrencyLimitAsync(queue, limit, _workerId, Now());
            return new JournalEntry
            {
                Client = _client, Op = Ops.Limit, T0 = t0, T1 = Ticks(), Queue = queue,
                Result = limit?.ToString() ?? "null",
            };
        });
    }

    private async Task WorkflowAsync()
    {
        var append = _rng.Next(100) < 25;
        await AlignToBoundaryAsync(); // workflow ids are always pool ids — always align (see EnqueueAsync)
        var workflowId = keys.CollisionWorkflowId(WindowIndex(perSecond: 0.5, width: 2));
        var queue = keys.GeneralQueues[_rng.Next(keys.GeneralQueues.Count)];

        var members = new List<NewJob>();
        var count = append ? _rng.Next(1, 3) : _rng.Next(2, 5);
        for (var i = 0; i < count; i++)
        {
            // Occasionally use a collision-pool JobId inside a workflow so workflow-create races
            // plain enqueues of the same id, not just other workflow creates.
            var jobId = !append && _rng.Next(100) < 15 ? keys.CollisionJobId(WindowIndex(1.5, 4)) : Guid.NewGuid();
            var parents = !append && i > 0
                ? (_rng.Next(100) < 60 ? new[] { members[i - 1].JobId } : new[] { members[0].JobId })
                : Array.Empty<Guid>();
            members.Add(new NewJob(jobId, keys.RoutableWires[_rng.Next(keys.RoutableWires.Count)],
                new byte[] { 1, 2, 3 }, queue, Now())
            {
                Parents = parents,
            });
        }

        var definition = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Name = $"torture-{workflowId.ToString()[..8]}",
            Members = members,
            IsAppend = append,
        };

        var entry = await Call(Ops.Workflow, async () =>
        {
            var t0 = Ticks();
            var result = await store.EnqueueWorkflowAsync(definition, Now());
            return new JournalEntry
            {
                Client = _client, Op = Ops.Workflow, T0 = t0, T1 = Ticks(),
                WorkflowId = workflowId, Result = result.ToString(),
                Detail = append ? "append" : "create",
            };
        });

        if (entry?.Result == nameof(WorkflowEnqueueResult.Ok))
        {
            // Journal each member as an accepted enqueue so durability/provenance audits see them.
            foreach (var member in members)
            {
                journal.Record(new JournalEntry
                {
                    Client = _client, Op = Ops.Enqueue, T0 = entry.T0, T1 = entry.T1,
                    JobId = member.JobId, Queue = member.Queue, Wire = member.WireName,
                    WorkflowId = workflowId, Result = nameof(EnqueueResult.Ok),
                    Parents = member.Parents.Count > 0 ? [.. member.Parents] : null,
                    Mode = member.Parents.Count > 0 ? member.Mode.ToString() : null,
                });
                Remember(member.JobId);
            }
        }
    }

    // ---- plumbing -----------------------------------------------------------------------------

    /// <summary>
    /// Runs one store call, journaling its entry on success, a transient-fault entry on classified
    /// contention noise, and a RawStoreException entry — a violation — on anything else. Raw
    /// provider exceptions escaping the store surface are exactly the 0194/0195 bug class.
    /// </summary>
    private async Task<JournalEntry?> Call(string op, Func<Task<JournalEntry?>> action)
    {
        try
        {
            var entry = await action();
            if (entry is not null)
            {
                journal.Record(entry);
            }
            return entry;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JobOutputTooLargeException)
        {
            return null; // defined contract behavior, not a finding
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            journal.Record(new JournalEntry
            {
                Client = _client, Op = Ops.TransientFault, T0 = Ticks(), T1 = Ticks(),
                Result = op, Detail = exception.GetType().Name,
            });
            return null;
        }
        catch (Exception exception)
        {
            journal.Record(new JournalEntry
            {
                Client = _client, Op = Ops.UnexpectedException, T0 = Ticks(), T1 = Ticks(),
                Result = op, Detail = $"{exception.GetType().FullName}: {exception.Message}",
            });
            return null;
        }
    }

    /// <summary>Set by the orchestrator to the target's classifier.</summary>
    public required Func<Exception, bool> IsTransient { get; init; }

    private static DateTimeOffset Now() => DateTimeOffset.UtcNow;

    private static long Ticks() => DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>A collision-pool index from a window that slides with elapsed wall time.</summary>
    private int WindowIndex(double perSecond, int width)
        => (int)((DateTimeOffset.UtcNow - started).TotalSeconds * perSecond) + _rng.Next(width);

    private static async Task AlignToBoundaryAsync()
    {
        var wait = 250 - (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 250);
        await Task.Delay(wait);
    }

    private string PickQueue()
    {
        var roll = _rng.Next(100);
        if (roll < 25)
        {
            return keys.GovernedQueue;
        }
        if (roll < 75)
        {
            return keys.GeneralQueues[_rng.Next(keys.GeneralQueues.Count)];
        }
        return keys.ConfigQueues[_rng.Next(keys.ConfigQueues.Count)];
    }

    private IReadOnlyList<string> PickClaimQueues()
    {
        var roll = _rng.Next(100);
        if (roll < 30)
        {
            return [keys.GovernedQueue];
        }
        if (roll < 60)
        {
            return keys.GeneralQueues;
        }
        if (roll < 80)
        {
            return keys.ConfigQueues;
        }
        return keys.AllQueues;
    }

    private JobTags PickTags()
    {
        var tags = JobTags.Empty;
        var count = _rng.Next(1, 3);
        for (var i = 0; i < count; i++)
        {
            tags = tags.With(keys.TagVocabulary[_rng.Next(keys.TagVocabulary.Count)]);
        }
        return tags;
    }

    private Guid PickKnownJobId()
        => _rng.Next(100) < 60 || _recentJobs.Count == 0
            ? keys.CollisionJobId(WindowIndex(1.5, 4))
            : _recentJobs[_rng.Next(_recentJobs.Count)];

    private void Remember(Guid jobId)
    {
        _recentJobs.Add(jobId);
        if (_recentJobs.Count > 256)
        {
            _recentJobs.RemoveAt(0);
        }
    }

    private static string[] TagStrings(JobTags tags)
        => [.. tags.Select(t => t.Key.Length == 0 ? t.Value : $"{t.Key}={t.Value}")];
}
