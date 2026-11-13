using System.Text;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon;

/// <summary>The daemon's log with the secrets it knows masked. Lines and strings are masked whole; a lone character is not.</summary>
public sealed class MaskingWriter(TextWriter inner, SecretMasker masker) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;
    public override void Write(char value) => inner.Write(value);
    public override void Write(string? value) => inner.Write(masker.MaskText(value));
    public override void WriteLine(string? value) => inner.WriteLine(masker.MaskText(value));
    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    public override void Flush() => inner.Flush();
    public override Task FlushAsync() => inner.FlushAsync();
    public override Task WriteAsync(string? value) => inner.WriteAsync(masker.MaskText(value));
    public override Task WriteLineAsync(string? value) => inner.WriteLineAsync(masker.MaskText(value));
}
