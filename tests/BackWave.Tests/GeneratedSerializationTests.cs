using System.Text;
using BackWave.Jobs;

namespace BackWave.Tests;

/// <summary>The generated wire format at runtime: round-trips and tolerant decoding.</summary>
public class GeneratedSerializationTests
{
    private static JobRegistration Registration()
    {
        Assert.True(Generated.BackWaveJobs.CreateRegistry()
            .TryGetByWireName("send-welcome-email", out var registration));
        return registration;
    }

    [Fact]
    public void GeneratedWireFormat_RoundTrips()
    {
        var registration = Registration();

        var payload = registration.Serialize(new SendWelcomeEmail("ada@example.test"));
        var decoded = (SendWelcomeEmail)registration.Deserialize(payload);

        Assert.Equal("ada@example.test", decoded.Email);
        Assert.Equal("""{"Email":"ada@example.test"}""", Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void GeneratedDecode_SkipsUnknownProperties_AndDefaultsMissingOnes()
    {
        var registration = Registration();

        // An old or newer deploy's payload: extra property, nested unknown structure.
        var decoded = (SendWelcomeEmail)registration.Deserialize(
            Encoding.UTF8.GetBytes("""{"Legacy":{"deep":[1,2]},"Email":"grace@example.test","Extra":7}"""));
        Assert.Equal("grace@example.test", decoded.Email);

        // Missing property: tolerant default, no throw — drift quarantines only on undecodable JSON.
        var empty = (SendWelcomeEmail)registration.Deserialize(Encoding.UTF8.GetBytes("{}"));
        Assert.Null(empty.Email);
    }
}
