using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// The agreed defaults (issue 0224 / mcp-0004): view allows, every write and the sensitive-data
/// read deny, exposure is on but killable by the MCP-named environment variable, and the actor
/// falls back to "mcp" when the request carries no identity.
/// </summary>
[Collection(SensitiveDataEnvCollection.Name)] // shares the process-wide sensitive-data env vars with the endpoint tests
public sealed class BackWaveProMcpOptionsTests
{
    private static readonly DefaultHttpContext Anonymous = new();

    [Fact]
    public async Task Defaults_ViewAllows_EverythingElseDenies()
    {
        var options = new BackWaveProMcpOptions();

        Assert.True(await options.AuthorizeView(Anonymous));
        Assert.False(await options.AuthorizeViewSensitiveData(Anonymous));
        Assert.False(await options.AuthorizeRequeue(Anonymous));
        Assert.False(await options.AuthorizeCancel(Anonymous));
        Assert.False(await options.AuthorizePauseQueue(Anonymous));
        Assert.False(await options.AuthorizeTriggerSchedule(Anonymous));
        Assert.False(await options.AuthorizeSetConcurrencyLimit(Anonymous));
    }

    [Fact]
    public void ResolveActor_DefaultsToIdentityName_FallingBackToMcp()
    {
        var options = new BackWaveProMcpOptions();

        Assert.Equal("mcp", options.ResolveActor(Anonymous));

        var authenticated = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "ops@example.com")], authenticationType: "test")),
        };
        Assert.Equal("ops@example.com", options.ResolveActor(authenticated));
    }

    [Fact]
    public void SensitiveDataExposure_DefaultsOn_HostFlagTurnsItOff()
    {
        Assert.True(new BackWaveProMcpOptions().SensitiveDataExposureEnabled);
        Assert.False(new BackWaveProMcpOptions { ExposeSensitiveData = false }.SensitiveDataExposureEnabled);
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("true", false)]
    [InlineData("TRUE", false)]
    [InlineData("yes", false)]
    [InlineData("on", false)]
    [InlineData(" on ", false)] // trimmed
    [InlineData("0", true)]
    [InlineData("false", true)]
    [InlineData("", true)]
    public void SensitiveDataExposure_HonorsTheMcpEnvKillSwitch(string value, bool expectedEnabled)
    {
        var options = new BackWaveProMcpOptions(); // ExposeSensitiveData = true
        try
        {
            Environment.SetEnvironmentVariable(BackWaveProMcpOptions.DisableSensitiveDataEnvVar, value);
            Assert.Equal(expectedEnabled, options.SensitiveDataExposureEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackWaveProMcpOptions.DisableSensitiveDataEnvVar, null);
        }
    }

    [Fact]
    public void KillSwitch_IsMcpNamed_NotTheDashboardVariable()
    {
        // Per-surface switches: the MCP surface must not honor the dashboard-named variable.
        var options = new BackWaveProMcpOptions();
        try
        {
            Environment.SetEnvironmentVariable("BACKWAVE_DASHBOARD_DISABLE_SENSITIVE_DATA", "1");
            Assert.True(options.SensitiveDataExposureEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKWAVE_DASHBOARD_DISABLE_SENSITIVE_DATA", null);
        }
    }
}
