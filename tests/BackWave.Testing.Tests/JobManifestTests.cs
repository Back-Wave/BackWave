using BackWave.Jobs;
using BackWave.Testing;

namespace BackWave.Testing.Tests;

public sealed record FirstJob(string Id);

public sealed record SecondJob(string Id);

public class JobManifestTests
{
    private static JobRegistration Registration<TJob>(string wireName) => new()
    {
        WireName = wireName,
        JobType = typeof(TJob),
        Queue = "default",
        Serialize = static _ => [],
        Deserialize = static _ => new object(),
        Execute = static (_, _, _, _) => Task.CompletedTask,
    };

    private static string TempManifestPath()
        => Path.Combine(Path.GetTempPath(), $"backwave-manifest-{Guid.NewGuid():N}.txt");

    [Fact]
    public void FirstRun_WritesTheManifest()
    {
        var path = TempManifestPath();
        var registry = new JobRegistry([Registration<FirstJob>("first-job")]);

        JobManifest.Verify(registry, path);

        Assert.Equal([$"first-job => {typeof(FirstJob).FullName}"], File.ReadAllLines(path));
    }

    [Fact]
    public void AdditiveRegistration_Passes_AndRecordsTheNewEntry()
    {
        var path = TempManifestPath();
        JobManifest.Verify(new JobRegistry([Registration<FirstJob>("first-job")]), path);

        JobManifest.Verify(new JobRegistry(
        [
            Registration<FirstJob>("first-job"),
            Registration<SecondJob>("second-job"),
        ]), path);

        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void RemovedWireName_FailsTheVerification()
    {
        var path = TempManifestPath();
        JobManifest.Verify(new JobRegistry(
        [
            Registration<FirstJob>("first-job"),
            Registration<SecondJob>("second-job"),
        ]), path);

        var exception = Assert.Throws<InvalidOperationException>(
            () => JobManifest.Verify(new JobRegistry([Registration<FirstJob>("first-job")]), path));

        Assert.Contains("second-job", exception.Message);
        Assert.Contains("new Wire Name", exception.Message);
    }

    [Fact]
    public void RenamedWireName_FailsTheVerification()
    {
        var path = TempManifestPath();
        JobManifest.Verify(new JobRegistry([Registration<FirstJob>("first-job")]), path);

        var exception = Assert.Throws<InvalidOperationException>(
            () => JobManifest.Verify(new JobRegistry([Registration<FirstJob>("first-job-v2")]), path));

        Assert.Contains("first-job =>", exception.Message);
    }

    [Fact]
    public void ChangedPayloadType_ForSameWireName_FailsTheVerification()
    {
        var path = TempManifestPath();
        JobManifest.Verify(new JobRegistry([Registration<FirstJob>("first-job")]), path);

        var exception = Assert.Throws<InvalidOperationException>(
            () => JobManifest.Verify(new JobRegistry([Registration<SecondJob>("first-job")]), path));

        Assert.Contains(typeof(FirstJob).FullName!, exception.Message);
    }

    [Fact]
    public void UnchangedRegistry_Passes()
    {
        var path = TempManifestPath();
        var registry = new JobRegistry([Registration<FirstJob>("first-job")]);

        JobManifest.Verify(registry, path);
        JobManifest.Verify(registry, path);

        Assert.Equal([$"first-job => {typeof(FirstJob).FullName}"], File.ReadAllLines(path));
    }
}
