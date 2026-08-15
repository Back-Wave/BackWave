using System.Text.Json;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>Structured-content assertions the plain xUnit asserts don't cover.</summary>
internal static class AssertJson
{
    /// <summary>
    /// Asserts a property carries no value: either omitted (the MCP serializer skips nulls) or an
    /// explicit JSON null. Use it wherever a nullable field of a structured result is expected
    /// empty, so the assertion doesn't couple to the serializer's null handling.
    /// </summary>
    public static void NullOrAbsent(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value))
        {
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
    }
}
