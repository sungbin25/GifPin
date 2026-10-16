using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace GifPin.Core;

/// <summary>
/// GifPin 에셋 팩: 사진 여러 장(+선택적으로 화면 배치)을 파일 하나로 주고받는다.
/// 파일은 확장자 .gifpin (속은 zip) — 다른 zip과 섞이지 않고, 파일 창에서 팩만 보이며, 두 번 누르면 GifPin으로 열림.
/// 구성: gifpin-pack.json (목록·배치) + items/ 폴더의 이미지 파일.
/// 예전에 .zip으로 내보낸 팩과 gifpin-pack.json이 없는 일반 .zip도 가져올 수 있다 (안의 이미지 파일을 모두 가져옴).
/// </summary>
public static class AssetPack
{
    public const string ManifestName = "gifpin-pack.json";
    public const string Extension = ".gifpin";

    /// <summary>저장 창: GifPin 팩만.</summary>
    public static string ExportFilter => Loc.T("pack.filter") + " (*.gifpin)|*.gifpin";
    /// <summary>열기 창: 처음엔 GifPin 팩만 보이고, 필요하면 예전 .zip 팩으로 바꿔 볼 수 있음.</summary>
    public static string ImportFilter => Loc.T("pack.filter") + " (*.gifpin)|*.gifpin|" + Loc.T("pack.filterOld") + " (*.zip)|*.zip";

    /// <summary>팩으로 가져올 파일인가 (.gifpin 또는 예전 .zip).</summary>
    public static bool IsPackFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(Extension, StringComparison.OrdinalIgnoreCase) || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>팩을 모아 두는 기본 폴더: 문서\GifPin Packs</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MotiNook Packs");
    private const long MaxEntryBytes = 100L * 1024 * 1024;
    private const long MaxTotalBytes = 500L * 1024 * 1024;
    private const int MaxEntries = 2000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public sealed class Manifest
    {
        public string Format { get; set; } = "gifpin-pack";
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "";
        public string? App { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public List<PackItem> Items { get; set; } = new();
        public List<PackLayout> Layout { get; set; } = new();
    }

    public sealed class PackItem
    {
        public string Id { get; set; } = "";
        public string File { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Favorite { get; set; }
        public bool Pixelated { get; set; }
    }

    /// <summary>화면 배치: 모니터 작업 영역 안에서의 상대 위치(GIF 가운데, 0~1).</summary>
    public sealed class PackLayout
    {
        public string Item { get; set; } = "";
        public int Monitor { get; set; } = 1;
        public double Fx { get; set; } = 0.9;
        public double Fy { get; set; } = 0.9;
        public double Scale { get; set; } = 1;
        public double Opacity { get; set; } = 1;
        public double Speed { get; set; } = 1;
        public bool FlipX { get; set; }
        public bool ClickThrough { get; set; } = true;
        public string MotionMode { get; set; } = "none";
        public double MotionSpeed { get; set; } = 120;
        public bool MotionFaceDirection { get; set; } = true;
        public bool MotionGravity { get; set; } = true;
        public bool JumpEnabled { get; set; } = true;
        public double JumpHeight { get; set; } = Motion.DefaultJumpHeight;
        public double JumpInterval { get; set; } = Motion.DefaultJumpInterval;
        public double JumpAngle { get; set; } = Motion.DefaultJumpAngle;
        public double SlideFriction { get; set; } = Motion.DefaultFriction;
        public string Layer { get; set; } = Layers.Top;
        public bool JumpAngleRandom { get; set; }
        public double JumpAngleRange { get; set; } = Motion.DefaultJumpAngleRange;
        public bool JumpHeightRandom { get; set; }
        public double JumpHeightMin { get; set; } = Motion.DefaultJumpHeightMin;
        public double JumpHeightMax { get; set; } = Motion.DefaultJumpHeightMax;
        public bool SlideAuto { get; set; }
        public bool PushDirRandom { get; set; } = true;
        public double PushAngle { get; set; }
        public bool PushForceRandom { get; set; } = true;
        public double PushForce { get; set; } = Motion.DefaultPushForce;
        public double PushForceMin { get; set; } = Motion.DefaultPushForceMin;
        public double PushForceMax { get; set; } = Motion.DefaultPushForceMax;
        public bool PushIntervalRandom { get; set; } = true;
        public double PushInterval { get; set; } = Motion.DefaultPushInterval;
        public double PushIntervalMin { get; set; } = Motion.DefaultPushIntervalMin;
        public double PushIntervalMax { get; set; } = Motion.DefaultPushIntervalMax;
        /// <summary>GIF 설정 전체 (효과 포함). 있으면 위 개별 값보다 우선. 이후 버전에서 늘어나는 설정도 그대로 담긴다.</summary>
        public ActiveItem? Settings { get; set; }
    }

    /// <summary>가져오기 결과: 임시 폴더에 풀린 파일과 메타데이터. 다 쓰면 Dispose로 임시 폴더 삭제.</summary>
    public sealed class Extracted : IDisposable
    {
        public string Name { get; init; } = "";
        public string TempDir { get; init; } = "";
        public List<(string path, PackItem item)> Files { get; } = new();
        public List<PackLayout> Layout { get; } = new();
        public int Skipped { get; set; }

        public void Dispose()
        {
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch { }
        }
    }

    // ── 내보내기 ─────────────────────────────────────────────

    public static void Export(string zipPath, string name, IReadOnlyList<PhotoItem> photos, IReadOnlyList<ActiveItem>? layout)
    {
        var manifest = new Manifest { Name = name, CreatedAt = DateTimeOffset.Now, App = "MotiNook " + typeof(AssetPack).Assembly.GetName().Version?.ToString(3) };
        var monitors = layout is { Count: > 0 } ? Monitors.List() : new List<MonitorInfo>();
        var tmp = zipPath + ".tmp";

        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var idByPhoto = new Dictionary<string, string>();
            int n = 1;
            foreach (var p in photos)
            {
                var src = Store.ResolvePath(p);
                if (!File.Exists(src)) continue;
                string id = "i" + n++;
                string entryName = $"items/{id}{Path.GetExtension(src).ToLowerInvariant()}";
                zip.CreateEntryFromFile(src, entryName, CompressionLevel.Optimal);
                manifest.Items.Add(new PackItem { Id = id, File = entryName, Name = p.Name, Favorite = p.Favorite, Pixelated = p.Pixelated });
                idByPhoto[p.Id] = id;
            }

            if (layout != null)
            {
                foreach (var a in layout)
                {
                    if (!idByPhoto.TryGetValue(a.PhotoId, out var id)) continue;
                    var p = photos.First(x => x.Id == a.PhotoId);
                    int w = (int)Math.Round(p.Width * a.Scale), h = (int)Math.Round(p.Height * a.Scale);
                    var mon = Monitors.Containing(monitors, a.X, a.Y, w, h) ?? Monitors.Primary(monitors);
                    double ww = Math.Max(1, mon.WorkRight - mon.WorkLeft), wh = Math.Max(1, mon.WorkBottom - mon.WorkTop);
                    manifest.Layout.Add(new PackLayout
                    {
                        Item = id,
                        Monitor = mon.Index,
                        Fx = Math.Round(Math.Clamp((a.X + w / 2.0 - mon.WorkLeft) / ww, 0, 1), 4),
                        Fy = Math.Round(Math.Clamp((a.Y + h / 2.0 - mon.WorkTop) / wh, 0, 1), 4),
                        Scale = a.Scale, Opacity = a.Opacity, Speed = a.Speed, FlipX = a.FlipX,
                        ClickThrough = a.ClickThrough, MotionMode = a.MotionMode, MotionSpeed = a.MotionSpeed,
                        MotionFaceDirection = a.MotionFaceDirection, MotionGravity = a.MotionGravity,
                        JumpEnabled = a.JumpEnabled, JumpHeight = a.JumpHeight, JumpInterval = a.JumpInterval, JumpAngle = a.JumpAngle,
                        SlideFriction = a.SlideFriction, Layer = Layers.Normalize(a.Layer),
                        JumpAngleRandom = a.JumpAngleRandom, JumpAngleRange = a.JumpAngleRange,
                        JumpHeightRandom = a.JumpHeightRandom, JumpHeightMin = a.JumpHeightMin, JumpHeightMax = a.JumpHeightMax,
                        SlideAuto = a.SlideAuto, PushDirRandom = a.PushDirRandom, PushAngle = a.PushAngle,
                        PushForceRandom = a.PushForceRandom, PushForce = a.PushForce, PushForceMin = a.PushForceMin, PushForceMax = a.PushForceMax,
                        PushIntervalRandom = a.PushIntervalRandom, PushInterval = a.PushInterval, PushIntervalMin = a.PushIntervalMin, PushIntervalMax = a.PushIntervalMax,
                        Settings = PackSettings(a, idByPhoto),
                    });
                }
            }

            var entry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using var es = entry.Open();
            JsonSerializer.Serialize(es, manifest, Json);
        }
        File.Move(tmp, zipPath, overwrite: true);
    }

    // ── 가져오기 ─────────────────────────────────────────────

    /// <summary>
    /// .zip을 안전하게 임시 폴더에 푼다. 압축 안의 경로는 쓰지 않고 순번 이름으로 저장하므로
    /// "../" 같은 경로 조작(zip slip)이 통하지 않는다. 지원 형식 이미지만, 크기 제한을 둔다.
    /// </summary>
    public static Extracted Extract(string zipPath)
    {
        var tempDir = Path.Combine(Store.DataDir, "tmp", "pack-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        Manifest? manifest = null;
        var result = new Extracted { TempDir = tempDir, Name = Path.GetFileNameWithoutExtension(zipPath) };

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > MaxEntries) throw new InvalidDataException(Loc.T("err.packTooManyFiles"));

            var manifestEntry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ManifestName, StringComparison.OrdinalIgnoreCase));
            if (manifestEntry != null)
            {
                if (manifestEntry.Length > 4 * 1024 * 1024) throw new InvalidDataException(Loc.T("err.packInvalid"));
                using var ms = manifestEntry.Open();
                manifest = JsonSerializer.Deserialize<Manifest>(ms, Json);
                if (manifest == null || manifest.Format != "gifpin-pack") throw new InvalidDataException(Loc.T("err.packInvalid"));
                if (manifest.Version > 1) throw new InvalidDataException(Loc.T("err.packNewer"));
            }

            var result2 = new Extracted { TempDir = tempDir, Name = string.IsNullOrWhiteSpace(manifest?.Name) ? result.Name : manifest!.Name };
            result = result2;

            // 가져올 (항목, 메타) 목록
            var wanted = new List<(ZipArchiveEntry entry, PackItem meta)>();
            if (manifest != null)
            {
                foreach (var item in manifest.Items)
                {
                    var e = zip.Entries.FirstOrDefault(x => x.FullName == item.File);
                    if (e == null || !Library.IsSupported(e.Name)) { result.Skipped++; continue; }
                    wanted.Add((e, item));
                }
                result.Layout.AddRange(manifest.Layout);
            }
            else
            {
                int n = 1;
                foreach (var e in zip.Entries.Where(x => x.Length > 0).OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    if (!Library.IsSupported(e.Name)) { if (!e.FullName.EndsWith("/")) result.Skipped++; continue; }
                    wanted.Add((e, new PackItem { Id = "z" + n++, Name = Path.GetFileNameWithoutExtension(e.Name) }));
                }
            }

            long total = 0;
            int index = 1;
            foreach (var (entry, meta) in wanted)
            {
                if (entry.Length > MaxEntryBytes) { result.Skipped++; continue; }
                total += entry.Length;
                if (total > MaxTotalBytes) throw new InvalidDataException(Loc.T("err.packTooLarge"));

                var dest = Path.Combine(tempDir, $"{index++:D4}{Path.GetExtension(entry.Name).ToLowerInvariant()}");
                using (var input = entry.Open())
                using (var output = File.Create(dest))
                {
                    CopyLimited(input, output, MaxEntryBytes);
                }
                if (string.IsNullOrWhiteSpace(meta.Name)) meta.Name = Path.GetFileNameWithoutExtension(entry.Name);
                result.Files.Add((dest, meta));
            }
            return result;
        }
        catch
        {
            result.Dispose();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            throw;
        }
    }

    /// <summary>압축 해제 크기를 실제 바이트로 다시 제한 (헤더의 크기 정보는 위조될 수 있음).</summary>
    private static void CopyLimited(Stream input, Stream output, long limit)
    {
        var buf = new byte[81920];
        long written = 0;
        int read;
        while ((read = input.Read(buf, 0, buf.Length)) > 0)
        {
            written += read;
            if (written > limit) throw new InvalidDataException(Loc.T("err.packTooLarge"));
            output.Write(buf, 0, read);
        }
    }

    /// <summary>배치 정보 → 실제 화면 좌표.</summary>
    /// <summary>팩에 담을 GIF 설정: 파티클 이미지 id를 팩 안의 항목 id로 바꿈.</summary>
    private static ActiveItem PackSettings(ActiveItem a, IReadOnlyDictionary<string, string> idByPhoto)
    {
        var s = a.Clone();
        s.FxParticleImages = string.Join(",", GifPin.Core.Fx.ParticleImageIds(a)
            .Select(id => idByPhoto.TryGetValue(id, out var packId) ? packId : null).OfType<string>());
        return s;
    }

    public static ActiveItem ToActive(PackLayout l, PhotoItem photo, IReadOnlyList<MonitorInfo> monitors)
    {
        var basic = ToActiveBasic(l, photo, monitors);
        if (l.Settings == null) return basic;
        // 전체 설정이 있으면 그대로 쓰되, 위치·식별자는 이 PC에 맞게
        var a = l.Settings.Clone();
        a.Id = basic.Id;
        a.PhotoId = basic.PhotoId;
        a.X = basic.X;
        a.Y = basic.Y;
        a.Scale = basic.Scale;
        a.Visible = true;
        a.Opacity = Math.Clamp(a.Opacity, 0.1, 1.0);
        a.Speed = Math.Clamp(a.Speed, 0.25, 4.0);
        a.MotionMode = Motion.Normalize(a.MotionMode);
        a.MotionSpeed = Math.Clamp(a.MotionSpeed, Motion.MinSpeed, Motion.MaxSpeed);
        a.Layer = Layers.Normalize(a.Layer);
        GifPin.Core.Fx.Normalize(a);
        return a;
    }

    private static ActiveItem ToActiveBasic(PackLayout l, PhotoItem photo, IReadOnlyList<MonitorInfo> monitors)
    {
        var mon = monitors.FirstOrDefault(m => m.Index == l.Monitor) ?? Monitors.Primary(monitors);
        double scale = Math.Clamp(l.Scale, 0.1, 4.0);
        int w = (int)Math.Round(photo.Width * scale), h = (int)Math.Round(photo.Height * scale);
        int x = (int)Math.Round(mon.WorkLeft + Math.Clamp(l.Fx, 0, 1) * (mon.WorkRight - mon.WorkLeft) - w / 2.0);
        int y = (int)Math.Round(mon.WorkTop + Math.Clamp(l.Fy, 0, 1) * (mon.WorkBottom - mon.WorkTop) - h / 2.0);
        return new ActiveItem
        {
            Id = Store.NewId("a"),
            PhotoId = photo.Id,
            X = x, Y = y, Scale = scale,
            Opacity = Math.Clamp(l.Opacity, 0.1, 1.0),
            Speed = Math.Clamp(l.Speed, 0.25, 4.0),
            FlipX = l.FlipX,
            ClickThrough = l.ClickThrough,
            MotionMode = Motion.Normalize(l.MotionMode),
            MotionSpeed = Math.Clamp(l.MotionSpeed, Motion.MinSpeed, Motion.MaxSpeed),
            MotionFaceDirection = l.MotionFaceDirection,
            MotionGravity = l.MotionGravity,
            JumpEnabled = l.JumpEnabled,
            JumpHeight = Math.Clamp(l.JumpHeight, Motion.MinJumpHeight, Motion.MaxJumpHeight),
            JumpInterval = Math.Clamp(l.JumpInterval, Motion.MinJumpInterval, Motion.MaxJumpInterval),
            JumpAngle = Math.Clamp(l.JumpAngle, -Motion.MaxJumpAngle, Motion.MaxJumpAngle),
            SlideFriction = Math.Clamp(l.SlideFriction, Motion.MinFriction, Motion.MaxFriction),
            Layer = Layers.Normalize(l.Layer),
            JumpAngleRandom = l.JumpAngleRandom,
            JumpAngleRange = Math.Clamp(l.JumpAngleRange, 0, Motion.MaxJumpAngle),
            JumpHeightRandom = l.JumpHeightRandom,
            JumpHeightMin = Math.Clamp(l.JumpHeightMin, Motion.MinJumpHeight, Motion.MaxJumpHeight),
            JumpHeightMax = Math.Clamp(l.JumpHeightMax, Motion.MinJumpHeight, Motion.MaxJumpHeight),
            SlideAuto = l.SlideAuto,
            PushDirRandom = l.PushDirRandom,
            PushAngle = ((l.PushAngle % 360) + 360) % 360,
            PushForceRandom = l.PushForceRandom,
            PushForce = Math.Clamp(l.PushForce, Motion.MinPushForce, Motion.MaxPushForce),
            PushForceMin = Math.Clamp(l.PushForceMin, Motion.MinPushForce, Motion.MaxPushForce),
            PushForceMax = Math.Clamp(l.PushForceMax, Motion.MinPushForce, Motion.MaxPushForce),
            PushIntervalRandom = l.PushIntervalRandom,
            PushInterval = Math.Clamp(l.PushInterval, Motion.MinPushInterval, Motion.MaxPushInterval),
            PushIntervalMin = Math.Clamp(l.PushIntervalMin, Motion.MinPushInterval, Motion.MaxPushInterval),
            PushIntervalMax = Math.Clamp(l.PushIntervalMax, Motion.MinPushInterval, Motion.MaxPushInterval),
        };
    }
}
