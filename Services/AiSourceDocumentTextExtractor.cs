using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EPATA.BusinessLedger.Services;

public sealed class AiSourceDocumentTextExtractor
{
    public const int MaxExtractedCharactersPerFile = 150_000;

    public static readonly string[] SupportedExtensions =
    [
        ".txt", ".md", ".eml", ".csv", ".json", ".pdf", ".docx"
    ];

    public async Task<AiSourceDocumentExtraction> ExtractAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        string text;
        string? warning = null;

        if (file.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || extension is ".txt" or ".md" or ".eml" or ".csv" or ".json")
        {
            using var reader = new StreamReader(file.OpenReadStream(), detectEncodingFromByteOrderMarks: true);
            text = await ReadAtMostAsync(reader, MaxExtractedCharactersPerFile + 1, cancellationToken);
        }
        else if (extension == ".docx")
        {
            await using var memory = new MemoryStream();
            await file.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;
            text = ExtractDocx(memory);
        }
        else if (extension == ".pdf")
        {
            await using var memory = new MemoryStream();
            await file.CopyToAsync(memory, cancellationToken);
            text = ExtractPdf(memory.ToArray());
            if (string.IsNullOrWhiteSpace(text))
            {
                warning = $"{file.FileName} did not contain readable embedded PDF text. A scanned/image-only PDF needs OCR or a configured vision-capable AI model.";
            }
        }
        else
        {
            return new AiSourceDocumentExtraction(string.Empty, $"{file.FileName} was skipped because its file type is not supported.");
        }

        text = Clean(text);
        if (text.Length > MaxExtractedCharactersPerFile)
        {
            text = text[..MaxExtractedCharactersPerFile];
            warning = $"{file.FileName} was read, but its extracted text was shortened to {MaxExtractedCharactersPerFile:N0} characters.";
        }

        return new AiSourceDocumentExtraction(text, warning);
    }

    private static string ExtractDocx(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries
            .Where(entry => entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(entry.FullName, @"^word/(?:header|footer|footnotes|endnotes)\d*\.xml$", RegexOptions.IgnoreCase))
            .OrderBy(entry => entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        var sections = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.Length > 5 * 1024 * 1024)
            {
                throw new InvalidDataException("A DOCX XML section expands beyond the safe extraction limit.");
            }
            using var entryStream = entry.Open();
            var document = XDocument.Load(entryStream);
            var paragraphs = document
                .Descendants()
                .Where(element => element.Name.LocalName == "p")
                .Select(paragraph => string.Concat(paragraph.Descendants()
                    .Where(element => element.Name.LocalName is "t" or "tab" or "br")
                    .Select(element => element.Name.LocalName == "t" ? element.Value : " ")))
                .Where(value => !string.IsNullOrWhiteSpace(value));
            sections.AddRange(paragraphs);
        }

        return string.Join(Environment.NewLine, sections);
    }

    private static string ExtractPdf(byte[] bytes)
    {
        var raw = Encoding.Latin1.GetString(bytes);
        var pieces = ExtractPdfTextOperators(raw).ToList();

        foreach (Match match in Regex.Matches(raw, @"(?s)(?<dictionary><<.*?>>)\s*stream\r?\n(?<stream>.*?)\r?\nendstream"))
        {
            if (!match.Groups["dictionary"].Value.Contains("FlateDecode", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var compressed = Encoding.Latin1.GetBytes(match.Groups["stream"].Value);
                using var input = new MemoryStream(compressed);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                CopyAtMost(zlib, output, 2_000_000);
                pieces.AddRange(ExtractPdfTextOperators(Encoding.Latin1.GetString(output.ToArray())));
            }
            catch
            {
                // Some PDF streams require filters or encodings beyond this best-effort extractor.
            }
        }

        return string.Join(" ", pieces);
    }

    private static void CopyAtMost(Stream input, Stream output, int maxBytes)
    {
        var buffer = new byte[16_384];
        var total = 0;
        while (total < maxBytes)
        {
            var read = input.Read(buffer, 0, Math.Min(buffer.Length, maxBytes - total));
            if (read == 0) break;
            output.Write(buffer, 0, read);
            total += read;
        }
    }

    private static IEnumerable<string> ExtractPdfTextOperators(string raw)
    {
        foreach (Match match in Regex.Matches(raw, @"\((?<text>(?:\\.|[^\\)])+)\)\s*(?:Tj|'|"")"))
        {
            var decoded = DecodePdfString(match.Groups["text"].Value);
            if (decoded.Any(char.IsLetterOrDigit)) yield return decoded;
        }

        foreach (Match array in Regex.Matches(raw, @"(?s)\[(?<items>.*?)\]\s*TJ"))
        {
            var text = string.Concat(Regex.Matches(array.Groups["items"].Value, @"\((?<text>(?:\\.|[^\\)])+)\)")
                .Select(match => DecodePdfString(match.Groups["text"].Value)));
            if (text.Any(char.IsLetterOrDigit)) yield return text;
        }
    }

    private static string DecodePdfString(string value)
    {
        return Regex.Replace(value, @"\\(?:(?<octal>[0-7]{1,3})|(?<escaped>[nrtbf()\\]))", match =>
        {
            if (match.Groups["octal"].Success)
            {
                return ((char)Convert.ToInt32(match.Groups["octal"].Value, 8)).ToString();
            }

            return match.Groups["escaped"].Value switch
            {
                "n" or "r" => " ",
                "t" => "\t",
                "b" or "f" => " ",
                _ => match.Groups["escaped"].Value
            };
        });
    }

    private static async Task<string> ReadAtMostAsync(StreamReader reader, int maxCharacters, CancellationToken cancellationToken)
    {
        var buffer = new char[Math.Min(maxCharacters, 16_384)];
        var builder = new StringBuilder(Math.Min(maxCharacters, 32_768));
        while (builder.Length < maxCharacters)
        {
            var requested = Math.Min(buffer.Length, maxCharacters - builder.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0) break;
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }

    private static string Clean(string value) => Regex.Replace(value, @"[^\S\r\n]+", " ").Trim();
}

public sealed record AiSourceDocumentExtraction(string Text, string? Warning);
