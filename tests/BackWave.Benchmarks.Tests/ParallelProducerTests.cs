using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// The producer fan-out partitioning (bench-0137 fix): the parallel producer must split the stream so every
/// job is enqueued exactly once, no matter how unevenly the count divides across producers — otherwise a
/// sustained run would under- or over-count jobs and silently corrupt throughput.
/// </summary>
public sealed class ParallelProducerTests
{
    [Theory]
    [InlineData(10000, 1)]
    [InlineData(10000, 4)]
    [InlineData(10000, 8)]
    [InlineData(10003, 8)] // remainder spread across the first ranges
    [InlineData(5, 8)]     // more producers than jobs
    [InlineData(0, 4)]     // empty stream
    public void Partition_covers_every_job_exactly_once_without_overlap(int total, int parts)
    {
        var ranges = ParallelProducer.Partition(total, parts);

        Assert.Equal(parts, ranges.Length);
        // Contiguous, gap-free, no overlap: each range starts where the previous ended.
        var offset = 0;
        foreach (var (rangeOffset, count) in ranges)
        {
            Assert.Equal(offset, rangeOffset);
            offset += count;
        }

        // Every job covered exactly once.
        Assert.Equal(total, ranges.Sum(r => r.Count));
        // As even as possible: the busiest and lightest producer differ by at most one job.
        if (total > 0)
        {
            Assert.True(ranges.Max(r => r.Count) - ranges.Min(r => r.Count) <= 1);
        }
    }

    [Fact]
    public void Partition_clamps_parts_to_at_least_one()
    {
        var ranges = ParallelProducer.Partition(100, 0);

        Assert.Single(ranges);
        Assert.Equal((0, 100), ranges[0]);
    }
}
