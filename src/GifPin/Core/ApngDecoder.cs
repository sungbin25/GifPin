using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;

namespace GifPin.Core;

/// <summary>
/// 애니메이션 PNG(APNG) 디코더. SkiaSharp은 APNG의 첫 장만 읽으므로 직접 처리한다.
/// 방식: 프레임마다 IHDR + 공용 보조 청크 + 해당 프레임 이미지 데이터로 "한 장짜리 PNG"를 다시 만들어
/// Skia로 디코딩하고, fcTL의 위치·폐기(dispose)·합성(blend) 규칙대로 캔버스에 겹쳐 완성 프레임을 얻는다.
/// </summary>
internal static class ApngDecoder
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    // 각 프레임 PNG에 그대로 복사할 보조 청크 (팔레트·투명색·색공간)
    private static readonly HashSet<string> SharedChunks = new() { "PLTE", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT" };
    private const long MaxFileBytes = 200L * 1024 * 1024;

    private readonly record struct Chunk(string Type, int DataOffset, int Length);

    private sealed class Frame
    {
        public int Width, Height, X, Y, DelayNum, DelayDen;
        public byte Dispose, Blend;
        public readonly List<(int offset, int length)> Data = new();
    }

    /// <summary>APNG인지 빠르게 확인 (PNG이고 IDAT 앞에 acTL이 있음).</summary>
    public static bool IsApng(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var sig = new byte[8];
            if (fs.Read(sig, 0, 8) != 8 || !StartsWithSignature(sig)) return false;
            var head = new byte[8];
            while (fs.Read(head, 0, 8) == 8)
            {
                int len = ReadInt(head, 0);
                string type = Encoding.ASCII.GetString(head, 4, 4);
                if (type == "acTL") return true;
                if (type == "IDAT" || type == "IEND" || len < 0) return false;
                fs.Seek(len + 4L, SeekOrigin.Current); // 데이터 + CRC
            }
        }
        catch { }
        return false;
    }

    /// <summary>(폭, 높이, 프레임 수). APNG가 아니면 null.</summary>
    public static (int width, int height, int frames)? Probe(string path)
    {
        try
        {
            var bytes = ReadAll(path);
            var chunks = ParseChunks(bytes);
            int w = 0, h = 0, frames = 0;
            foreach (var c in chunks)
            {
                if (c.Type == "IHDR") { w = ReadInt(bytes, c.DataOffset); h = ReadInt(bytes, c.DataOffset + 4); }
                else if (c.Type == "acTL") frames = ReadInt(bytes, c.DataOffset);
            }
            return frames > 0 && w > 0 && h > 0 ? (w, h, frames) : null;
        }
        catch { return null; }
    }

    public static GifAnimation Decode(string path) => Decode(path, 0, 0, 1);

    /// <summary>targetW×targetH로 줄여서(0이면 원본) step 간격으로 프레임을 저장. 합성은 원본 크기 캔버스 하나에서.</summary>
    public static GifAnimation Decode(string path, int targetW, int targetH, int step)
    {
        var bytes = ReadAll(path);
        var chunks = ParseChunks(bytes);

        Chunk? ihdr = null;
        var shared = new List<Chunk>();
        var frames = new List<Frame>();
        Frame? current = null;
        bool seenIdat = false;

        foreach (var c in chunks)
        {
            switch (c.Type)
            {
                case "IHDR": if (c.Length >= 13) ihdr = c; break;
                case "fcTL":
                    if (c.Length < 26) throw new InvalidDataException("APNG: fcTL chunk too short");
                    current = new Frame
                    {
                        Width = ReadInt(bytes, c.DataOffset + 4),
                        Height = ReadInt(bytes, c.DataOffset + 8),
                        X = ReadInt(bytes, c.DataOffset + 12),
                        Y = ReadInt(bytes, c.DataOffset + 16),
                        DelayNum = (bytes[c.DataOffset + 20] << 8) | bytes[c.DataOffset + 21],
                        DelayDen = (bytes[c.DataOffset + 22] << 8) | bytes[c.DataOffset + 23],
                        Dispose = bytes[c.DataOffset + 24],
                        Blend = bytes[c.DataOffset + 25],
                    };
                    frames.Add(current);
                    break;
                case "IDAT":
                    seenIdat = true;
                    // fcTL이 IDAT보다 앞에 있으면 기본 이미지가 첫 프레임, 아니면 애니메이션에 포함되지 않음
                    if (current != null && frames.Count == 1) current.Data.Add((c.DataOffset, c.Length));
                    break;
                case "fdAT":
                    if (current != null && c.Length > 4) current.Data.Add((c.DataOffset + 4, c.Length - 4)); // 앞 4바이트 = 순번
                    break;
                default:
                    if (!seenIdat && SharedChunks.Contains(c.Type)) shared.Add(c);
                    break;
            }
        }
        if (ihdr == null) throw new InvalidDataException("APNG: missing IHDR");
        frames.RemoveAll(f => f.Data.Count == 0 || f.Width <= 0 || f.Height <= 0);
        if (frames.Count == 0) throw new InvalidDataException("APNG: no frames");

        int width = ReadInt(bytes, ihdr.Value.DataOffset), height = ReadInt(bytes, ihdr.Value.DataOffset + 4);
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) throw new InvalidDataException("APNG: unsupported image size");

        if ((long)width * height > GifAnimation.MaxCanvasPixels) throw new InvalidDataException("APNG: unsupported image size");
        if (targetW <= 0 || targetH <= 0) { targetW = width; targetH = height; }
        step = Math.Max(1, step);
        bool scale = targetW != width || targetH != height;
        var target = new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
        var anim = new GifAnimation { Width = targetW, Height = targetH, SourceWidth = width, SourceHeight = height, FrameStep = step };
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvasBmp = new SKBitmap(info);
        canvasBmp.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(canvasBmp);
        using var copyPaint = new SKPaint { BlendMode = SKBlendMode.Src };

        try
        {
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                if (f.X < 0 || f.Y < 0 || (long)f.X + f.Width > width || (long)f.Y + f.Height > height)
                    throw new InvalidDataException($"APNG: frame {i + 1} is outside the image");

                using var piece = DecodeFramePng(bytes, ihdr.Value, shared, f);
                var region = SKRect.Create(f.X, f.Y, f.Width, f.Height);
                byte dispose = i == 0 && f.Dispose == 2 ? (byte)1 : f.Dispose; // 첫 프레임의 "이전으로"는 "지우기"로 취급
                using var before = dispose == 2 ? canvasBmp.Copy() : null;

                if (f.Blend == 0) // SOURCE: 영역을 지우고 덮어씀
                {
                    canvas.Save();
                    canvas.ClipRect(region);
                    canvas.Clear(SKColors.Transparent);
                    canvas.Restore();
                }
                canvas.DrawBitmap(piece, f.X, f.Y);
                canvas.Flush();

                int den = f.DelayDen == 0 ? 100 : f.DelayDen;
                int ms = (int)Math.Round(f.DelayNum * 1000.0 / den);
                ms = ms <= 10 ? 100 : ms;
                if (i % step == 0)
                {
                    anim.Frames.Add(scale ? canvasBmp.Resize(target, SKFilterQuality.Medium) ?? canvasBmp.Copy() : canvasBmp.Copy());
                    anim.DelaysMs.Add(ms);
                }
                else if (anim.DelaysMs.Count > 0) anim.DelaysMs[^1] += ms; // 건너뛴 프레임 시간은 앞 프레임에 더함

                if (dispose == 1)
                {
                    canvas.Save();
                    canvas.ClipRect(region);
                    canvas.Clear(SKColors.Transparent);
                    canvas.Restore();
                }
                else if (dispose == 2 && before != null)
                {
                    canvas.DrawBitmap(before, 0, 0, copyPaint);
                }
                canvas.Flush();
            }
        }
        catch
        {
            anim.Dispose();
            throw;
        }
        return anim;
    }

    /// <summary>프레임 하나를 독립된 PNG로 다시 만들어 디코딩 → BGRA premultiplied 비트맵.</summary>
    private static SKBitmap DecodeFramePng(byte[] src, Chunk ihdr, List<Chunk> shared, Frame f)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);

        var hdr = new byte[13];
        Array.Copy(src, ihdr.DataOffset, hdr, 0, 13);
        WriteInt(hdr, 0, f.Width);
        WriteInt(hdr, 4, f.Height);
        WriteChunk(ms, "IHDR", hdr, 0, 13);

        foreach (var c in shared) WriteChunk(ms, c.Type, src, c.DataOffset, c.Length);

        int total = 0;
        foreach (var (_, len) in f.Data) total += len;
        var idat = new byte[total];
        int pos = 0;
        foreach (var (off, len) in f.Data) { Array.Copy(src, off, idat, pos, len); pos += len; }
        WriteChunk(ms, "IDAT", idat, 0, idat.Length);
        WriteChunk(ms, "IEND", Array.Empty<byte>(), 0, 0);

        using var decoded = SKBitmap.Decode(ms.ToArray()) ?? throw new InvalidDataException("APNG: could not decode a frame");
        var result = new SKBitmap(new SKImageInfo(f.Width, f.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        result.Erase(SKColors.Transparent);
        using (var c = new SKCanvas(result))
        {
            c.DrawBitmap(decoded, 0, 0);
            c.Flush();
        }
        return result;
    }

    // ── PNG 청크 유틸 ─────────────────────────────────────────

    private static byte[] ReadAll(string path)
    {
        var len = new FileInfo(path).Length;
        if (len > MaxFileBytes) throw new InvalidDataException(Loc.T("err.fileTooLarge"));
        return File.ReadAllBytes(path);
    }

    private static bool StartsWithSignature(byte[] b)
    {
        if (b.Length < 8) return false;
        for (int i = 0; i < 8; i++) if (b[i] != Signature[i]) return false;
        return true;
    }

    private static List<Chunk> ParseChunks(byte[] b)
    {
        if (!StartsWithSignature(b)) throw new InvalidDataException("Not a PNG file");
        var list = new List<Chunk>();
        int pos = 8;
        while (pos + 12 <= b.Length)
        {
            int len = ReadInt(b, pos);
            if (len < 0 || pos + 12L + len > b.Length) break; // 잘린 파일: 읽은 데까지만
            string type = Encoding.ASCII.GetString(b, pos + 4, 4);
            list.Add(new Chunk(type, pos + 8, len));
            pos += 12 + len;
            if (type == "IEND") break;
        }
        return list;
    }

    private static int ReadInt(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    private static void WriteInt(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static void WriteChunk(Stream s, string type, byte[] data, int offset, int length)
    {
        var head = new byte[8];
        WriteInt(head, 0, length);
        Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        s.Write(head, 0, 8);
        s.Write(data, offset, length);
        uint crc = Crc32.Update(0xFFFFFFFFu, head, 4, 4);
        crc = Crc32.Update(crc, data, offset, length) ^ 0xFFFFFFFFu;
        var tail = new byte[4];
        WriteInt(tail, 0, unchecked((int)crc));
        s.Write(tail, 0, 4);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Update(uint crc, byte[] data, int offset, int length)
        {
            for (int i = offset; i < offset + length; i++) crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
