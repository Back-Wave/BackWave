using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BackWave.Jobs;

/// <summary>
/// The <b>Job Output</b> codec: the single point that turns a handler's typed output value into the
/// opaque blob stored on the job row and back again. It uses the <b>same JSON serializer as the
/// payload</b> (a caller-supplied <see cref="JsonTypeInfo{T}"/>, so the path is reflection-free and
/// NativeAOT-safe), which guarantees producer shape equals reader shape: a descendant that pulls the
/// output deserializes the exact bytes the producer wrote. Pure — no IO, no clock, no size bound (the
/// maximum output size is enforced by the store at write time).
/// </summary>
public static class JobOutputCodec
{
    /// <summary>Serializes a handler's output value to the opaque blob the store persists.</summary>
    /// <typeparam name="T">The output value type.</typeparam>
    /// <param name="value">The output value to encode.</param>
    /// <param name="typeInfo">
    /// The source-generated metadata for <typeparamref name="T"/>, keeping serialization
    /// reflection-free.
    /// </param>
    /// <returns>The UTF-8 JSON bytes to persist as the job's output.</returns>
    public static ReadOnlyMemory<byte> Encode<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    /// <summary>Deserializes a stored output blob back to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The output value type the producer wrote.</typeparam>
    /// <param name="output">The stored UTF-8 JSON output bytes.</param>
    /// <param name="typeInfo">
    /// The source-generated metadata for <typeparamref name="T"/>, keeping deserialization
    /// reflection-free.
    /// </param>
    /// <returns>The deserialized output value.</returns>
    /// <exception cref="InvalidOperationException">
    /// The bytes deserialize to null — a producer/reader shape mismatch the caller must learn about
    /// loudly.
    /// </exception>
    public static T Decode<T>(ReadOnlyMemory<byte> output, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(output.Span, typeInfo)
           ?? throw new InvalidOperationException("Job Output deserialized to null.");
}
