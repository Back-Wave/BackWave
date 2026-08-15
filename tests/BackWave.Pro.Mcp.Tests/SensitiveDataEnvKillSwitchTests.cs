namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// Lock 3 of the sensitive-data triple lock: the <c>BACKWAVE_MCP_DISABLE_SENSITIVE_DATA</c>
/// environment kill-switch, exercised end-to-end through the mounted endpoint. The variable is
/// process-wide, so every test touching it shares one xUnit collection (no parallel interference).
/// Truthy parsing must match the dashboard's: <c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>
/// (case-insensitive, trimmed) kill; anything else leaves exposure on.
/// </summary>
[Collection(SensitiveDataEnvCollection.Name)]
public sealed class SensitiveDataEnvKillSwitchTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData(" on ", true)] // trimmed, like the dashboard's parsing
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public async Task EnvKillSwitch_HidesAndErrors_WithDashboardTruthyParsing(string value, bool killed)
    {
        // Permission granted and ExposeSensitiveData left true: only the env lock is in play.
        await using var server = await McpTestServer.StartAsync(
            mcp => mcp.AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true));
        var jobId = await server.SeedJobAsync("critical");

        try
        {
            Environment.SetEnvironmentVariable(BackWaveProMcpOptions.DisableSensitiveDataEnvVar, value);

            var tools = await server.Client.ListToolsAsync();
            var call = await server.Client.CallToolAsync(
                "get_job_payload", new Dictionary<string, object?> { ["job_id"] = jobId });

            if (killed)
            {
                Assert.DoesNotContain(tools, t => t.Name == "get_job_payload");
                Assert.DoesNotContain(tools, t => t.Name == "get_job_output");
                Assert.True(call.IsError);
                Assert.Contains("BACKWAVE_MCP_DISABLE_SENSITIVE_DATA", call.Text);
            }
            else
            {
                Assert.Contains(tools, t => t.Name == "get_job_payload");
                Assert.Contains(tools, t => t.Name == "get_job_output");
                Assert.False(call.IsError);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackWaveProMcpOptions.DisableSensitiveDataEnvVar, null);
        }
    }

    [Fact]
    public async Task DashboardNamedVariable_DoesNotKillTheMcpSurface()
    {
        // Per-surface switches (mcp-0004): the MCP surface honors only its own variable.
        await using var server = await McpTestServer.StartAsync(
            mcp => mcp.AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true));

        try
        {
            Environment.SetEnvironmentVariable("BACKWAVE_DASHBOARD_DISABLE_SENSITIVE_DATA", "1");
            Assert.Contains(await server.Client.ListToolsAsync(), t => t.Name == "get_job_payload");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKWAVE_DASHBOARD_DISABLE_SENSITIVE_DATA", null);
        }
    }
}

/// <summary>
/// The xUnit collection serializing every test that mutates the process-wide sensitive-data
/// environment variables.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SensitiveDataEnvCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "BackWave MCP sensitive-data env";
}
