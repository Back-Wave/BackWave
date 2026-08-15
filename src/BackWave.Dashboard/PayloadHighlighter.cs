using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace BackWave.Dashboard;

/// <summary>
/// A display-only nicety for the Payload card: when a payload's best-effort UTF-8 text happens
/// to parse as JSON, pretty-print it and tokenise it into CSS-classed spans (<c>.bw-json__*</c>)
/// for syntax highlighting. This does NOT change BackWave's stance that the payload is opaque
/// bytes it never parses for execution — it is purely how the dashboard renders it. When the
/// text is not valid JSON, <see cref="Highlight"/> returns <c>null</c> and the caller falls back
/// to the raw text verbatim. Highlighting runs server-side (the dashboard ships no client JS for
/// this) and emits HTML-encoded content, so payload bytes can never inject markup.
/// </summary>
internal static class PayloadHighlighter
{
    private const string Indent = "  ";

    /// <summary>Pretty, highlighted JSON, or <c>null</c> when <paramref name="text"/> isn't JSON.</summary>
    public static MarkupString? Highlight(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var sb = new StringBuilder();
            Write(sb, doc.RootElement, 0);
            return new MarkupString(sb.ToString());
        }
    }

    private static void Write(StringBuilder sb, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(sb, element, depth);
                break;
            case JsonValueKind.Array:
                WriteArray(sb, element, depth);
                break;
            case JsonValueKind.String:
                Token(sb, "str", element.GetRawText());
                break;
            case JsonValueKind.Number:
                Token(sb, "num", element.GetRawText());
                break;
            case JsonValueKind.True or JsonValueKind.False:
                Token(sb, "bool", element.GetRawText());
                break;
            default: // Null (and the never-rendered Undefined)
                Token(sb, "null", "null");
                break;
        }
    }

    private static void WriteObject(StringBuilder sb, JsonElement element, int depth)
    {
        Punc(sb, "{");
        var any = false;
        foreach (var prop in element.EnumerateObject())
        {
            if (any) Punc(sb, ",");
            any = true;
            NewLine(sb, depth + 1);
            // Re-encode the key as a quoted JSON string literal. JsonEncodedText uses the same default
            // encoder as JsonSerializer but is reflection- and dynamic-code-free, so it stays AOT-safe.
            Token(sb, "key", $"\"{JsonEncodedText.Encode(prop.Name)}\"");
            Punc(sb, ": ");
            Write(sb, prop.Value, depth + 1);
        }
        if (any) NewLine(sb, depth);
        Punc(sb, "}");
    }

    private static void WriteArray(StringBuilder sb, JsonElement element, int depth)
    {
        Punc(sb, "[");
        var any = false;
        foreach (var item in element.EnumerateArray())
        {
            if (any) Punc(sb, ",");
            any = true;
            NewLine(sb, depth + 1);
            Write(sb, item, depth + 1);
        }
        if (any) NewLine(sb, depth);
        Punc(sb, "]");
    }

    private static void NewLine(StringBuilder sb, int depth)
    {
        sb.Append('\n');
        for (var i = 0; i < depth; i++) sb.Append(Indent);
    }

    private static void Token(StringBuilder sb, string cls, string rawText) => sb
        .Append("<span class=\"bw-json__").Append(cls).Append("\">")
        .Append(WebUtility.HtmlEncode(rawText)).Append("</span>");

    private static void Punc(StringBuilder sb, string text) => sb
        .Append("<span class=\"bw-json__punc\">").Append(WebUtility.HtmlEncode(text)).Append("</span>");
}
