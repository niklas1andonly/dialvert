using System.Text;

namespace Dialvert.Core.Engines.Document;

/// <summary>Reads text files of unknown encoding: BOM → strict UTF-8 → Windows-1252 (old German exports).</summary>
static class TextFiles
{
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(bytes); }
    }

    public static void Write(string path, string text, bool bom) =>
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));
}
