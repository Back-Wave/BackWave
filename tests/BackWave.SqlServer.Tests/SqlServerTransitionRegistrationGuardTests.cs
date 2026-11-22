using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace BackWave.SqlServer.Tests;

public sealed class SqlServerTransitionRegistrationGuardTests
{
    private static readonly Regex TransitionInsert = new(
        @"\b(INSERT|MERGE)\s+(INTO\s+)?backwave\.job_transitions\b", RegexOptions.IgnoreCase);

    [Fact]
    public void EveryTransitionLogInsert_StartsWithTheCommitHorizonRegistration()
    {
        var registration = SqlServerJobStore.RegisterTransitionWriter.Trim();
        var inserts = 0;
        foreach (var literal in StringLiterals(typeof(SqlServerJobStore).Assembly))
        {
            foreach (Match insert in TransitionInsert.Matches(literal))
            {
                inserts++;
                Assert.True(
                    literal[..insert.Index].TrimEnd().EndsWith(registration, StringComparison.Ordinal),
                    $"This Transition Log insert does not start with RegisterTransitionWriter:{Environment.NewLine}{literal}");
            }
        }

        Assert.True(inserts >= 3, $"The scan found {inserts} Transition Log insert texts, so it read the wrong assembly.");
    }

    // The #US heap holds every string literal the compiled code loads, with constants already folded in.
    private static List<string> StringLiterals(Assembly assembly)
    {
        using var pe = new PEReader(File.OpenRead(assembly.Location));
        var metadata = pe.GetMetadataReader();
        var literals = new List<string>();
        for (var handle = MetadataTokens.UserStringHandle(1); !handle.IsNil; handle = metadata.GetNextHandle(handle))
        {
            literals.Add(metadata.GetUserString(handle));
        }
        return literals;
    }
}
