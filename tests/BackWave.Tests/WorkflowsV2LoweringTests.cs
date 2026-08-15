using System.Text.Json;
using BackWave.Jobs;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

/// <summary>
/// Workflows v2 lowering seam (issue 0263, ADR 0047 amendment): the reserved-namespace payload envelope. A
/// member with a parent carries its parent-step wire names under the reserved after-key so a flat workflow
/// trace can reconstruct the DAG edges, so only a seedless, parentless member lowers byte-for-byte identical
/// to a standalone enqueue of the same step. Reuses the v2 step types registered elsewhere in the suite.
/// </summary>
public class LoweringAfterKeyTests
{
    private const string AfterKey = "$backwave.workflowAfter";

    private static BackWaveHarness NewHarness()
    {
        var services = new ServiceCollection()
            .AddSingleton<V2Recorder>()
            .AddTransient<IJobHandler<ChargeStep>, ChargeStepHandler>()
            .AddTransient<IJobHandler<ReceiptStep>, ReceiptStepHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<ChargeStep, ChargeStepHandler>("v2-charge", WorkflowsV2JsonContext.Default.ChargeStep),
            JobRegistration.Create<ReceiptStep, ReceiptStepHandler>("v2-receipt", WorkflowsV2JsonContext.Default.ReceiptStep),
        ]);
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }

    // A seedless workflow whose second step is parented on the first: the parentless root stays byte-identical
    // to a standalone enqueue, while the parented child carries the reserved after-key naming its parent - so
    // the byte-identical lowering claim holds only for a member that is both seedless and parentless.
    [Fact]
    public void Lowering_ParentedSeedlessMember_CarriesTheAfterKey_WhileTheParentlessRootStaysByteIdentical()
    {
        var h = NewHarness();

        var def = h.Client.Workflow()
            .Then(new ChargeStep("o1"))     // root: seedless, parentless
            .Then(new ReceiptStep("o1"))    // parented on charge
            .Build();

        var byWire = def.Members.ToDictionary(m => m.WireName);
        var root = byWire["v2-charge"];
        var child = byWire["v2-receipt"];

        // The parentless root is byte-for-byte identical to a standalone enqueue and carries no after-key.
        var rootStandalone = JsonSerializer.SerializeToUtf8Bytes(new ChargeStep("o1"), WorkflowsV2JsonContext.Default.ChargeStep);
        Assert.Equal(rootStandalone, root.Payload.ToArray());
        using (var rootDoc = JsonDocument.Parse(root.Payload))
        {
            Assert.False(rootDoc.RootElement.TryGetProperty(AfterKey, out _));
        }

        // The parented child is NOT byte-identical: it carries the reserved after-key with its parent's wire name.
        var childStandalone = JsonSerializer.SerializeToUtf8Bytes(new ReceiptStep("o1"), WorkflowsV2JsonContext.Default.ReceiptStep);
        Assert.NotEqual(childStandalone, child.Payload.ToArray());
        using (var childDoc = JsonDocument.Parse(child.Payload))
        {
            Assert.True(childDoc.RootElement.TryGetProperty(AfterKey, out var after));
            Assert.Equal(JsonValueKind.Array, after.ValueKind);
            Assert.Contains(after.EnumerateArray(), e => e.GetString() == "v2-charge");
        }

        // The after-key is an unknown property the step decoder skips, so the child still deserializes cleanly.
        var childStep = JsonSerializer.Deserialize(child.Payload.Span, WorkflowsV2JsonContext.Default.ReceiptStep);
        Assert.Equal("o1", childStep!.Note);
    }
}
