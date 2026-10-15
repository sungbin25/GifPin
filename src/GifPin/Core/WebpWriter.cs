using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;

namespace GifPin.Core;

/// <summary>
/// 프레임들을 애니메이션 WebP(무손실, 반투명 지원)로 저장한다.
/// SkiaSharp은 한 장짜리 WebP만 인코딩하므로, 프레임마다 인코딩한 뒤 비트스트림 청크를 꺼내
/// RIFF 컨테이너(VP8X + ANIM + ANMF…)로 직접 묶는다. (WebP Container Specification)
/// </summary>
public static class WebpWriter
{
    /// <summary>Writes one frame at a time so video length does not determine RAM usage.</summary>
    public sealed class Streaming : IDisposable
    {
        private readonly FileStream _file;
        private readonly int _width, _height;
        private bool _completed;
        public int FrameCount { get; private set; }

        public Streaming(string path, int width, int height)
        {
            if (width < 1 || height < 1 || width > 16383 || height > 16383) throw new ArgumentOutOfRangeException(nameof(width));
            _width = width; _height = height;
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            _file.Write(Encoding.ASCII.GetBytes("RIFF"));
            _file.Write(new byte[4]);
            _file.Write(Encoding.ASCII.GetBytes("WEBP"));
            var header = new byte[10]; header[0] = 0x12;
            Put24(header, 4, width - 1); Put24(header, 7, height - 1);
            WriteChunk(_file, "VP8X", header);
            WriteChunk(_file, "ANIM", new byte[6]); // infinite loop
        }

        public void Add(SKBitmap frame, int delayMs)
        {
            if (_completed || frame.Width != _width || frame.Height != _height) throw new InvalidOperationException("Invalid animation frame");
            byte[] data = EncodeFrame(frame);
            var header = new byte[16];
            Put24(header, 6, _width - 1); Put24(header, 9, _height - 1);
            Put24(header, 12, Math.Clamp(delayMs, 1, 0xFFFFFF)); header[15] = 2;
            int size = checked(header.Length + data.Length);
            if (_file.Length + size + 9 > uint.MaxValue) throw new IOException("Animation exceeds the WebP file size limit");
            _file.Write(Encoding.ASCII.GetBytes("ANMF"));
            _file.Write(BitConverter.GetBytes((uint)size));
            _file.Write(header); _file.Write(data);
            if ((size & 1) != 0) _file.WriteByte(0);
            FrameCount++;
        }

        public void Complete()
        {
            if (FrameCount == 0) throw new InvalidDataException(Loc.T("err.noFrames"));
            _file.Position = 4;
            _file.Write(BitConverter.GetBytes(checked((uint)(_file.Length - 8))));
            _file.Flush(); _completed = true;
        }

        public void Dispose() => _file.Dispose();
    }

    public static void Save(string path, IReadOnlyList<SKBitmap> frames, IReadOnlyList<int> delaysMs)
    {
        if (frames.Count == 0) throw new ArgumentException("No frames", nameof(frames));
        int w = frames[0].Width, h = frames[0].Height;

        using var body = new MemoryStream();
        // VP8X: 애니메이션(0x02) + 알파(0x10)
        var vp8x = new byte[10];
        vp8x[0] = 0x12;
        Put24(vp8x, 4, w - 1);
        Put24(vp8x, 7, h - 1);
        WriteChunk(body, "VP8X", vp8x);

        // ANIM: 배경색(BGRA 4바이트) + 반복 횟수(0 = 무한)
        WriteChunk(body, "ANIM", new byte[6]);

        for (int i = 0; i < frames.Count; i++)
        {
            byte[] bitstream = EncodeFrame(frames[i]);
            using var anmf = new MemoryStream();
            var header = new byte[16];
            Put24(header, 0, 0);                                  // X 오프셋 / 2
            Put24(header, 3, 0);                                  // Y 오프셋 / 2
            Put24(header, 6, frames[i].Width - 1);
            Put24(header, 9, frames[i].Height - 1);
            int delay = i < delaysMs.Count ? delaysMs[i] : 100;
            Put24(header, 12, Math.Clamp(delay, 1, 0xFFFFFF));    // 표시 시간(ms)
            header[15] = 0x02;                                    // 덮어쓰기(블렌딩 안 함), 폐기 안 함
            anmf.Write(header);
            anmf.Write(bitstream);
            WriteChunk(body, "ANMF", anmf.ToArray());
        }

        using var fs = File.Create(path);
        fs.Write(Encoding.ASCII.GetBytes("RIFF"));
        fs.Write(BitConverter.GetBytes((uint)(4 + body.Length)));
        fs.Write(Encoding.ASCII.GetBytes("WEBP"));
        body.Position = 0;
        body.CopyTo(fs);
    }

    /// <summary>한 장을 무손실 WebP로 인코딩하고, 이미지 데이터 청크(ALPH/VP8/VP8L)만 꺼낸다.</summary>
    private static byte[] EncodeFrame(SKBitmap frame)
    {
        using var pixmap = frame.PeekPixels();
        using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75))
            ?? throw new InvalidOperationException("WebP encoding failed");
        byte[] file = data.ToArray();
        if (file.Length < 12 || Encoding.ASCII.GetString(file, 0, 4) != "RIFF" || Encoding.ASCII.GetString(file, 8, 4) != "WEBP")
            throw new InvalidDataException("Unexpected WebP format");

        using var outStream = new MemoryStream();
        int pos = 12;
        while (pos + 8 <= file.Length)
        {
            string fourcc = Encoding.ASCII.GetString(file, pos, 4);
            int size = BitConverter.ToInt32(file, pos + 4);
            int total = 8 + size + (size & 1);
            if (fourcc is "ALPH" or "VP8 " or "VP8L")
                outStream.Write(file, pos, Math.Min(total, file.Length - pos));
            pos += total;
        }
        if (outStream.Length == 0) throw new InvalidDataException("WebP image data not found");
        return outStream.ToArray();
    }

    private static void WriteChunk(Stream s, string fourcc, byte[] payload)
    {
        s.Write(Encoding.ASCII.GetBytes(fourcc));
        s.Write(BitConverter.GetBytes((uint)payload.Length));
        s.Write(payload);
        if ((payload.Length & 1) == 1) s.WriteByte(0);
    }

    private static void Put24(byte[] b, int offset, int value)
    {
        b[offset] = (byte)value;
        b[offset + 1] = (byte)(value >> 8);
        b[offset + 2] = (byte)(value >> 16);
    }
}
