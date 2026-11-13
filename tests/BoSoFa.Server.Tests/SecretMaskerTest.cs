using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class SecretMaskerTest
{
    const string Token = "glsa_Abc+def/ghi=jkl\"mno<p>&q'r_0123";

    static SecretMasker With(params string[] values)
    {
        var masker = new SecretMasker();
        masker.Retain(values);
        return masker;
    }

    [Fact]
    public void AValueIsMaskedWhereverItIs()
    {
        var masker = With("s3cret-value", "other-one");
        Assert.Equal("key=*** and *** and ***.", masker.MaskText("key=s3cret-value and other-one and s3cret-value."));
        var clean = "nothing to hide";
        Assert.Same(clean, masker.MaskText(clean));
        Assert.True(masker.Contains("x s3cret-value"));
        Assert.False(masker.Contains("s3cret-valu"));
        Assert.Null(masker.MaskText(null));
        Assert.Equal("", masker.MaskText(""));
    }

    [Fact]
    public void ShortValuesAreNotMaskedAndRetainingIsForGood()
    {
        var masker = new SecretMasker();
        Assert.True(masker.IsEmpty);
        masker.Retain(["abc", null, ""]);
        Assert.True(masker.IsEmpty);
        Assert.Equal("abc abcd", masker.MaskText("abc abcd"));
        masker.Retain("abcd"); // short values are masked as they are, not in their other forms
        masker.Retain("abcd");
        Assert.Equal("abc *** YWJjZA==", masker.MaskText("abc abcd YWJjZA=="));
        // A rotation gives a new value; the old one stays masked, since a process may still print it.
        masker.Retain("rotated-value-2");
        Assert.Equal("*** ***", masker.MaskText("abcd rotated-value-2"));
    }

    [Fact]
    public void TheLongestValueWinsWhereOneBeginsAnother()
    {
        var masker = With("token-1234", "token-1234-extended");
        Assert.Equal("[***] [***]", masker.MaskText("[token-1234-extended] [token-1234]"));
    }

    [Fact]
    public void TheEncodedFormsOfAValueAreMasked()
    {
        var masker = With(Token);
        var bytes = Encoding.UTF8.GetBytes(Token);
        string[] forms =
        [
            JsonSerializer.Serialize(Token)[1..^1], // System.Text.Json: +, ", <…
            JsonSerializer.Serialize(Token, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })[1..^1],
            System.Text.RegularExpressions.Regex.Replace(JsonSerializer.Serialize(Token)[1..^1], @"\\u[0-9A-F]{4}", match => match.Value.ToLowerInvariant()), // lowercase hex
            Uri.EscapeDataString(Token),
            System.Text.RegularExpressions.Regex.Replace(Uri.EscapeDataString(Token), "%[0-9A-F]{2}", match => match.Value.ToLowerInvariant()),
            WebUtility.UrlEncode(Token),
            Convert.ToBase64String(bytes),
            Convert.ToBase64String(bytes).TrimEnd('='),
            Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')
        ];
        foreach (var form in forms)
            Assert.Equal("<***>", masker.MaskText($"<{form}>"));

        // `echo $TOKEN | base64`: the Base64 of a longer text begins with the value's own, up to its last whole group.
        var echoed = Convert.ToBase64String(Encoding.UTF8.GetBytes(Token + "\n"));
        var masked = masker.MaskText(echoed);
        Assert.StartsWith(SecretMasker.Mask, masked);
        Assert.True(masked.Length - SecretMasker.Mask.Length <= 4, masked); // at most the last group, a byte of the value and the newline
    }

    [Fact]
    public void JsonStaysValidWithItsSecretsMasked()
    {
        var masker = With(Token, "12345678");
        var json = JsonSerializer.Serialize(new JsonObject
        {
            ["command"] = $"curl -H 'Authorization: Bearer {Token}'",
            ["count"] = 12345678, // a number that looks like a secret is no string of it
            ["nested"] = new JsonArray(Token, "12345678", "fine")
        });
        var masked = masker.MaskJson(json);
        var node = JsonNode.Parse(masked)!;
        Assert.Equal("curl -H 'Authorization: Bearer ***'", node["command"]!.GetValue<string>());
        Assert.Equal(12345678, node["count"]!.GetValue<int>());
        Assert.Equal(["***", "***", "fine"], node["nested"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Same(json, With("absent-value").MaskJson(json));
        Assert.Equal("{\"a\":\"***", masker.MaskJson("{\"a\":\"12345678")); // cut off: still masked, nothing more made up
    }

    [Fact]
    public void AStreamMasksWhereverItIsCut()
    {
        var masker = With("token-1234", "token-1234-extended", Token);
        var text = $"a token-1234-extended, b token-1234; c {Token}. d token-12 and token-1234";
        var whole = masker.MaskText(text);
        for (var cut = 0; cut <= text.Length; cut++)
        {
            var stream = masker.CreateStream();
            var shown = stream.Write(text[..cut]) + stream.Write(text[cut..]) + stream.Flush();
            Assert.True(whole == shown, $"cut at {cut}: {shown}");
        }
        // A character at a time: nothing of a secret is ever shown, and the end is held only while it may begin one.
        var single = masker.CreateStream();
        var builder = new StringBuilder();
        foreach (var character in text)
        {
            builder.Append(single.Write(character.ToString()));
            Assert.DoesNotContain("token-1234", builder.ToString());
        }
        Assert.Equal(whole, builder.Append(single.Flush()).ToString());
        var plain = masker.CreateStream();
        Assert.Equal("no secret here", plain.Write("no secret here"));
        Assert.Equal("", plain.Flush());
    }

    /// <summary>Texts made of values, their forms and pieces of them, cut anywhere: a stream shows what masking the whole shows.</summary>
    [Fact]
    public void AStreamAgreesWithTheWholeTextCutAtRandom()
    {
        string[] values = ["token-1234", "token-1234-extended", "abcd", Token, "dG9rZW4tMTIz"];
        var masker = With(values);
        string[] pieces = [.. values, .. values.Select(value => value[..(value.Length / 2)]), .. values.Select(value => value[1..]),
            JsonSerializer.Serialize(Token)[1..^1], Convert.ToBase64String(Encoding.UTF8.GetBytes("token-1234\n")), " ", "-", "d", "ed", "x"];
        var random = new Random(20261113);
        for (var round = 0; round < 2000; round++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(1, 12)).Select(_ => pieces[random.Next(pieces.Length)]));
            var stream = masker.CreateStream();
            var shown = new StringBuilder();
            for (var at = 0; at < text.Length;)
            {
                var length = Math.Min(random.Next(1, 8), text.Length - at);
                shown.Append(stream.Write(text.Substring(at, length)));
                at += length;
            }
            shown.Append(stream.Flush());
            Assert.True(masker.MaskText(text) == shown.ToString(), $"{text}\n{shown}");
        }
    }

    [Fact]
    public void ALogLineIsMaskedInPlace()
    {
        var masker = With("token-1234", Token);
        var line = $"[info] calling with token-1234 and {JsonSerializer.Serialize(Token)} done";
        var buffer = line.ToCharArray();
        var length = masker.MaskInPlace(buffer);
        Assert.Equal(masker.MaskText(line), new string(buffer, 0, length));
        var clean = "nothing".ToCharArray();
        Assert.Equal(clean.Length, masker.MaskInPlace(clean));
    }

    [Fact]
    public void AnExceptionIsToldMasked()
    {
        var masker = With("token-1234");
        var clean = new InvalidOperationException("fine");
        Assert.Same(clean, masker.MaskException(clean));
        var leaking = new InvalidOperationException("failed with token-1234", new IOException("inner token-1234"));
        var masked = Assert.IsType<MaskedException>(masker.MaskException(leaking));
        Assert.Equal(("System.InvalidOperationException", "failed with ***"), (masked.TypeName, masked.Message));
        Assert.DoesNotContain("token-1234", masked.ToString());
        Assert.Equal("inner ***", masked.InnerException!.Message);
    }
}
