namespace BackWave.Pro;

/// <summary>
/// Thrown when a workflow graph is invalid - it has a dependency cycle, an empty graph, a duplicate
/// step identity, or a step whose own payload declares a property in the reserved namespace BackWave uses
/// to carry workflow metadata. Caught at build time, never in production.
/// </summary>
/// <param name="message">A description of why the workflow graph is invalid.</param>
public sealed class InvalidWorkflowException(string message) : Exception(message);
