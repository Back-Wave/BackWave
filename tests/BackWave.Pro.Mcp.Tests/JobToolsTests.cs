using System.Text.Json;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// The job read tools end-to-end through the mounted MCP endpoint (issue 0225): search_jobs with
/// its tag-predicate grammar and cursor paging, get_job's found-not-error contract, the
/// self-explaining get_job_history response, and get_job_dependencies edges.
/// </summary>
public sealed class JobToolsTests
{
    private static readonly string[] JobToolNames =
        ["search_jobs", "get_job", "get_job_history", "get_job_dependencies"];

    [Fact]
    public async Task ToolsList_ShowsAllFourJobTools_WithOutputSchemas()
    {
        await using var server = await McpTestServer.StartAsync();

        var tools = await server.Client.ListToolsAsync();

        foreach (var name in JobToolNames)
        {
            var tool = Assert.Single(tools, t => t.Name == name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.NotNull(tool.OutputSchema);
        }

        // The envelope conventions: structured output advertises its shape per tool.
        JsonElement OutputProperties(string name) =>
            tools.Single(t => t.Name == name).OutputSchema!.Value.GetProperty("properties");
        Assert.True(OutputProperties("search_jobs").TryGetProperty("jobs", out _));
        Assert.True(OutputProperties("search_jobs").TryGetProperty("nextCursor", out _));
        Assert.True(OutputProperties("search_jobs").TryGetProperty("hasMore", out _));
        Assert.True(OutputProperties("get_job").TryGetProperty("found", out _));
        Assert.True(OutputProperties("get_job_history").TryGetProperty("transitions", out _));
        Assert.True(OutputProperties("get_job_history").TryGetProperty("historyPolicy", out _));
        Assert.True(OutputProperties("get_job_dependencies").TryGetProperty("gatingParents", out _));
        Assert.True(OutputProperties("get_job_dependencies").TryGetProperty("children", out _));

        // The input contract is snake_case (the fixed tool shapes).
        var searchInputs = tools.Single(t => t.Name == "search_jobs").InputSchema!.Value.GetProperty("properties");
        foreach (var parameter in new[]
                 { "state", "queue", "wire_name", "schedule_id", "tags", "after_cursor", "sort", "max_results" })
        {
            Assert.True(searchInputs.TryGetProperty(parameter, out _), $"search_jobs is missing input '{parameter}'");
        }
    }

    [Fact]
    public async Task SearchJobs_FiltersAndReturnsFullSnapshots_NewestFirstByDefault()
    {
        await using var server = await McpTestServer.StartAsync();
        var first = await server.SeedJobAsync("critical", "send-email");
        var second = await server.SeedJobAsync("critical", "send-email");
        await server.SeedJobAsync("bulk", "resize-image");

        var result = await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["queue"] = "critical",
            ["wire_name"] = "send-email",
        });

        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.False(structured.GetProperty("hasMore").GetBoolean());
        var jobs = structured.GetProperty("jobs").EnumerateArray().ToList();
        Assert.Equal(2, jobs.Count);

        // Newest-first default: the later seed leads the page.
        Assert.Equal(second, jobs[0].GetProperty("jobId").GetGuid());
        Assert.Equal(first, jobs[1].GetProperty("jobId").GetGuid());
        Assert.True(jobs[0].GetProperty("sequence").GetInt64() > jobs[1].GetProperty("sequence").GetInt64());

        // Full snapshot fields ride along.
        Assert.Equal("critical", jobs[0].GetProperty("queue").GetString());
        Assert.Equal("send-email", jobs[0].GetProperty("wireName").GetString());
        Assert.Equal("Scheduled", jobs[0].GetProperty("state").GetString());
        Assert.Equal(0, jobs[0].GetProperty("attempt").GetInt32());
        Assert.False(jobs[0].GetProperty("cancelRequested").GetBoolean());
    }

    [Fact]
    public async Task SearchJobs_PagesViaCursor_WithHasMoreCorrectAtTheBoundary()
    {
        await using var server = await McpTestServer.StartAsync();
        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            seeded.Add(await server.SeedJobAsync("critical"));
        }

        var seen = new List<Guid>();
        long? cursor = null;

        // Page 1: full page, more behind it.
        var page1 = (await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["max_results"] = 2,
        })).StructuredContent!.Value;
        Assert.True(page1.GetProperty("hasMore").GetBoolean());
        cursor = page1.GetProperty("nextCursor").GetInt64();
        seen.AddRange(page1.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("jobId").GetGuid()));
        Assert.Equal(2, seen.Count);

        // Page 2 via the cursor: the next two, no overlap.
        var page2 = (await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["max_results"] = 2,
            ["after_cursor"] = cursor,
        })).StructuredContent!.Value;
        Assert.True(page2.GetProperty("hasMore").GetBoolean());
        cursor = page2.GetProperty("nextCursor").GetInt64();
        seen.AddRange(page2.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("jobId").GetGuid()));
        Assert.Equal(4, seen.Distinct().Count());

        // Page 3: the final row — hasMore false and no next cursor at the boundary.
        var page3 = (await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["max_results"] = 2,
            ["after_cursor"] = cursor,
        })).StructuredContent!.Value;
        Assert.False(page3.GetProperty("hasMore").GetBoolean());
        Assert.True(!page3.TryGetProperty("nextCursor", out var last) || last.ValueKind == JsonValueKind.Null);
        seen.AddRange(page3.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("jobId").GetGuid()));

        // The three pages tile the whole set exactly, newest first.
        Assert.Equal(5, seen.Count);
        Assert.Equal(Enumerable.Reverse(seeded), seen);
    }

    [Fact]
    public async Task SearchJobs_PagesThroughEveryMatch_WhenTheRequestedSizeMeetsTheStoreCap()
    {
        // Regression: at max_results == the store's monitor page cap (default 200), the naive "+1
        // sentinel" asked the store for cap + 1 rows, the store clamped that back to cap, and a full
        // final page reported hasMore=false — silently stranding every later match. Seeding more than
        // one full page and paging at the cap must still reach every job, with no loss and no dupes.
        await using var server = await McpTestServer.StartAsync();
        const int total = 300; // more than the 200 default cap: at least two pages
        var seeded = new List<Guid>();
        for (var i = 0; i < total; i++)
        {
            seeded.Add(await server.SeedJobAsync("critical"));
        }

        var seen = new List<Guid>();
        long? cursor = null;
        var pages = 0;
        while (true)
        {
            var args = new Dictionary<string, object?> { ["max_results"] = 200 };
            if (cursor is not null)
            {
                args["after_cursor"] = cursor;
            }
            var page = (await server.Client.CallToolAsync("search_jobs", args)).StructuredContent!.Value;
            seen.AddRange(page.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("jobId").GetGuid()));
            pages++;
            Assert.True(pages <= total, "paging did not terminate");
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                // The last page carries no next cursor.
                Assert.True(!page.TryGetProperty("nextCursor", out var last) || last.ValueKind == JsonValueKind.Null);
                break;
            }
            cursor = page.GetProperty("nextCursor").GetInt64();
        }

        // Every seeded job appears exactly once — no loss at the boundary, no duplicates across pages.
        Assert.Equal(total, seen.Count);
        Assert.Equal(total, seen.Distinct().Count());
        Assert.Equal(seeded.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task SearchJobs_ClampTracksTheStoresConfiguredPageCap_NotADefaultConstant()
    {
        // The clamp reads the store's actual MaxMonitorPageSize through the monitor, not a hardcoded
        // 200. A host that lowers the cap below the default must be honored, or the +1 sentinel would
        // exceed the real cap and strand later matches. Configure a cap of 10, page at (and above) it,
        // and every seeded job must still be reached with no loss and no duplicates.
        const int cap = 10;
        await using var server = await McpTestServer.StartAsync(
            bounds: StoreBounds.Default with { MaxMonitorPageSize = cap });
        const int total = 25; // more than two capped pages
        var seeded = new List<Guid>();
        for (var i = 0; i < total; i++)
        {
            seeded.Add(await server.SeedJobAsync("critical"));
        }

        // A request AT the configured cap is clamped to cap - 1 so the sentinel survives under 10.
        var first = (await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["max_results"] = cap,
        })).StructuredContent!.Value;
        Assert.Equal(cap - 1, first.GetProperty("jobs").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());

        // Paging at the configured cap tiles the whole set exactly — the proof the sentinel survived.
        var seen = new List<Guid>();
        long? cursor = null;
        var pages = 0;
        while (true)
        {
            var args = new Dictionary<string, object?> { ["max_results"] = cap };
            if (cursor is not null)
            {
                args["after_cursor"] = cursor;
            }
            var page = (await server.Client.CallToolAsync("search_jobs", args)).StructuredContent!.Value;
            seen.AddRange(page.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("jobId").GetGuid()));
            pages++;
            Assert.True(pages <= total, "paging did not terminate");
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                break;
            }
            cursor = page.GetProperty("nextCursor").GetInt64();
        }

        Assert.Equal(total, seen.Count);
        Assert.Equal(seeded.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task SearchJobs_WhenTheStoreCapIsOne_ReturnsACleanPage_NotAServerFault()
    {
        // Regression: a host with MaxMonitorPageSize == 1 drove the clamp (cap - 1) to pageSize 0, so
        // MaxResults was 1 and any match set hasMore=true over an empty jobs list — the NextCursor read
        // (jobs[^1]) then threw ArgumentOutOfRangeException and surfaced as an opaque server fault. A
        // cap of 1 is a reachable store configuration (no floor validation on StoreBounds). search_jobs
        // must degrade to a clean single-row page instead of faulting.
        await using var server = await McpTestServer.StartAsync(
            bounds: StoreBounds.Default with { MaxMonitorPageSize = 1 });
        await server.SeedJobAsync("critical");
        await server.SeedJobAsync("critical");

        var result = await server.Client.CallToolAsync("search_jobs");

        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        // The store cap admits one row per page; there is no room for a sentinel, so hasMore stays
        // false (paging cannot advance at cap 1), but the call returns cleanly rather than throwing.
        Assert.Equal(1, structured.GetProperty("jobs").GetArrayLength());
        Assert.False(structured.GetProperty("hasMore").GetBoolean());
        Assert.True(!structured.TryGetProperty("nextCursor", out var cursor) || cursor.ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData(199)]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(int.MaxValue)]
    public async Task SearchJobs_AtOrAboveTheStoreCap_ReturnsACappedPageWithAWorkingCursor(int maxResults)
    {
        // The page is capped one below the store's monitor page cap so the sentinel survives: a caller
        // asking for the cap (or more, up to int.MaxValue — which previously overflowed pageSize + 1 to
        // int.MinValue and returned an empty page) gets a full capped page plus a cursor that reaches
        // the rest. With 250 jobs there is always a second page behind the first.
        await using var server = await McpTestServer.StartAsync();
        const int total = 250;
        for (var i = 0; i < total; i++)
        {
            await server.SeedJobAsync("critical");
        }

        var page = (await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["max_results"] = maxResults,
        })).StructuredContent!.Value;

        var jobs = page.GetProperty("jobs").EnumerateArray().ToList();
        // Clamped to cap - 1 (199), never the whole 250 in one page, never empty (the overflow bug).
        Assert.Equal(199, jobs.Count);
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        var cursor = page.GetProperty("nextCursor").GetInt64();

        // The cursor advances into the remaining jobs rather than repeating the first page.
        var next = (await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["max_results"] = maxResults,
            ["after_cursor"] = cursor,
        })).StructuredContent!.Value;
        var firstIds = jobs.Select(j => j.GetProperty("jobId").GetGuid()).ToHashSet();
        var nextIds = next.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("jobId").GetGuid()).ToList();
        Assert.NotEmpty(nextIds);
        Assert.All(nextIds, id => Assert.DoesNotContain(id, firstIds));
    }

    [Fact]
    public async Task SearchJobs_TagPredicates_ThreeFormsAndAndComposition()
    {
        await using var server = await McpTestServer.StartAsync();
        var urgentAcme = await SeedTaggedJobAsync(server.Store, JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme"));
        var acmeOnly = await SeedTaggedJobAsync(server.Store, JobTags.Empty.WithTag("tenant", "acme"));
        var urgentGlobex = await SeedTaggedJobAsync(server.Store, JobTags.Empty.WithLabel("urgent").WithTag("tenant", "globex"));
        await SeedTaggedJobAsync(server.Store, JobTags.Empty); // untagged: matches nothing below

        // Bare "value": a label.
        Assert.Equal([urgentGlobex, urgentAcme], await SearchByTagsAsync(server, ["urgent"]));

        // "key=value": that exact keyed tag.
        Assert.Equal([acmeOnly, urgentAcme], await SearchByTagsAsync(server, ["tenant=acme"]));

        // "key=*": any value under the key.
        Assert.Equal([urgentGlobex, acmeOnly, urgentAcme], await SearchByTagsAsync(server, ["tenant=*"]));

        // AND-composition: both predicates must hold.
        Assert.Equal([urgentAcme], await SearchByTagsAsync(server, ["urgent", "tenant=acme"]));

        // Tags come back on the snapshot as (key, value) rows.
        var result = (await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["tags"] = new[] { "urgent", "tenant=acme" },
        })).StructuredContent!.Value;
        var tags = result.GetProperty("jobs")[0].GetProperty("tags").EnumerateArray()
            .Select(t => (t.GetProperty("key").GetString(), t.GetProperty("value").GetString()))
            .ToList();
        Assert.Contains(("", "urgent"), tags);
        Assert.Contains(("tenant", "acme"), tags);
    }

    [Theory]
    [InlineData("=acme")]
    [InlineData("tenant=")]
    [InlineData("=")]
    [InlineData("")]
    public async Task SearchJobs_MalformedTagPredicate_IsAnErrorNamingTheThreeForms(string malformed)
    {
        await using var server = await McpTestServer.StartAsync();

        var result = await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["tags"] = new[] { malformed },
        });

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        // The message names all three predicate forms so the client can self-correct.
        Assert.Contains("key=value", result.Text);
        Assert.Contains("key=*", result.Text);
        Assert.Contains("label", result.Text);
    }

    [Fact]
    public async Task SearchJobs_InvalidStateAndSort_AreInvalidInputErrors()
    {
        await using var server = await McpTestServer.StartAsync();

        var badState = await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["state"] = "Exploded",
        });
        Assert.True(badState.IsError);
        Assert.Contains("Scheduled", badState.Text); // the valid states are enumerated

        // A numeric string parses to an undefined enum value; it must be rejected, not silently
        // matched to nothing (which would look like an empty, successful page to the caller).
        var numericState = await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["state"] = "999",
        });
        Assert.True(numericState.IsError);
        Assert.Contains("Scheduled", numericState.Text);

        // A comma-separated combination bitwise-ORs to an undefined value; likewise rejected.
        var combinedState = await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["state"] = "Succeeded,Cancelled",
        });
        Assert.True(combinedState.IsError);
        Assert.Contains("Scheduled", combinedState.Text);

        var badSort = await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["sort"] = "sideways",
        });
        Assert.True(badSort.IsError);
        Assert.Contains("newest_first", badSort.Text);
        Assert.Contains("oldest_first", badSort.Text);
    }

    [Fact]
    public async Task SearchJobs_OldestFirst_ReversesTheOrder()
    {
        await using var server = await McpTestServer.StartAsync();
        var first = await server.SeedJobAsync("critical");
        var second = await server.SeedJobAsync("critical");

        var jobs = (await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["sort"] = "oldest_first",
        })).StructuredContent!.Value.GetProperty("jobs").EnumerateArray().ToList();

        Assert.Equal(first, jobs[0].GetProperty("jobId").GetGuid());
        Assert.Equal(second, jobs[1].GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task GetJob_ReturnsTheSnapshot()
    {
        await using var server = await McpTestServer.StartAsync();
        var id = await server.SeedJobAsync("critical", "send-email");

        var result = await server.Client.CallToolAsync("get_job", new Dictionary<string, object?>
        {
            ["job_id"] = id.ToString(),
        });

        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.True(structured.GetProperty("found").GetBoolean());
        var job = structured.GetProperty("job");
        Assert.Equal(id, job.GetProperty("jobId").GetGuid());
        Assert.Equal("send-email", job.GetProperty("wireName").GetString());
        Assert.Equal("Scheduled", job.GetProperty("state").GetString());
    }

    [Fact]
    public async Task GetJob_UnknownId_IsFoundFalse_NotAnError()
    {
        await using var server = await McpTestServer.StartAsync();

        var result = await server.Client.CallToolAsync("get_job", new Dictionary<string, object?>
        {
            ["job_id"] = Guid.NewGuid().ToString(),
        });

        // An unknown id is an answer, not a fault.
        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.False(structured.GetProperty("found").GetBoolean());
        Assert.True(!structured.TryGetProperty("job", out var job) || job.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task GetJob_MalformedId_IsAnInvalidInputError()
    {
        await using var server = await McpTestServer.StartAsync();

        var result = await server.Client.CallToolAsync("get_job", new Dictionary<string, object?>
        {
            ["job_id"] = "not-a-guid",
        });

        Assert.True(result.IsError);
        Assert.Contains("GUID", result.Text);
    }

    [Fact]
    public async Task GetJobHistory_ReturnsTransitions_AndStatesTheFullPolicy()
    {
        await using var server = await McpTestServer.StartAsync();
        var id = await server.SeedJobAsync("critical");

        var result = await server.Client.CallToolAsync("get_job_history", new Dictionary<string, object?>
        {
            ["job_id"] = id.ToString(),
        });

        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.Equal("TransitionsAndFailureDetail", structured.GetProperty("historyPolicy").GetString());
        Assert.True(!structured.TryGetProperty("historyNote", out var note) || note.ValueKind == JsonValueKind.Null);
        var transition = Assert.Single(structured.GetProperty("transitions").EnumerateArray());
        Assert.Equal("Scheduled", transition.GetProperty("state").GetString());
        Assert.Equal(0, transition.GetProperty("attempt").GetInt32());
        Assert.Equal(0, transition.GetProperty("ordinal").GetInt64());
    }

    [Fact]
    public async Task GetJobHistory_WhenRecordingIsOff_TheResponseSaysSo()
    {
        // A host whose store records no history: the empty timeline must be self-explaining, so
        // this test wires its own host around a policy-Off store (the shared fixture's store
        // records the full log).
        var store = new InMemoryJobStore(historyPolicy: JobHistoryPolicy.Off);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBackWave(bw => bw
            .UseStore(store)
            .UseRegistry(new JobRegistry([]))
            .AddMcp());
        await using var app = builder.Build();
        app.UseBackWaveProMcp();
        await app.StartAsync();
        var client = new McpTestClient(app.GetTestClient());

        var id = Guid.NewGuid();
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(
            new NewJob(id, "test-job", "{}"u8.ToArray(), "critical", DateTimeOffset.UtcNow),
            now: DateTimeOffset.UtcNow));

        var result = await client.CallToolAsync("get_job_history", new Dictionary<string, object?>
        {
            ["job_id"] = id.ToString(),
        });

        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.Empty(structured.GetProperty("transitions").EnumerateArray());
        Assert.Equal("Off", structured.GetProperty("historyPolicy").GetString());
        Assert.Contains("recording is turned off", structured.GetProperty("historyNote").GetString());
    }

    [Fact]
    public async Task GetJobDependencies_ReturnsBothSidesOfTheEdges()
    {
        await using var server = await McpTestServer.StartAsync();
        var parent = await server.SeedJobAsync("critical");
        var child = Guid.NewGuid();
        Assert.Equal(EnqueueResult.Ok, await server.Store.EnqueueAsync(
            new NewJob(child, "test-job", "{}"u8.ToArray(), "critical", DateTimeOffset.UtcNow)
            {
                Parents = [parent],
            },
            now: DateTimeOffset.UtcNow));

        var parentEdges = (await server.Client.CallToolAsync("get_job_dependencies", new Dictionary<string, object?>
        {
            ["job_id"] = parent.ToString(),
        })).StructuredContent!.Value;
        Assert.Empty(parentEdges.GetProperty("gatingParents").EnumerateArray());
        Assert.Equal(child, Assert.Single(parentEdges.GetProperty("children").EnumerateArray()).GetGuid());

        var childEdges = (await server.Client.CallToolAsync("get_job_dependencies", new Dictionary<string, object?>
        {
            ["job_id"] = child.ToString(),
        })).StructuredContent!.Value;
        Assert.Equal(parent, Assert.Single(childEdges.GetProperty("gatingParents").EnumerateArray()).GetGuid());
        Assert.Empty(childEdges.GetProperty("children").EnumerateArray());

        // Unknown job: both sides empty, not an error.
        var unknown = await server.Client.CallToolAsync("get_job_dependencies", new Dictionary<string, object?>
        {
            ["job_id"] = Guid.NewGuid().ToString(),
        });
        Assert.False(unknown.IsError);
        Assert.Empty(unknown.StructuredContent!.Value.GetProperty("gatingParents").EnumerateArray());
        Assert.Empty(unknown.StructuredContent!.Value.GetProperty("children").EnumerateArray());
    }

    [Fact]
    public async Task DeniedViewGate_CoversTheJobTools()
    {
        await using var server = await McpTestServer.StartAsync(
            mcp => mcp.AuthorizeView = _ => ValueTask.FromResult(false));

        Assert.Empty(await server.Client.ListToolsAsync());
        var result = await server.Client.CallToolAsync("search_jobs");
        Assert.True(result.IsError);
        Assert.Contains("Permission denied", result.Text);
    }

    private static async Task<Guid> SeedTaggedJobAsync(InMemoryJobStore store, JobTags tags)
    {
        var id = Guid.NewGuid();
        var result = await store.EnqueueAsync(
            new NewJob(id, "test-job", "{}"u8.ToArray(), "critical", DateTimeOffset.UtcNow) { Tags = tags },
            now: DateTimeOffset.UtcNow);
        Assert.Equal(EnqueueResult.Ok, result);
        return id;
    }

    private static async Task<Guid[]> SearchByTagsAsync(McpTestServer server, string[] tags)
    {
        var result = await server.Client.CallToolAsync("search_jobs", new Dictionary<string, object?>
        {
            ["tags"] = tags,
        });
        Assert.False(result.IsError);
        return [.. result.StructuredContent!.Value.GetProperty("jobs").EnumerateArray()
            .Select(j => j.GetProperty("jobId").GetGuid())];
    }
}
