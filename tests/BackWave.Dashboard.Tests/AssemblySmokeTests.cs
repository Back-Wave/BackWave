namespace BackWave.Dashboard.Tests;

public class AssemblySmokeTests
{
    [Fact]
    public void PackageAssemblyLoads()
    {
        Assert.NotNull(typeof(BackWave.Dashboard.Tests.AssemblySmokeTests).Assembly);
    }
}
