using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BackWave.Benchmarks.Diagnostics;

/// <summary>
/// A diagnostic span recorder for the in-process targets. With <see cref="EnvVar"/> set to a file path, it
/// listens to the Npgsql statement spans and the BackWave Postgres adapter's store-operation spans, and writes
/// one CSV row per span when the process ends.
/// </summary>
internal sealed class DbTrace : IDisposable
{
    /// <summary>Environment variable holding the CSV output path; unset means off.</summary>
    public const string EnvVar = "BACKWAVE_BENCH_DB_TRACE";

    private static readonly HashSet<string> Sources = ["Npgsql", "BackWave.Postgres"];

    private readonly string _path;
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<string> _rows = new();

    private DbTrace(string path)
    {
        _path = path;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => Sources.Contains(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Record,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Starts recording when <see cref="EnvVar"/> is set; returns null otherwise.</summary>
    public static DbTrace? StartFromEnvironment()
    {
        var path = System.Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        Console.Error.WriteLine($"DB TRACE ENGAGED ({path}): a DIAGNOSTIC run, never reported.");
        return new DbTrace(path);
    }

    private void Record(Activity activity)
    {
        var tags = new StringBuilder();
        foreach (var (key, value) in activity.TagObjects)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            text = text.Replace('\n', ' ').Replace('\r', ' ').Replace('"', '\'');
            if (text.Length > 160)
            {
                text = text[..160];
            }

            tags.Append(key).Append('=').Append(text).Append(" | ");
        }

        _rows.Enqueue(string.Join(',',
            activity.StartTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            activity.Duration.TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture),
            activity.Source.Name,
            activity.DisplayName.Replace(',', ' '),
            System.Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture),
            $"\"{tags}\""));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _listener.Dispose();
        File.WriteAllLines(_path, ["start_ticks,duration_ms,source,name,thread,tags", .. _rows]);
        Console.Error.WriteLine($"DB TRACE wrote {_rows.Count} spans to {_path}");
    }
}
