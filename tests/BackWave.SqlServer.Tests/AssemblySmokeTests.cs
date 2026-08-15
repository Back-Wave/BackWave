namespace BackWave.SqlServer.Tests;

public class AssemblySmokeTests
{
    [Fact]
    public void PackageAssemblyLoads()
    {
        Assert.NotNull(typeof(BackWave.SqlServer.Tests.AssemblySmokeTests).Assembly);
    }
}
