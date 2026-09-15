namespace BackWave.Storage;

/// <summary>Collapses a parent list to its distinct set once, at the store boundary, so every step past it can trust the list.</summary>
internal static class ParentSet
{
    internal static NewJob WithDistinctParents(this NewJob job)
        => job.Parents.Count > 1 ? job with { Parents = job.Parents.Distinct().ToArray() } : job;

    internal static WorkflowDefinition WithDistinctParents(this WorkflowDefinition workflow)
        => workflow with { Members = [.. workflow.Members.Select(WithDistinctParents)] };
}
