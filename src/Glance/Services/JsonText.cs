using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glance.Services;

/// JSON page: strict validation (RFC 8259, no comments or trailing commas) and reformatting that keeps
/// numbers and strings exactly as written.
public static partial class JsonText
{
    /// On an error: the parser's message, and the char index where it stopped, for the caret.
    public record Result(bool Ok, string Message, int ErrorAt = -1);

    public static Result Check(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(false, "");
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            return new(true, r.ValueKind switch
            {
                JsonValueKind.Object => Count("object", r.EnumerateObject().Count(), "key"),
                JsonValueKind.Array => Count("array", r.GetArrayLength(), "item"),
                var k => k.ToString().ToLowerInvariant(),
            });
        }
        catch (JsonException e)
        {
            var line = (int)(e.LineNumber ?? 0);
            var start = LineStart(text, line);
            var at = Advance(text, start, (int)(e.BytePositionInLine ?? 0));
            // The message ends with "LineNumber: 2 | BytePositionInLine: 5." (both from 0); say it the way an editor would.
            var msg = e.Message.Split(" LineNumber:")[0].TrimEnd();
            return new(false, $"Line {line + 1}, column {at - start + 1}: {msg}", at);
        }
    }

    static string Count(string kind, int n, string what) => $"{kind} · {n} {what}{(n == 1 ? "" : "s")}";

    public static string Format(string text, bool indent)
    {
        using var doc = JsonDocument.Parse(text);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
               {
                   Indented = indent, IndentSize = 2,
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keeps é, ő, emoji readable instead of \uXXXX
               }))
            doc.RootElement.WriteTo(w);
        // The encoder escapes every character beyond U+FFFF (emoji) as a surrogate pair; put those back.
        // Only real escapes: an even run of backslashes before one is text, not an escape.
        return EscapedPair().Replace(Encoding.UTF8.GetString(ms.ToArray()),
            m => $"{(char)Convert.ToInt32(m.Groups[1].Value, 16)}{(char)Convert.ToInt32(m.Groups[2].Value, 16)}");
    }

    [GeneratedRegex(@"(?<=(?<!\\)(?:\\\\)*)\\u(D[89AB][0-9A-F]{2})\\u(D[C-F][0-9A-F]{2})")]
    private static partial Regex EscapedPair();

    static int LineStart(string text, int line)
    {
        var i = 0;
        for (var n = 0; n < line && i < text.Length; i++)
            if (text[i] == '\n') n++;
        return i;
    }

    /// The parser counts UTF-8 bytes within the line: step that many bytes' worth of chars from the line start.
    static int Advance(string text, int i, int bytes)
    {
        while (bytes > 0 && i < text.Length)
        {
            var n = char.IsSurrogatePair(text, i) ? 2 : 1;
            bytes -= Encoding.UTF8.GetByteCount(text.AsSpan(i, n));
            i += n;
        }
        return i;
    }
}
