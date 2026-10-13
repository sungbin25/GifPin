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
    public static void Save(string path, IReadOnlyList<SKBitmap> frames, IReadOnlyList<int> delaysMs)
    {
        if (frames.Count == 0) throw new ArgumentException("프레임이 없습니다.", nameof(frames));
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
            ?? throw new InvalidOperationException("WebP 인코딩 실패");
        byte[] file = data.ToArray();
        if (file.Length < 12 || Encoding.ASCII.GetString(file, 0, 4) != "RIFF" || Encoding.ASCII.GetString(file, 8, 4) != "WEBP")
            throw new InvalidDataException("예상치 못한 WebP 형식");

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
        if (outStream.Length == 0) throw new InvalidDataException("WebP 이미지 데이터를 찾지 못했습니다.");
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
