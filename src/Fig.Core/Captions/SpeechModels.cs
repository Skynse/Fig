using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Fig.Core.Captions
{
    /// <summary>A Whisper model Fig can download: its ggml file on Hugging Face, its size and its SHA-256.</summary>
    public sealed record SpeechModel(string Id, string Name, string Description, string FileName, long Bytes, string Sha256)
    {
        public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}";

        public string SizeLabel => Bytes >= 1_000_000_000 ? $"{Bytes / 1e9:0.0} GB" : $"{Bytes / 1e6:0} MB";
    }

    /// <summary>
    /// The speech models, and the folder they are kept in. Models are not shipped with Fig: one is downloaded the first
    /// time it is used, checked against its hash, and kept for next time. The quantized builds are a fraction of the
    /// full ones' size and transcribe as well for captions.
    /// </summary>
    public sealed class SpeechModels
    {
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        public static IReadOnlyList<SpeechModel> All { get; } =
        [
            new("tiny", "Tiny", "Fastest. Fine for clear speech.", "ggml-tiny-q5_1.bin", 32_152_673,
                "818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7"),
            new("base", "Base", "Fast and good. The usual choice.", "ggml-base-q5_1.bin", 59_707_625,
                "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898"),
            new("small", "Small", "Better with accents, noise and music. Slower.", "ggml-small-q5_1.bin", 190_085_487,
                "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"),
            new("turbo", "Large turbo", "Most accurate. Slow without a strong CPU.", "ggml-large-v3-turbo-q5_0.bin", 574_041_195,
                "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
        ];

        public static SpeechModel Default => All[1];

        public SpeechModels(string directory) => Directory = directory;

        /// <summary>Where the models are kept (<c>~/.fig/models/whisper</c>).</summary>
        public string Directory { get; }

        public string PathOf(SpeechModel model) => Path.Combine(Directory, model.FileName);

        /// <summary>True when the model is on disk, whole. (Its hash was checked when it was downloaded.)</summary>
        public bool IsInstalled(SpeechModel model)
            => File.Exists(PathOf(model)) && new FileInfo(PathOf(model)).Length == model.Bytes;

        /// <summary>
        /// Downloads the model to a partial file, checks its hash, then moves it into place, so a model on disk is
        /// always whole. <paramref name="progress"/> gets 0..1. A failed or cancelled download leaves nothing behind.
        /// </summary>
        public async Task DownloadAsync(SpeechModel model, IProgress<double>? progress, CancellationToken cancel)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var target = PathOf(model);
            var part = target + ".part";
            try
            {
                using var response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? model.Bytes;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var body = await response.Content.ReadAsStreamAsync(cancel))
                await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long read = 0;
                    int n;
                    while ((n = await body.ReadAsync(buffer, cancel)) > 0)
                    {
                        hash.AppendData(buffer, 0, n);
                        await file.WriteAsync(buffer.AsMemory(0, n), cancel);
                        read += n;
                        progress?.Report(Math.Min(1, read / (double)total));
                    }
                }
                var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (sha != model.Sha256)
                    throw new InvalidDataException($"The downloaded {model.Name} model is damaged (its checksum does not match). Try again.");
                File.Move(part, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(part))
                    File.Delete(part);
            }
        }

        /// <summary>Removes a downloaded model.</summary>
        public void Delete(SpeechModel model)
        {
            if (File.Exists(PathOf(model)))
                File.Delete(PathOf(model));
        }

        public static SpeechModel? Find(string id) => All.FirstOrDefault(m => m.Id == id);
    }
}
