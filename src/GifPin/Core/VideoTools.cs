using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace GifPin.Core;

/// <summary>Pinned, checksum-verified tools, downloaded only when video import is used.</summary>
internal static class VideoTools
{
    internal const string Version = "8.1.2";
    internal const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip";
    internal const string Sha256 = "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(15) };
    internal static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GifPin", "VideoTools", Version);

    public static async Task<string> EnsureAsync(IProgress<double>? progress, CancellationToken token)
    {
        // A portable distribution may include the same tools alongside the executable.
        string bundled = Path.Combine(AppContext.BaseDirectory, "VideoTools");
        if (Ready(bundled)) return bundled;
        await Gate.WaitAsync(token);
        string? stage = null;
        try
        {
            if (Ready(CacheDir)) return CacheDir;
            string parent = Path.GetDirectoryName(CacheDir)!;
            Directory.CreateDirectory(parent);
            stage = Path.Combine(parent, "download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            string archive = Path.Combine(stage, "tools.zip");
            using (var response = await Client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                long length = response.Content.Headers.ContentLength ?? 0, received = 0;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(archive);
                byte[] buffer = new byte[81920];
                int n;
                while ((n = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += n;
                    if (received > 250L * 1024 * 1024) throw new InvalidDataException(Loc.T("video.toolDownloadFailed"));
                    await output.WriteAsync(buffer.AsMemory(0, n), token);
                    progress?.Report(length > 0 ? received / (double)length : 0);
                }
            }
            await using (var file = File.OpenRead(archive))
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                if (!hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Loc.T("video.toolChecksum"));
            }
            // Extract only known files to explicit destinations, never archive-provided paths.
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (string name in new[] { "ffmpeg.exe", "ffprobe.exe", "LICENSE", "README.txt" })
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (!entry.FullName.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)) continue;
                        token.ThrowIfCancellationRequested();
                        if (entry.Length > 300L * 1024 * 1024) throw new InvalidDataException(Loc.T("video.toolDownloadFailed"));
                        entry.ExtractToFile(Path.Combine(stage, name), overwrite: true);
                        break;
                    }
                }
            }
            if (!File.Exists(Path.Combine(stage, "ffmpeg.exe")) || !File.Exists(Path.Combine(stage, "ffprobe.exe")))
                throw new InvalidDataException(Loc.T("video.toolDownloadFailed"));
            File.Delete(archive);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(CacheDir);
            // Marker is written last. Interrupted installations are retried next time.
            foreach (string file in Directory.GetFiles(stage)) File.Move(file, Path.Combine(CacheDir, Path.GetFileName(file)), overwrite: true);
            File.WriteAllText(Path.Combine(CacheDir, "verified.txt"), Sha256);
            return CacheDir;
        }
        finally
        {
            if (stage != null) { try { Directory.Delete(stage, true); } catch { } }
            Gate.Release();
        }
    }

    private static bool Ready(string dir) => File.Exists(Path.Combine(dir, "ffmpeg.exe")) && File.Exists(Path.Combine(dir, "ffprobe.exe"))
        && File.Exists(Path.Combine(dir, "verified.txt")) && File.ReadAllText(Path.Combine(dir, "verified.txt")).Trim() == Sha256;
}
