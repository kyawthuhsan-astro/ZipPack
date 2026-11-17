using ZipPack;
using ZipPackNet.Services;

namespace ZipPackNet.UI;

/// <summary>What the user chose in <see cref="ImageCompressDialog"/>.</summary>
/// <param name="Options">How to compress.</param>
/// <param name="ReplaceOriginals">
/// <see langword="true"/> to write each result into its source folder (replacing the original when the format is
/// unchanged); <see langword="false"/> to write everything into <paramref name="OutputFolder"/>.
/// </param>
/// <param name="OutputFolder">The folder for the results when <paramref name="ReplaceOriginals"/> is <see langword="false"/>.</param>
internal sealed record ImageJobChoices(ImageCompressOptions Options, bool ReplaceOriginals, string? OutputFolder);

/// <summary>The totals of an image job.</summary>
/// <param name="Compressed">The number of images written.</param>
/// <param name="KeptOriginal">How many of those could not be made smaller and were left as they were.</param>
/// <param name="OriginalBytes">The total size of the images written, before.</param>
/// <param name="CompressedBytes">The total size of the images written, after.</param>
/// <param name="Failures">The images that failed, with the reason.</param>
internal sealed record ImageJobSummary(int Compressed, int KeptOriginal, long OriginalBytes, long CompressedBytes, IReadOnlyList<(string Name, string Reason)> Failures)
{
    public long SavedBytes => OriginalBytes - CompressedBytes;

    /// <summary>Gets the saving in percent of the original size.</summary>
    public int SavedPercent => OriginalBytes > 0 ? (int)Math.Round(100d * SavedBytes / OriginalBytes) : 0;

    public static ImageJobSummary From(IReadOnlyList<ImageCompressResult> results)
    {
        ImageCompressResult[] good = [.. results.Where(r => r.Succeeded)];
        return new ImageJobSummary(
            good.Length,
            good.Count(r => r.KeptOriginal),
            good.Sum(r => r.OriginalSize),
            good.Sum(r => r.CompressedSize),
            [.. results.Where(r => !r.Succeeded).Select(r => (Path.GetFileName(r.SourcePath), r.Error!.Message))]);
    }

    /// <summary>Gets the message shown when the job is done.</summary>
    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        int total = Compressed + Failures.Count;
        text.Append(total == 1 && Compressed == 1 ? "The image was compressed" : $"{Compressed} of {total} images were compressed");
        if (Compressed > 0)
        {
            text.Append($": {Format.Size(OriginalBytes)} → {Format.Size(CompressedBytes)}");
            text.Append(SavedBytes > 0 ? $" (saved {Format.Size(SavedBytes)}, {SavedPercent}%)." : ".");
        }
        else
        {
            text.Append('.');
        }

        if (KeptOriginal > 0)
        {
            text.Append(KeptOriginal == 1
                ? "\n\n1 image was already as small as it gets and was left unchanged."
                : $"\n\n{KeptOriginal} images were already as small as they get and were left unchanged.");
        }

        if (Failures.Count > 0)
        {
            text.Append(Failures.Count == 1 ? "\n\n1 image could not be compressed:" : $"\n\n{Failures.Count} images could not be compressed:");
            foreach ((string name, string reason) in Failures.Take(5))
            {
                text.Append($"\n•  {name} — {reason}");
            }

            if (Failures.Count > 5)
            {
                text.Append($"\n•  … and {Failures.Count - 5} more");
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// The "Compress images" workflow: pick the images among the given paths, ask for options, compress them behind a
/// <see cref="ProgressDialog"/> and report what was saved.
/// </summary>
internal sealed class ImageWorkflow(ICompressionService service, AppSettings settings)
{
    private const long UnitsPerImage = 1000; // Matches the relay in ZipPack: progress counts images, not bytes.

    private IMagickNetCore Images => service.GetStrategy<IMagickNetCore>("image");

    /// <summary>Gets a value indicating whether any of the paths is, or contains, an image.</summary>
    public bool ContainsImages(IEnumerable<string> paths) => CollectImages(paths, Images).Count > 0;

    /// <summary>
    /// Returns the images among <paramref name="paths"/>: files are checked by content, folders are searched
    /// recursively by extension. Duplicates are removed; the order is kept.
    /// </summary>
    internal static IReadOnlyList<string> CollectImages(IEnumerable<string> paths, IMagickNetCore images)
    {
        var extensions = new HashSet<string>(images.FileExtensions, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                if (extensions.Contains(Path.GetExtension(path)) || images.CanCompressAsync(path).AsTask().GetAwaiter().GetResult())
                {
                    Add(path);
                }
            }
            else if (Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                foreach (string file in Directory.EnumerateFiles(path, "*", options).Where(file => extensions.Contains(Path.GetExtension(file))).Order(StringComparer.OrdinalIgnoreCase))
                {
                    Add(file);
                }
            }
        }

        return found;

        void Add(string file)
        {
            string full = Path.GetFullPath(file);
            if (seen.Add(full))
            {
                found.Add(full);
            }
        }
    }

    /// <summary>Splits the job into batches: one per source folder when writing next to the originals, else one.</summary>
    internal static IReadOnlyList<ImageBatchCompressRequest> Plan(IReadOnlyList<string> images, ImageJobChoices choices)
    {
        if (!choices.ReplaceOriginals)
        {
            ArgumentException.ThrowIfNullOrEmpty(choices.OutputFolder);
            return [new ImageBatchCompressRequest { SourcePaths = images, DestinationDirectory = choices.OutputFolder, Options = choices.Options }];
        }

        return
        [
            .. images
                .GroupBy(image => Path.GetDirectoryName(image)!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ImageBatchCompressRequest { SourcePaths = [.. group], DestinationDirectory = group.Key, Options = choices.Options }),
        ];
    }

    /// <summary>Runs the whole workflow.</summary>
    /// <returns>The totals, or <see langword="null"/> when there was nothing to do, or it was cancelled or failed.</returns>
    public ImageJobSummary? Compress(IWin32Window? owner, IReadOnlyList<string> paths)
    {
        IMagickNetCore images = Images;
        IReadOnlyList<string> files = CollectImages(paths, images);
        if (files.Count == 0)
        {
            MessageBox.Show(
                owner,
                $"There are no images among the selected items.\n\nSupported: {string.Join(" ", images.FileExtensions)}",
                "Compress images",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return null;
        }

        ImageJobChoices? choices = ImageCompressDialog.Prompt(owner, images, settings, files);
        if (choices is null)
        {
            return null;
        }

        IReadOnlyList<ImageBatchCompressRequest> batches = Plan(files, choices);
        var results = new List<ImageCompressResult>();

        OperationResult outcome = ProgressDialog.Run(
            owner,
            files.Count == 1 ? $"Compressing {Path.GetFileName(files[0])}" : $"Compressing {files.Count} images",
            async (progress, cancellationToken) =>
            {
                long done = 0;
                foreach (ImageBatchCompressRequest batch in batches)
                {
                    long offset = done;
                    var relay = new Progress(p => progress.Report(new CompressionProgress(
                        (offset * UnitsPerImage) + p.BytesProcessed, files.Count * UnitsPerImage, p.CurrentItem)));
                    results.AddRange(await images.CompressImagesAsync(batch, relay, cancellationToken));
                    done += batch.SourcePaths.Count;
                }
            },
            describe: p => $"{p.BytesProcessed / UnitsPerImage} of {p.TotalBytes / UnitsPerImage} images");

        if (outcome.Status == OperationStatus.Failed)
        {
            ErrorPresenter.Show(owner, outcome.Error!);
            return null;
        }

        if (outcome.Status != OperationStatus.Completed)
        {
            return null;
        }

        ImageJobSummary summary = ImageJobSummary.From(results);
        MessageBox.Show(
            owner,
            summary.Describe(),
            "Compress images",
            MessageBoxButtons.OK,
            summary.Failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        return summary;
    }

    /// <summary>Forwards synchronously (the progress dialog's sink is already thread-safe).</summary>
    private sealed class Progress(Action<CompressionProgress> handler) : IProgress<CompressionProgress>
    {
        public void Report(CompressionProgress value) => handler(value);
    }
}
