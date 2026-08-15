using BackWave.Monitor;
using BackWave.Operations;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Dashboard.Tests;

/// <summary>
/// The dashboard extension seam (issue 0154): a separately-installed package contributes nav entries
/// and a top-of-content banner through <see cref="IDashboardExtension"/>, and the base dashboard
/// renders identically when none is registered. These tests register a stub extension directly, so
/// they exercise the seam without any Pro package.
/// </summary>
public sealed class ExtensionPointTests
{
    // A stub extension whose contributions are fixed at construction, so a test controls exactly
    // what the seam folds in.
    private sealed class StubExtension(IEnumerable<DashboardNavEntry> nav, string? banner) : IDashboardExtension
    {
        public IEnumerable<DashboardNavEntry> NavEntries() => nav;
        public string? Banner(string basePath) => banner;
    }

    private static async Task<(WebApplication App, HttpClient Http)> StartAsync(
        IDashboardExtension? extension, BackWaveDashboardOptions? options = null)
    {
        var store = new InMemoryJobStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new BackWaveMonitor(store, registry: null));
        builder.Services.AddSingleton(new BackWaveOperator(store));
        builder.Services.AddAntiforgery();
        if (extension is not null)
        {
            builder.Services.AddSingleton(extension);
        }

        var app = builder.Build();
        app.UseBackWaveDashboard("/backwave", options);
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task NavEntries_ContributedByAnExtension_AppearInTheSidebar()
    {
        var (app, http) = await StartAsync(new StubExtension(
            [new DashboardNavEntry("metrics", "Metrics", "/metrics")], banner: null));
        await using (app)
        {
            var html = await http.GetStringAsync("/backwave/");
            Assert.Contains("Metrics", html);
            // Linked under the dashboard's own mount path, so it resolves wherever the host mounts it.
            Assert.Contains("href=\"/backwave/metrics\"", html);
        }
    }

    [Fact]
    public async Task Banner_ContributedByAnExtension_RendersAtopThePageContent()
    {
        const string marker = "<div id=\"stub-banner\">heads up</div>";
        var (app, http) = await StartAsync(new StubExtension([], banner: marker));
        await using (app)
        {
            var html = await http.GetStringAsync("/backwave/");
            Assert.Contains(marker, html);
            // Chrome, not page content: it sits outside the live region the SSE client swaps.
            Assert.True(
                html.IndexOf(marker, StringComparison.Ordinal) < html.IndexOf("id=\"bw-live\"", StringComparison.Ordinal),
                "The banner should render before the #bw-live region.");
        }
    }

    [Fact]
    public async Task NoExtensionRegistered_RendersIdenticallyToTheBaseDashboard()
    {
        // The whole point of the seam: with nothing registered, every byte matches the dashboard as
        // it rendered before the seam existed. Compare a fully un-extended render against one taken
        // through the same path with no extension in the container.
        var (appA, httpA) = await StartAsync(extension: null);
        string baseline;
        await using (appA)
        {
            baseline = await httpA.GetStringAsync("/backwave/");
        }

        var (appB, httpB) = await StartAsync(extension: null);
        await using (appB)
        {
            var again = await httpB.GetStringAsync("/backwave/");
            Assert.Equal(baseline, again);
        }

        // And the un-extended render carries none of the seam's hooks: no extra nav, no banner.
        Assert.DoesNotContain("stub-banner", baseline);
    }

    // A stub that contributes one LIVE page route, counts how many times its loader runs, and starts
    // returning null once the loader has been called more than nullAfterCall times — i.e. its resource
    // "disappears" mid-view. Component is a built-in live page so it renders through the real machinery.
    private sealed class LivePageExtension(string template, int nullAfterCall = int.MaxValue) : IDashboardExtension
    {
        private int _loads;
        public int Loads => Volatile.Read(ref _loads);

        public IEnumerable<DashboardPageRoute> PageRoutes() =>
        [
            new DashboardPageRoute
            {
                Template = template,
                Component = typeof(Components.Pages.Executing),
                Live = true,
                LoadAsync = _ =>
                {
                    var n = Interlocked.Increment(ref _loads);
                    Dictionary<string, object?>? parameters = n > nullAfterCall
                        ? null
                        : new Dictionary<string, object?>
                        {
                            ["BasePath"] = "/backwave",
                            ["Jobs"] = Array.Empty<JobSnapshot>(),
                            ["Now"] = default(DateTimeOffset),
                        };
                    return Task.FromResult(parameters);
                },
            },
        ];
    }

    [Fact]
    public async Task ContributedLivePage_InitialRender_LoadsItsDataOnlyOnce()
    {
        // The initial GET probes the loader once and reuses that probe as the first frame — it must not
        // load a second time to render the same page.
        var extension = new LivePageExtension("live-thing");
        var (app, http) = await StartAsync(extension);
        await using (app)
        {
            var html = await http.GetStringAsync("/backwave/live-thing");
            Assert.Contains("Executing now", html); // the contributed live page rendered
            Assert.Contains("new EventSource", html); // and it is wired live
            Assert.Equal(1, extension.Loads); // probe reused as the seed — not loaded twice
        }
    }

    [Fact]
    public async Task ContributedLivePage_WhoseResourceVanishesMidStream_SwapsToNotFound_NotStaleData()
    {
        // The resource exists at probe time (so the stream starts) but the next refresh tick returns
        // null. The live region must swap to Not Found rather than freeze on the first snapshot.
        var extension = new LivePageExtension("live-thing", nullAfterCall: 1);
        var (app, http) = await StartAsync(
            extension, new BackWaveDashboardOptions { LiveRefreshInterval = TimeSpan.FromMilliseconds(50) });
        await using (app)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await http.GetAsync(
                "/backwave/live-thing?live=1", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
            var sawPage = false;
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
            {
                if (line.Contains("Executing now"))
                {
                    sawPage = true; // the live page streamed first, from the probe snapshot
                }
                if (line.Contains("No such page or job"))
                {
                    Assert.True(sawPage, "the page should stream before the resource disappears");
                    return; // success: the vanished resource swapped the region to Not Found
                }
            }
            Assert.Fail("the stream ended without swapping to the Not Found fragment");
        }
    }

    [Fact]
    public async Task AnExtensionReturningNoBannerAndNoNav_LeavesTheRenderUnchanged()
    {
        // A registered-but-silent extension (null banner, empty nav) must not perturb the output, so
        // an extension that conditionally shows nothing is invisible — exactly how the Pro banner
        // behaves under a valid license.
        var (appBase, httpBase) = await StartAsync(extension: null);
        string baseline;
        await using (appBase)
        {
            baseline = await httpBase.GetStringAsync("/backwave/");
        }

        var (app, http) = await StartAsync(new StubExtension([], banner: null));
        await using (app)
        {
            var withSilentExtension = await http.GetStringAsync("/backwave/");
            Assert.Equal(baseline, withSilentExtension);
        }
    }
}
