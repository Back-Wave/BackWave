namespace BackWave.Oracle.Tests;

public class AssemblySmokeTests
{
    [Fact]
    public void PackageAssemblyLoads()
    {
        Assert.NotNull(typeof(BackWave.Oracle.Tests.AssemblySmokeTests).Assembly);
    }
}
