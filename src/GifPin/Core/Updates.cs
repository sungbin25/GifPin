using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GifPin.Core;

/// <summary>GitHub의 최신 릴리스를 확인 (설정에서 끌 수 있음). 보내는 정보는 일반 HTTP 요청뿐이다.</summary>
public static class Updates
{
    public const string ReleasesPage = "https://github.com/sungbin25/MotiNook/releases/latest";
    private const string LatestApi = "https://api.github.com/repos/sungbin25/MotiNook/releases/latest";

    public sealed record Result(string Version, string Url, bool IsNewer);

    /// <summary>최신 릴리스 확인. 실패하면 null (오프라인 등).</summary>
    public static async Task<Result?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"MotiNook/{currentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.GetAsync(LatestApi, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = ReleasesPage;
            string version = tag.TrimStart('v', 'V');
            return new Result(version, url, IsNewer(version, currentVersion));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Log.Info($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>"0.10.0" &gt; "0.9.3" 처럼 숫자로 비교. 읽을 수 없으면 false.</summary>
    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Pad(candidate), out var a) && Version.TryParse(Pad(current), out var b) && a > b;

    private static string Pad(string v)
    {
        v = v.Split('-', '+')[0].Trim();
        int dots = v.Split('.').Length - 1;
        return dots switch { 0 => v + ".0.0", 1 => v + ".0", _ => v };
    }
}
