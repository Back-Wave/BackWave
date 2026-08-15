using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Pro.Licensing;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// Pro soft-fail proven at the MCP surface (issue 0228): with the Pro license missing, malformed,
/// or out-of-term, all three workflow tools stay listed and fully functional, and no license text
/// appears anywhere in the protocol — not in <c>tools/list</c> and not in any tool result. The
/// license nag stays on operator-facing surfaces (the startup log warning and the dashboard
/// banner), never in an LLM's context.
/// </summary>
public sealed class WorkflowLicenseSoftFailTests
{
    /// <summary>The three non-Valid license states, each producing the exact state it claims.</summary>
    public static TheoryData<string> NonValidLicenseStates => new("Missing", "Malformed", "OutOfTerm");

    [Theory]
    [MemberData(nameof(NonValidLicenseStates))]
    public async Task Unlicensed_WorkflowToolsStayListedAndFunctional_WithNoLicenseTextInAnyResult(
        string stateName)
    {
        var expectedState = Enum.Parse<LicenseState>(stateName);
        var (app, store, client) = await StartUnlicensedHostAsync(expectedState);
        await using (app)
        {
            // The host really is in the claimed license state — the premise of the proof.
            Assert.Equal(expectedState, app.Services.GetRequiredService<ProLicense>().State);

            var (workflowId, rootId, _) = await WorkflowToolsTests.SeedFanOutWorkflowAsync(store, "soft-fail");

            // Listed: all three workflow tools are advertised (cancel_workflow because the cancel
            // gate is granted — license state plays no part). And the raw listing carries no
            // license text anywhere.
            var listing = await client.SendAsync("tools/list");
            var toolNames = listing.GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.Contains("list_workflows", toolNames);
            Assert.Contains("get_workflow", toolNames);
            Assert.Contains("cancel_workflow", toolNames);
            AssertNoLicenseText(listing.GetRawText());

            // Fully functional: every workflow tool answers exactly as it would licensed, and no
            // result smuggles in a nag.
            var list = await client.CallToolAsync("list_workflows");
            Assert.False(list.IsError);
            var row = Assert.Single(list.StructuredContent!.Value.GetProperty("workflows").EnumerateArray());
            Assert.Equal(workflowId, Guid.Parse(row.GetProperty("workflowId").GetString()!));
            AssertNoLicenseText(list.Raw.GetRawText());

            var get = await client.CallToolAsync("get_workflow", new { workflow_id = workflowId.ToString() });
            Assert.False(get.IsError);
            Assert.True(get.StructuredContent!.Value.GetProperty("found").GetBoolean());
            Assert.Equal(
                3, get.StructuredContent!.Value.GetProperty("workflow")
                    .GetProperty("members").GetArrayLength());
            AssertNoLicenseText(get.Raw.GetRawText());

            var cancel = await client.CallToolAsync(
                "cancel_workflow", new { workflow_id = workflowId.ToString() });
            Assert.False(cancel.IsError);
            Assert.True(cancel.StructuredContent!.Value.GetProperty("found").GetBoolean());
            Assert.Equal(1, cancel.StructuredContent!.Value.GetProperty("cancelledImmediately").GetInt32());
            AssertNoLicenseText(cancel.Raw.GetRawText());

            // The cancel really landed, audit stamped as ever — soft-fail means full function.
            Assert.Equal(
                BackWave.Storage.JobState.Cancelled,
                (await store.GetJobAsync(rootId))!.State);
            var audit = Assert.Single(await store.ListAuditRecordsAsync(rootId.ToString()));
            Assert.Equal("mcp", audit.Actor);
        }
    }

    // "license" (case-insensitive) also catches "unlicensed", "license key", "licensee" — every
    // phrase the operator-facing nags use.
    private static void AssertNoLicenseText(string wireJson)
        => Assert.DoesNotContain("licen", wireJson, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The full consumer wiring — <c>AddBackWave</c> + <c>bw.AddMcp()</c> (cancel granted) with
    /// <c>AddBackWavePro</c> alongside, exactly like a Pro host — put into the requested non-Valid
    /// license state.
    /// </summary>
    private static async Task<(WebApplication App, InMemoryJobStore Store, McpTestClient Client)>
        StartUnlicensedHostAsync(LicenseState state)
    {
        var store = new InMemoryJobStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBackWave(bw => bw
            .UseStore(store)
            .UseRegistry(new JobRegistry([]))
            .AddMcp(mcp => mcp.AuthorizeCancel = _ => ValueTask.FromResult(true)));

        switch (state)
        {
            case LicenseState.Missing:
                builder.Services.AddBackWavePro(license: null);
                break;
            case LicenseState.Malformed:
                builder.Services.AddBackWavePro("this-is-not-a-license");
                break;
            case LicenseState.OutOfTerm:
                // A genuine test-signed license whose subscription term has ended. The production
                // signing key lives outside the repo, so this is minted with the ephemeral test
                // keypair and injected directly; AddBackWavePro's TryAdd respects it.
                builder.Services.AddSingleton(OutOfTermDevLicense());
                builder.Services.AddBackWavePro();
                break;
        }

        var app = builder.Build();
        app.UseBackWaveProMcp();
        await app.StartAsync();
        return (app, store, new McpTestClient(app.GetTestClient()));
    }

    // Issue a real license with the ephemeral test keypair, then evaluate it one day past its term.
    private static ProLicense OutOfTermDevLicense()
    {
        using var signingKey = TestKeys.SigningKey();
        var license = LicenseCrypto.Issue(
            new LicenseClaims
            {
                Licensee = "Acme Inc",
                Issued = new DateOnly(2025, 1, 1),
                Term = new DateOnly(2026, 1, 1),
                Band = "growth",
            },
            signingKey);
        using var verificationKey = TestKeys.VerificationKey();
        return ProLicense.Evaluate(license, verificationKey, asOf: new DateOnly(2026, 1, 2));
    }
}
