namespace BackWave.Pro;

/// <summary>
/// A reference to one step already added to a workflow, for a fan-in <c>after</c> list: the step's .NET
/// type plus an optional disambiguation <see cref="Name"/>. A plain <see cref="System.Type"/> converts to a
/// ref implicitly, so <c>after: [typeof(ChargeStep)]</c> keeps working; supply a <see cref="Name"/> only to
/// pick one of several steps of the same type that were disambiguated at add time.
/// </summary>
public readonly struct WorkflowStepRef
{
    /// <summary>The referenced step's .NET type.</summary>
    public Type StepType { get; }

    /// <summary>The referenced step's disambiguation name, or <see langword="null"/> to match the sole step of that type.</summary>
    public string? Name { get; }

    /// <summary>
    /// Creates a reference to a step by its type and an optional disambiguation name.
    /// </summary>
    /// <param name="stepType">The referenced step's .NET type.</param>
    /// <param name="name">The step's disambiguation name, or <see langword="null"/> to match the sole step of that type.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stepType"/> is <see langword="null"/>.</exception>
    public WorkflowStepRef(Type stepType, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(stepType);
        StepType = stepType;
        Name = name;
    }

    /// <summary>
    /// Converts a plain step type to a name-less reference, so a fan-in list can be written as
    /// <c>[typeof(StepA), typeof(StepB)]</c> without wrapping each type. The named alternate is the
    /// <see cref="WorkflowStepRef(Type, string?)"/> constructor.
    /// </summary>
    /// <param name="stepType">The referenced step's .NET type.</param>
    /// <returns>A reference to the sole step of that type.</returns>
    public static implicit operator WorkflowStepRef(Type stepType) => new(stepType);
}
