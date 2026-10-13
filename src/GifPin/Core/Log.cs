using System;
using System.IO;
using System.Text;

namespace GifPin.Core;

/// <summary>
/// 간단한 파일 로그: %AppData%\GifPin\logs\gifpin.log
/// 1MB를 넘으면 gifpin.1.log로 넘기고 새로 시작한다 (최대 2개 보관). 어느 스레드에서나 호출 가능.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Folder => Path.Combine(Store.DataDir, "logs");
    public static string FilePath => Path.Combine(Folder, "gifpin.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level.PadRight(5)).Append(' ').Append(message);
            if (ex != null) sb.AppendLine().Append("    ").Append(ex.ToString().Replace(Environment.NewLine, Environment.NewLine + "    "));
            sb.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = Path.Combine(Folder, "gifpin.1.log");
                    File.Copy(FilePath, old, overwrite: true);
                    File.Delete(FilePath);
                }
                File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { /* 로그 실패로 앱이 멈추면 안 됨 */ }
    }

    /// <summary>로그 끝부분 (진단 정보 복사용).</summary>
    public static string Tail(int maxLines)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return "";
                var lines = File.ReadAllLines(FilePath);
                int start = Math.Max(0, lines.Length - maxLines);
                return string.Join(Environment.NewLine, lines, start, lines.Length - start);
            }
        }
        catch { return ""; }
    }
}
