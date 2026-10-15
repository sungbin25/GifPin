using System;
using System.IO;
using System.Linq;
using System.Windows;
using GifPin.Core;
using Microsoft.Win32;

namespace GifPin.UI;

/// <summary>
/// .gifpin 파일을 GifPin 팩으로 등록 (현재 사용자만, 관리자 권한 없음).
/// 탐색기에서 팩 전용 아이콘으로 보이고, 두 번 누르면 GifPin이 열려 팩을 가져온다.
/// 설치 프로그램이 등록해 두면 그대로 두고, 등록이 없거나 가리키는 exe가 사라졌으면(포터블 등) 지금 exe로 등록한다.
/// </summary>
public static class FileAssociation
{
    public const string ProgId = "GifPin.Pack";
    private const string Classes = @"Software\Classes\";

    /// <summary>지금 등록된 열기 명령의 exe (없으면 null).</summary>
    public static string? RegisteredExe()
    {
        try
        {
            using var ext = Registry.CurrentUser.OpenSubKey(Classes + AssetPack.Extension);
            if (ext?.GetValue("") as string != ProgId) return null;
            using var cmd = Registry.CurrentUser.OpenSubKey(Classes + ProgId + @"\shell\open\command");
            if (cmd?.GetValue("") is not string command) return null;
            command = command.Trim();
            if (command.StartsWith('"'))
            {
                int end = command.IndexOf('"', 1);
                return end > 1 ? command[1..end] : null;
            }
            int space = command.IndexOf(' ');
            return space > 0 ? command[..space] : command;
        }
        catch { return null; }
    }

    public static string? RegisteredIcon()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Classes + ProgId + @"\DefaultIcon");
            return (key?.GetValue("") as string)?.Trim('"');
        }
        catch { return null; }
    }

    /// <summary>등록이 살아 있는가 (exe와 아이콘 파일이 실제로 있음).</summary>
    public static bool IsHealthy()
    {
        var exe = RegisteredExe();
        var icon = RegisteredIcon();
        return exe != null && File.Exists(exe) && icon != null && File.Exists(icon.Split(',')[0]);
    }

    /// <summary>등록이 없거나 깨졌으면 지금 실행 중인 exe로 등록. 등록했으면 true.</summary>
    public static bool EnsureRegistered()
    {
        try
        {
            if (IsHealthy()) return false;
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            Register(exe, WritePackIcon());
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Pack file association failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>앱에 들어 있는 팩 아이콘을 데이터 폴더에 꺼내 둠 (버전마다 다른 이름 → 아이콘 캐시 문제 없음).</summary>
    private static string WritePackIcon()
    {
        string dir = Path.Combine(Store.DataDir, "icons");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"GifPinPack-{AppController.AppVersion}.ico");
        if (!File.Exists(path))
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/GifPinPack.ico"))
                      ?? throw new InvalidOperationException("pack icon resource missing");
            using (var s = res.Stream)
            using (var f = File.Create(path))
                s.CopyTo(f);
            foreach (var old in Directory.GetFiles(dir, "GifPinPack-*.ico"))
                if (!old.Equals(path, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch { }
        }
        return path;
    }

    private static void Register(string exe, string icon)
    {
        using (var ext = Registry.CurrentUser.CreateSubKey(Classes + AssetPack.Extension))
        {
            ext.SetValue("", ProgId);
            using var owp = ext.CreateSubKey("OpenWithProgids");
            owp.SetValue(ProgId, "");
        }
        using (var prog = Registry.CurrentUser.CreateSubKey(Classes + ProgId))
        {
            prog.SetValue("", Loc.T("pack.filter"));
            using (var di = prog.CreateSubKey("DefaultIcon")) di.SetValue("", icon);
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        Native.SHChangeNotify(SHCNE_ASSOCCHANGED, 0x3000 /* SHCNF_IDLIST | FLUSHNOWAIT */, IntPtr.Zero, IntPtr.Zero);
        Log.Info($"Registered {AssetPack.Extension} packs → {exe}");
    }

    // ── 열기 요청 (탐색기에서 팩을 두 번 눌렀을 때) ────────────
    // 이미 GifPin이 실행 중이면 새로 뜬 쪽이 파일 경로를 여기에 적고 신호만 보낸 뒤 끝나고, 실행 중인 쪽이 읽어서 가져온다.

    private static string RequestFile => Path.Combine(Store.DataDir, "open-requests.txt");
    private static readonly object Gate = new();

    public static void QueueOpen(System.Collections.Generic.IEnumerable<string> paths)
    {
        lock (Gate)
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    Directory.CreateDirectory(Store.DataDir);
                    File.AppendAllLines(RequestFile, paths);
                    return;
                }
                catch (IOException) { System.Threading.Thread.Sleep(50); }
            }
    }

    public static System.Collections.Generic.List<string> TakeOpenRequests()
    {
        var list = new System.Collections.Generic.List<string>();
        lock (Gate)
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    if (!File.Exists(RequestFile)) return list;
                    foreach (var line in File.ReadAllLines(RequestFile))
                        if (line.Trim().Length > 0 && !list.Contains(line.Trim(), StringComparer.OrdinalIgnoreCase)) list.Add(line.Trim());
                    File.Delete(RequestFile);
                    return list;
                }
                catch (IOException) { System.Threading.Thread.Sleep(50); }
            }
        return list;
    }
}
