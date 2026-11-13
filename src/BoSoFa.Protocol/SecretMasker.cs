using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace BoSoFa.Protocol;

/// <summary>
/// Replaces the values of secrets by <see cref="Mask"/> wherever they would be written or shown (docs/secrets.md §5): the
/// outputs of tools, the items of sessions, logs. Besides each value as it is, it masks the forms a value commonly takes in
/// text: escaped in JSON (as System.Text.Json and other serializers write it), URL-encoded, and in Base64, including the
/// start of the Base64 of a longer text that begins with it (<c>echo $TOKEN | base64</c>).
/// </summary>
/// <remarks>
/// A value it was given once stays masked for as long as it lives: a process that took a value may print it after the value
/// was rotated or deleted. A value shorter than <see cref="MinimumLength"/> is not masked, which would hide ordinary words,
/// and its other forms only from <see cref="FormMinimumLength"/> characters on. Masking stops accidents, not an agent that
/// means to leak a value it holds (a substring, <c>rev</c>, another encoding).
/// </remarks>
public sealed partial class SecretMasker
{
    /// <summary>The text that stands for a secret.</summary>
    public const string Mask = "***";

    /// <summary>The length below which a value is not masked.</summary>
    public const int MinimumLength = 4;

    /// <summary>The length below which a value's encoded forms are not masked; they are often shorter or more common.</summary>
    public const int FormMinimumLength = 8;

    static readonly JavaScriptEncoder[] JsonEncoders = [JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping];

    /// <summary>What to look for, longest first so that a value wins over a shorter one it begins with.</summary>
    sealed class Patterns
    {
        public static readonly Patterns Empty = new([]);

        Patterns(string[] all)
        {
            All = all;
            Search = SearchValues.Create(all, StringComparison.Ordinal);
            Longest = all.Length == 0 ? 0 : all[0].Length;
        }

        public string[] All { get; }
        public SearchValues<string> Search { get; }
        public int Longest { get; }

        public static Patterns Of(IEnumerable<string> values) =>
            new(values.SelectMany(FormsOf).Distinct(StringComparer.Ordinal).OrderByDescending(form => form.Length).ThenBy(form => form, StringComparer.Ordinal)
                .ToArray());

        /// <summary>The length of the longest pattern at the start of <paramref name="text"/>, which holds one.</summary>
        public int LengthAt(ReadOnlySpan<char> text)
        {
            foreach (var pattern in All)
                if (text.StartsWith(pattern, StringComparison.Ordinal)) return pattern.Length;
            throw new InvalidOperationException("No pattern at this position.");
        }
    }

    readonly Lock _gate = new();
    readonly HashSet<string> _values = new(StringComparer.Ordinal);
    volatile Patterns _patterns = Patterns.Empty;

    /// <summary>Masks a value from now on, for as long as this masker lives.</summary>
    /// <param name="value">The value; one shorter than <see cref="MinimumLength"/>, or <see langword="null"/>, is ignored.</param>
    public void Retain(string? value) => Retain([value]);

    /// <summary>Masks these values from now on, for as long as this masker lives.</summary>
    public void Retain(IEnumerable<string?> values)
    {
        lock (_gate)
        {
            var added = false;
            foreach (var value in values)
                if (value is { Length: >= MinimumLength }) added |= _values.Add(value);
            if (added) _patterns = Patterns.Of(_values);
        }
    }

    /// <summary>Gets whether there is nothing to mask.</summary>
    public bool IsEmpty => _patterns.All.Length == 0;

    /// <summary>The value and the forms of it that are masked, each at least <see cref="MinimumLength"/> characters long.</summary>
    internal static IEnumerable<string> FormsOf(string value)
    {
        yield return value;
        if (value.Length < FormMinimumLength) yield break;
        foreach (var encoder in JsonEncoders)
        {
            var escaped = encoder.Encode(value);
            yield return escaped;
            yield return LowerHex().Replace(escaped, match => match.Value.ToLowerInvariant()); // +, as some serializers write it
        }
        var uri = Uri.EscapeDataString(value);
        yield return uri;
        yield return LowerHex().Replace(uri, match => match.Value.ToLowerInvariant());
        yield return WebUtility.UrlEncode(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        var full = Convert.ToBase64String(bytes);
        yield return full;
        yield return full.TrimEnd('=');
        yield return Base64Url(full).TrimEnd('=');
        // The Base64 of a text that begins with the value begins with this: it ends where the value's bytes fill whole groups.
        var aligned = bytes.Length / 3 * 3;
        if (aligned * 4 / 3 >= FormMinimumLength)
        {
            var prefix = Convert.ToBase64String(bytes, 0, aligned);
            yield return prefix;
            yield return Base64Url(prefix);
        }
    }

    static string Base64Url(string base64) => base64.Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("(%[0-9A-F]{2}|\\\\u[0-9A-F]{4})")]
    private static partial Regex LowerHex();

    /// <summary>Tells whether a text holds a secret.</summary>
    public bool Contains(string? text)
    {
        var patterns = _patterns;
        return patterns.All.Length > 0 && !string.IsNullOrEmpty(text) && text.AsSpan().IndexOfAny(patterns.Search) >= 0;
    }

    /// <summary>Replaces each secret of a text by <see cref="Mask"/>.</summary>
    /// <returns>The text, the same instance when it holds no secret.</returns>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    public string? MaskText(string? text) => string.IsNullOrEmpty(text) ? text : MaskWith(text, _patterns);

    static string MaskWith(string text, Patterns patterns)
    {
        if (patterns.All.Length == 0) return text;
        var span = text.AsSpan();
        var found = span.IndexOfAny(patterns.Search);
        if (found < 0) return text;
        var builder = new StringBuilder(text.Length);
        var from = 0;
        while (found >= 0)
        {
            var at = from + found;
            builder.Append(span[from..at]).Append(Mask);
            from = at + patterns.LengthAt(span[at..]);
            found = span[from..].IndexOfAny(patterns.Search);
        }
        return builder.Append(span[from..]).ToString();
    }

    /// <summary>Replaces each secret of a text by <see cref="Mask"/> where it is, as a formatted line of a log.</summary>
    /// <returns>The length of the text now: a mask is never longer than what it replaces.</returns>
    public int MaskInPlace(Span<char> text)
    {
        var patterns = _patterns;
        if (patterns.All.Length == 0) return text.Length;
        var found = text.IndexOfAny(patterns.Search);
        if (found < 0) return text.Length;
        int read = 0, write = 0;
        while (found >= 0)
        {
            var at = read + found;
            var length = patterns.LengthAt(text[at..]); // before the mask may write over its start
            text[read..at].CopyTo(text[write..]);
            write += at - read;
            Mask.AsSpan().CopyTo(text[write..]);
            write += Mask.Length;
            read = at + length;
            found = text[read..].IndexOfAny(patterns.Search);
        }
        text[read..].CopyTo(text[write..]);
        return write + text.Length - read;
    }

    /// <summary>
    /// Replaces each secret inside the strings of a JSON text by <see cref="Mask"/>, as it is escaped there. Names, numbers and
    /// punctuation stay as they are, so the text stays valid JSON even when a secret looks like a number of it.
    /// </summary>
    /// <returns>The text, the same instance when it holds no secret.</returns>
    public string MaskJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var patterns = _patterns;
        if (patterns.All.Length == 0 || json.AsSpan().IndexOfAny(patterns.Search) < 0) return json;
        var builder = new StringBuilder(json.Length);
        var copied = 0;
        for (var index = 0; index < json.Length; index++)
        {
            if (json[index] != '"') continue;
            var start = index + 1;
            var end = start;
            while (end < json.Length && json[end] != '"') end += json[end] == '\\' ? 2 : 1;
            end = Math.Min(end, json.Length);
            // A string's content has no raw quote, so no match crosses its edges.
            builder.Append(json, copied, start - copied).Append(MaskWith(json[start..end], patterns));
            copied = end;
            index = end;
        }
        return builder.Append(json, copied, json.Length - copied).ToString();
    }

    /// <summary>A report of a session with each secret masked: its texts, and its JSON as JSON.</summary>
    /// <returns>The report itself when there is nothing to mask.</returns>
    public SessionReport MaskReport(SessionReport report)
    {
        if (IsEmpty) return report;
        return report switch
        {
            ItemReport item => item with { Text = MaskText(item.Text), Data = MaskData(item.Data) },
            StatusReport { Error: not null } status => status with { Error = MaskText(status.Error) },
            RunEndedReport ended => ended with { Outcome = MaskText(ended.Outcome) },
            ApprovalRequestedReport approval => approval with { Input = MaskData(approval.Input)!, Description = MaskText(approval.Description) },
            _ => report
        };
    }

    /// <summary>JSON as JSON, anything else as text.</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(data))]
    string? MaskData(string? data) => data is null ? null : data.AsSpan().TrimStart() is ['{' or '[', ..] ? MaskJson(data) : MaskText(data);

    /// <summary>
    /// The masking of one text that comes in parts, such as the deltas of a reply: what a part gives is masked and can be shown
    /// at once, and its end that could begin a secret is held back until the next part, or <see cref="SecretTextStream.Flush"/>,
    /// tells.
    /// </summary>
    public SecretTextStream CreateStream() => new(this);

    /// <summary>Replaces each secret of an exception by <see cref="Mask"/>, for what logs or exports it.</summary>
    /// <returns>The exception itself when it holds no secret; else a <see cref="MaskedException"/> that tells the same, masked.</returns>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(exception))]
    public Exception? MaskException(Exception? exception)
    {
        if (exception is null) return null;
        string text;
        try { text = exception.ToString(); }
        catch (Exception failure) when (failure is not OutOfMemoryException) { text = $"{exception.GetType().FullName}: {exception.Message}"; }
        if (!Contains(text) && !Contains(exception.Message)) return exception;
        return new MaskedException(exception.GetType().FullName ?? exception.GetType().Name, MaskText(exception.Message), MaskText(text),
            MaskException(exception.InnerException));
    }

    /// <summary>One text that comes in parts; see <see cref="SecretMasker.CreateStream"/>. Not thread-safe.</summary>
    public sealed class SecretTextStream
    {
        readonly SecretMasker _masker;
        string _held = "";

        internal SecretTextStream(SecretMasker masker) => _masker = masker;

        /// <summary>Takes the next part.</summary>
        /// <returns>What can be shown now, masked; possibly empty.</returns>
        public string Write(string part)
        {
            var text = _held + part;
            var patterns = _masker._patterns;
            if (patterns.All.Length == 0)
            {
                _held = "";
                return text;
            }
            // Up to the end that may begin a secret; a secret that begins before it is whole already (else the held end would
            // begin at its start), so it is masked whole even where it reaches into that end.
            var cut = text.Length - Held(text, patterns);
            var span = text.AsSpan();
            var shown = new StringBuilder(cut);
            var from = 0;
            int found;
            while ((found = span[from..].IndexOfAny(patterns.Search)) >= 0 && from + found < cut)
            {
                var at = from + found;
                shown.Append(span[from..at]).Append(Mask);
                from = at + patterns.LengthAt(span[at..]);
                cut = Math.Max(cut, from);
            }
            shown.Append(span[from..cut]);
            _held = text[cut..];
            return shown.ToString();
        }

        /// <summary>The text ended: what was held back, masked.</summary>
        public string Flush()
        {
            var rest = MaskWith(_held, _masker._patterns);
            _held = "";
            return rest;
        }

        /// <summary>The longest end of the text that begins a pattern without completing it, so a later part may complete it.</summary>
        static int Held(string text, Patterns patterns)
        {
            for (var length = Math.Min(text.Length, patterns.Longest - 1); length > 0; length--)
            {
                var end = text.AsSpan(text.Length - length);
                foreach (var pattern in patterns.All)
                    if (pattern.Length > length && pattern.AsSpan().StartsWith(end, StringComparison.Ordinal)) return length;
            }
            return 0;
        }
    }
}

/// <summary>An exception told again with its secrets masked: its type, message and text, for logs and exports.</summary>
public sealed class MaskedException(string typeName, string message, string text, Exception? inner) : Exception(message, inner)
{
    /// <summary>The full name of the type of the exception it stands for.</summary>
    public string TypeName { get; } = typeName;

    public override string ToString() => text;
}
