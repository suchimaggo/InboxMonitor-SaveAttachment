using System.IO.Compression;

namespace InboxReader;

public sealed record ExtractedZipDocument(
    string EntryName,
    byte[] Content);

public sealed record ZipExtractionResult(
    bool Success,
    IReadOnlyList<ExtractedZipDocument> Documents,
    string? Error);

public static class ZipDocumentExtractor
{
    private const int MaximumFiles = 50;

    private const long MaximumTotalUncompressedBytes =
        100L * 1024 * 1024;

    private static readonly HashSet<string>
        SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
            ".jpg",
            ".jpeg",
            ".png"
        };

    public static ZipExtractionResult Extract(
        byte[] zipBytes)
    {
        ArgumentNullException.ThrowIfNull(zipBytes);

        if (zipBytes.Length == 0)
        {
            return Failure(
                "ZIP file is empty.");
        }

        try
        {
            using var zipStream =
                new MemoryStream(zipBytes);

            using var archive =
                new ZipArchive(
                    zipStream,
                    ZipArchiveMode.Read,
                    leaveOpen: false);

            var documents =
                new List<ExtractedZipDocument>();

            long totalUncompressedBytes = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue;
                }

                var normalizedPath =
                    entry.FullName.Replace('\\', '/');

                if (normalizedPath.StartsWith(
                        "__MACOSX/",
                        StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.Equals(
                        ".DS_Store",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsUnsafeEntryPath(normalizedPath))
                {
                    return Failure(
                        "ZIP contains an unsafe entry path.");
                }

                var extension =
                    Path.GetExtension(entry.Name);

                if (extension.Equals(
                        ".zip",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        "Nested ZIP files are not supported.");
                }

                if (!SupportedExtensions.Contains(extension))
                {
                    continue;
                }

                if (documents.Count >= MaximumFiles)
                {
                    return Failure(
                        $"ZIP contains more than {MaximumFiles} supported documents.");
                }

                totalUncompressedBytes += entry.Length;

                if (totalUncompressedBytes >
                    MaximumTotalUncompressedBytes)
                {
                    return Failure(
                        "ZIP exceeds the 100 MB total uncompressed limit.");
                }

                using var entryStream =
                    entry.Open();

                using var memory =
                    new MemoryStream();

                entryStream.CopyTo(memory);

                documents.Add(
                    new ExtractedZipDocument(
                        EntryName:
                            entry.Name,
                        Content:
                            memory.ToArray()));
            }

            if (documents.Count == 0)
            {
                return Failure(
                    "ZIP contains no supported documents.");
            }

            return new ZipExtractionResult(
                Success: true,
                Documents: documents,
                Error: null);
        }
        catch (InvalidDataException)
        {
            return Failure(
                "ZIP is corrupt, invalid, or unsupported.");
        }
        catch (NotSupportedException)
        {
            return Failure(
                "ZIP uses an unsupported compression or encryption format.");
        }
    }

    private static bool IsUnsafeEntryPath(
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        if (path.StartsWith(
                "/",
                StringComparison.Ordinal))
        {
            return true;
        }

        var segments =
            path.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(segment =>
            segment.Equals(
                "..",
                StringComparison.Ordinal));
    }

    private static ZipExtractionResult Failure(
        string error)
    {
        return new ZipExtractionResult(
            Success: false,
            Documents:
                Array.Empty<ExtractedZipDocument>(),
            Error: error);
    }
}