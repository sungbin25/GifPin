using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace GifPin.Core;

internal sealed record VideoInfo(int Width, int Height, double Duration);
internal sealed record VideoOptions(double Start, double End, int Size, int Fps)
{
    public void Validate(VideoInfo info)
    {
        if (!double.IsFinite(Start) || !double.IsFinite(End) || Start < 0 || End <= Start || End > info.Duration + 0.001
            || Size < 64 || Size > 1024 || Fps < 1 || Fps > 30)
            throw new ArgumentException(Loc.T("video.invalidRange"));
    }
}

internal static class VideoConverter
{
    private static readonly string[] Extensions = { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".mpeg", ".mpg" };
    public static bool IsVideo(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static string Filter => Loc.T("video.files") + "|" + string.Join(";", Extensions.Select(e => "*" + e));
    private static string N(double n) => n.ToString("0.########", CultureInfo.InvariantCulture);

    private static Process Start(string tools, string exe, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(Path.Combine(tools, exe))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException(Loc.T("video.failed"));
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        char[] buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > 4096) text.Remove(0, text.Length - 4096);
        }
        return text.ToString();
    }

    public static async Task<VideoInfo> ProbeAsync(string tools, string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Start(tools, "ffprobe.exe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries",
            "stream=width,height,duration,sample_aspect_ratio:stream_side_data=rotation:format=duration", "-of", "json", Path.GetFullPath(path) });
        using var cancel = timeout.Token.Register(() => Kill(process));
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = ReadErrorsAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string json = await output, error = await errors;
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidDataException(Loc.T("video.cannotRead") + " " + error.Trim());
            return ParseInfo(json);
        }
        finally { Kill(process); await process.WaitForExitAsync(); await output; await errors; }
    }

    internal static VideoInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var streams = root.GetProperty("streams");
        if (streams.GetArrayLength() == 0) throw new InvalidDataException(Loc.T("video.noVideo"));
        var stream = streams[0];
        int width = stream.GetProperty("width").GetInt32(), height = stream.GetProperty("height").GetInt32();
        double duration = ReadNumber(stream, "duration");
        if (duration <= 0 && root.TryGetProperty("format", out var format)) duration = ReadNumber(format, "duration");
        if (stream.TryGetProperty("sample_aspect_ratio", out var sar))
        {
            string[] parts = (sar.GetString() ?? "").Split(':');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && a > 0 && b > 0)
                width = (int)Math.Clamp(Math.Round(width * a / b), 1, 32768);
        }
        if (stream.TryGetProperty("side_data_list", out var side))
            foreach (var item in side.EnumerateArray())
                if (item.TryGetProperty("rotation", out var rotation) && Math.Abs(rotation.GetDouble() % 180) > 45) { (width, height) = (height, width); break; }
        if (width <= 0 || height <= 0 || !double.IsFinite(duration) || duration <= 0) throw new InvalidDataException(Loc.T("video.cannotRead"));
        return new VideoInfo(width, height, duration);
    }

    private static double ReadNumber(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : 0;

    internal static (int w, int h) Dimensions(VideoInfo info, int size)
    {
        double scale = Math.Min(1, size / (double)Math.Max(info.Width, info.Height));
        return (Math.Max(1, (int)Math.Round(info.Width * scale)), Math.Max(1, (int)Math.Round(info.Height * scale)));
    }

    /// <summary>Callback owns neither the bitmap nor its pixels; copy to retain a frame.</summary>
    private static async Task<int> ReadFramesAsync(string tools, string path, VideoOptions options, VideoInfo info,
        double fps, int size, Action<SKBitmap, int> onFrame, CancellationToken token)
    {
        options.Validate(info);
        var (w, h) = Dimensions(info, size);
        using var process = Start(tools, "ffmpeg.exe", new[] { "-nostdin", "-v", "error", "-threads", "2", "-ss", N(options.Start),
            "-i", Path.GetFullPath(path), "-t", N(options.End - options.Start), "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"fps={N(fps)}:round=up,scale={w}:{h}:flags=lanczos,setsar=1", "-threads", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" });
        using var cancel = token.Register(() => Kill(process));
        // Drain stderr concurrently to prevent pipe deadlock.
        var errors = ReadErrorsAsync(process.StandardError);
        byte[] buffer = new byte[checked(w * h * 4)];
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        int count = 0;
        try
        {
            while (true)
            {
                int offset = 0;
                while (offset < buffer.Length)
                {
                    // A damaged decoder must not leave the import window stuck indefinitely.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    idle.CancelAfter(TimeSpan.FromSeconds(60));
                    int n = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(offset), idle.Token);
                    if (n == 0) break;
                    offset += n;
                }
                token.ThrowIfCancellationRequested();
                if (offset == 0) break;
                if (offset != buffer.Length) throw new InvalidDataException(Loc.T("video.cannotRead"));
                // FFmpeg emits straight alpha; WPF and BackgroundRemover consume premultiplied pixels.
                for (int i = 0; i < buffer.Length; i += 4)
                {
                    int alpha = buffer[i + 3];
                    if (alpha == 255) continue;
                    buffer[i] = (byte)((buffer[i] * alpha + 127) / 255);
                    buffer[i + 1] = (byte)((buffer[i + 1] * alpha + 127) / 255);
                    buffer[i + 2] = (byte)((buffer[i + 2] * alpha + 127) / 255);
                }
                Marshal.Copy(buffer, 0, bitmap.GetPixels(), buffer.Length);
                onFrame(bitmap, count++);
            }
            await process.WaitForExitAsync(token);
            string error = await errors;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidDataException(Loc.T("video.cannotRead") + " " + error.Trim());
            if (count == 0) throw new InvalidDataException(Loc.T("err.noFrames"));
            return count;
        }
        finally { Kill(process); await process.WaitForExitAsync(); await errors; }
    }

    public static async Task<(List<SKBitmap> frames, double fps)> PreviewAsync(string tools, string path, VideoOptions options, VideoInfo info, CancellationToken token)
    {
        var frames = new List<SKBitmap>();
        // Sample the whole selected interval, limited to ~48 MB of source pixels.
        double fps = Math.Min(options.Fps, 120 / (options.End - options.Start));
        try
        {
            await ReadFramesAsync(tools, path, options, info, fps, Math.Min(options.Size, 320), (frame, _) =>
            {
                if (frames.Count < 121) frames.Add(frame.Copy());
            }, token);
            return (frames, fps);
        }
        catch { foreach (var frame in frames) frame.Dispose(); throw; }
    }

    public static async Task ConvertAsync(string tools, string path, string output, VideoOptions options, VideoInfo info,
        BgRemoveSettings? background, IProgress<double>? progress, CancellationToken token)
    {
        options.Validate(info);
        var (w, h) = Dimensions(info, options.Size);
        SKColor color = SKColors.White;
        if (background != null && !BackgroundRemover.TryParseHex(background.Color, out color)) throw new ArgumentException("Invalid background color");
        string partial = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var writer = new WebpWriter.Streaming(partial, w, h))
            {
                await ReadFramesAsync(tools, path, options, info, options.Fps, options.Size, (frame, index) =>
                {
                    token.ThrowIfCancellationRequested();
                    // Distributed rounding keeps 24/30 fps timing accurate over long clips.
                    int delay = (int)Math.Round((index + 1) * 1000.0 / options.Fps) - (int)Math.Round(index * 1000.0 / options.Fps);
                    if (background != null)
                    {
                        using var result = BackgroundRemover.Process(frame, color, background.Tolerance, background.ConnectedOnly, background.Refine);
                        writer.Add(result, delay);
                    }
                    else writer.Add(frame, delay);
                    progress?.Report(Math.Min(0.99, (index + 1) / (options.Fps * (options.End - options.Start))));
                }, token);
                token.ThrowIfCancellationRequested();
                writer.Complete();
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, output); // never overwrite an existing asset
            progress?.Report(1);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
