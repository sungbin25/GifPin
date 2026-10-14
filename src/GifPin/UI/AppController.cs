using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GifPin.Core;

namespace GifPin.UI;

public enum PanelMode { None, Photo, Active }

/// <summary>
/// 앱 전체 동작을 묶는 곳 (UI 스레드 전용).
/// 상태(AppState) ↔ 오버레이 엔진 ↔ 메인 창 / 편집 툴바 / 트레이를 연결한다.
/// </summary>
public sealed class AppController
{
    public static AppController Current { get; private set; } = null!;

    public AppState State { get; }
    public OverlayEngine Engine { get; } = new();
    public ObservableCollection<PhotoVM> VisiblePhotos { get; } = new();
    public ObservableCollection<ActiveVM> Actives { get; } = new();

    public string Filter { get; private set; } = "all";     // all | favorite | recent
    public string Sort { get; private set; } = "recent";    // recent | added | name
    public string Search { get; private set; } = "";

    public string? SelectedActiveId { get; private set; }
    public string? SelectedPhotoId { get; private set; }
    public PanelMode PanelMode { get; private set; } = PanelMode.None;
    public bool IsEditing { get; private set; }
    public bool IsQuitting { get; private set; }

    /// <summary>현재 연결된 모니터 (화면 구성이 바뀌면 갱신).</summary>
    public IReadOnlyList<MonitorInfo> MonitorList { get; private set; } = Array.Empty<MonitorInfo>();

    /// <summary>선택이 바뀜 → 속성 패널 다시 그리기.</summary>
    public event Action? SelectionChanged;
    /// <summary>선택된 GIF의 위치·크기가 바뀜 (드래그 확정, 실시간 포함).</summary>
    public event Action<ActiveItem>? GeometryChanged;
    /// <summary>안내 메시지 (메인 창 하단 토스트).</summary>
    public event Action<string>? Notice;
    /// <summary>모니터 구성이 바뀜.</summary>
    public event Action? MonitorsChanged;

    private readonly Dispatcher _ui;
    private readonly Dictionary<string, PhotoVM> _photoVMs = new();
    private MainWindow? _main;
    private EditToolbar? _toolbar;
    private TrayIcon? _tray;
    private List<ActiveItem>? _editSnapshot;
    private bool _mainWasVisible;

    public AppController(AppState state)
    {
        Current = this;
        State = state;
        _ui = Dispatcher.CurrentDispatcher;

        Engine.Changed += a => _ui.InvokeAsync(() => OnEngineChanged(a, commit: true));
        Engine.Moved += a => _ui.InvokeAsync(() => { OnEngineChanged(a, commit: false); SaveLazy(); });
        _lazySave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _lazySave.Tick += (_, _) => { _lazySave.Stop(); Save(); };
        Engine.LiveChanged += a => _ui.InvokeAsync(() => OnEngineChanged(a, commit: false));
        Engine.Clicked += id => _ui.InvokeAsync(() => SelectActive(id, flash: false));
        Engine.HotkeyPressed += id => _ui.InvokeAsync(() => OnHotkey(id));
        Engine.LoadFailed += (id, msg) => _ui.InvokeAsync(() => Notice?.Invoke(Loc.F("notice.loadFailed", msg)));
        Engine.RuleHiddenChanged += by => _ui.InvokeAsync(() => { RuleStateChanged?.Invoke(); _tray?.Refresh(); });
    }

    // ── 시작 / 종료 ──────────────────────────────────────────

    public void Start(bool showWindow)
    {
        Log.Info($"GifPin {AppVersion} starting · {Environment.OSVersion} · {State.Library.Count} photos, {State.Active.Count} on screen");
        Engine.Start();
        ApplyEngineSettings();

        // 파일이 사라진 사진을 쓰는 GIF는 화면에서 내림
        int missing = State.Active.RemoveAll(a => State.FindPhoto(a.PhotoId) is not PhotoItem p || !File.Exists(Store.ResolvePath(p)));
        if (missing > 0) Log.Warn($"Removed {missing} overlay(s) whose image file is missing");

        foreach (var a in State.Active) a.Normalize(); // 저장 파일의 값도 범위 검사
        MonitorList = Monitors.List();
        EnsureAllReachable(notify: false);
        foreach (var a in State.Active) SyncToEngine(a);

        foreach (var p in State.Library) _photoVMs[p.Id] = new PhotoVM(p);
        RebuildVisible();
        RebuildActives();
        if (State.Active.Count > 0) SelectActive(State.Active[0].Id, flash: false);

        _tray = new TrayIcon(this);
        State.Settings.StartWithWindows = Autostart.IsEnabled(); // 설치 프로그램에서 켰을 수도 있음
        if (State.Settings.StartWithWindows) Autostart.Apply(true); // 설치 위치가 바뀌었을 수 있어 경로 갱신

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        StartSceneClock();
        StartUpdateClock();

        if (showWindow) ShowMain();
        if (Engine.HotkeyFailures.Count > 0)
            _tray.ShowBalloon(Loc.T("tray.hotkeyFailTitle"), Loc.F("tray.hotkeyFailText", string.Join(", ", Engine.HotkeyFailures)));
        Save();
    }

    /// <summary>설정 전체를 엔진에 반영 (시작할 때, 설정 초기화·백업 복원 뒤).</summary>
    private void ApplyEngineSettings()
    {
        var s = State.Settings;
        Engine.SetPointerTiming(s.HoldToGrabMs, s.ResumeDelayMs);
        Engine.SetEffectsSettings(s.EffectsFps, s.PauseEffectsFullscreen, s.PauseEffectsOnBattery, s.AutoLightEffects, s.AutoLightCpuPercent);
        Engine.SetAllHidden(s.AllHidden);
        Engine.SetHideFromCapture(s.HideFromCapture);
        Engine.SetHoverFade(s.HoverFade, s.HoverFadeOpacity);
        Engine.SetEffectsOff(s.EffectsOff);
        Engine.SetAppRules(s.HideForApps, s.HideForAppsWhileRunning);
        ApplyPerformance();
        ApplyHotkeys();
    }

    private void ApplyPerformance()
    {
        var s = State.Settings;
        Engine.SetEffectsSettings(s.EffectsFps, s.PauseEffectsFullscreen, s.PauseEffectsOnBattery, s.AutoLightEffects, s.AutoLightCpuPercent);
        Engine.SetPerformance(s.MaxParticlesTotal, s.GifFpsCap, s.RenderQuality, s.MemoryBudgetMB, s.UnloadHidden, s.PauseOccluded, s.PauseWhenLocked);
    }

    // ── 성능 ─────────────────────────────────────────────────

    /// <summary>성능 모드 선택: 그 모드의 값으로 여러 설정을 한 번에 바꿈.</summary>
    public void SetPerfMode(string mode)
    {
        if (mode == PerfModes.Custom) { State.Settings.PerfMode = PerfModes.Custom; Save(); SettingsChanged?.Invoke(); return; }
        PerfModes.Apply(State.Settings, mode);
        State.Settings.Normalize();
        ApplyPerformance();
        Log.Info($"Performance mode: {State.Settings.PerfMode}");
        Save();
        SettingsChanged?.Invoke();
    }

    /// <summary>성능 설정 하나를 바꿈 → 어느 모드와도 같지 않으면 "직접 설정".</summary>
    public void SetPerfOption(Action<AppSettings> change)
    {
        change(State.Settings);
        State.Settings.Normalize();
        State.Settings.PerfMode = PerfModes.Detect(State.Settings);
        ApplyPerformance();
        Save();
        SettingsChanged?.Invoke();
    }

    public void SetLibraryPreview(bool on)
    {
        State.Settings.LibraryPreview = on;
        Save();
        SelectionChanged?.Invoke();
    }

    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuAt;

    /// <summary>성능 모니터: GifPin의 CPU·메모리, 디코딩한 GIF 메모리, GIF별 상태.</summary>
    public async Task<string> PerfReportAsync()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();
        var now = DateTime.UtcNow;
        var cpu = proc.TotalProcessorTime;
        double cpuPct = 0;
        if (_lastCpuAt != default)
        {
            double wall = (now - _lastCpuAt).TotalSeconds;
            if (wall > 0.2) cpuPct = (cpu - _lastCpuTime).TotalSeconds / wall / Environment.ProcessorCount * 100;
        }
        _lastCpuTime = cpu;
        _lastCpuAt = now;
        var (count, bytes) = await Engine.AnimStatsAsync();
        var sb = new StringBuilder();
        sb.AppendLine(Loc.F("perf.summary", cpuPct.ToString("0.0"), proc.WorkingSet64 / (1024 * 1024), proc.PrivateMemorySize64 / (1024 * 1024),
            count, bytes / (1024 * 1024)));
        int particles = 0;
        foreach (var a in State.Active)
        {
            var p = await Engine.ProbeAsync(a.Id);
            if (p == null) continue;
            particles += p.Particles;
            string status = p.Unloaded ? Loc.T("perf.unloaded")
                : !p.Shown ? Loc.T("perf.hidden")
                : p.Occluded ? Loc.T("perf.occluded")
                : Engine.SystemPaused ? Loc.T("perf.paused")
                : p.FramesPerSecond > 0 ? Loc.F("perf.fps", p.FramesPerSecond) : Loc.T("perf.idle");
            string name = State.FindPhoto(a.PhotoId)?.Name ?? "?";
            if (name.Length > 24) name = name[..24] + "…";
            sb.AppendLine($"  {State.Active.IndexOf(a) + 1}. {name} — {status}" + (p.Particles > 0 ? " · " + Loc.F("perf.particles", p.Particles) : ""));
        }
        sb.Append(Loc.F("perf.particlesTotal", particles, State.Settings.MaxParticlesTotal));
        return sb.ToString();
    }

    public static string AppVersion => typeof(AppController).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>현재 언어의 문구를 WPF 리소스(s.키)로 복사 → XAML의 DynamicResource가 바로 바뀜.</summary>
    public static void ApplyLanguageResources()
    {
        var res = Application.Current.Resources;
        foreach (var kv in Loc.All()) res["s." + kv.Key] = kv.Value;
    }

    public void SetLanguage(string lang)
    {
        State.Settings.Language = Loc.Normalize(lang);
        Loc.SetLanguage(State.Settings.Language);
        ApplyLanguageResources();
        foreach (var vm in _photoVMs.Values) vm.RefreshAll();
        foreach (var vm in Actives) vm.RefreshText();
        _tray?.Refresh();
        _toolbar?.RefreshSelection();
        Save();
    }

    // ── 테마 ─────────────────────────────────────────────────

    /// <summary>테마가 바뀜 (코드로 만든 화면 요소를 다시 만들 때).</summary>
    public event Action? ThemeChanged;

    public void SetTheme(string theme)
    {
        State.Settings.Theme = Themes.Normalize(theme);
        Themes.Apply(State.Settings.Theme);
        ThemeChanged?.Invoke();
        Save();
    }

    /// <summary>"시스템 설정 따르기"일 때 Windows의 라이트/다크가 바뀌면 따라감.</summary>
    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General || State.Settings.Theme != Themes.System) return;
        _ui.InvokeAsync(() =>
        {
            string before = Themes.Current;
            if (Themes.Resolve(Themes.System) == before) return;
            Themes.Apply(Themes.System);
            ThemeChanged?.Invoke();
        });
    }

    public void ShowMain()
    {
        if (IsEditing) return;
        if (_main == null)
        {
            _main = new MainWindow(this);
            _main.Closed += (_, _) => _main = null;
        }
        _main.ShowFromTray();
    }

    /// <summary>메인 창의 X 버튼: 종료하지 않고 트레이로.</summary>
    public void OnMainClosingToTray()
    {
        if (!State.Settings.TrayNoticeShown)
        {
            State.Settings.TrayNoticeShown = true;
            _tray?.ShowBalloon(Loc.T("tray.stillRunningTitle"), Loc.T("tray.stillRunningText"));
            Save();
        }
    }

    public void Quit()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        Log.Info("Quitting");
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (IsEditing) FinishEdit(save: true);
        if (_main != null) State.Settings.Window = _main.CurrentBounds();
        Store.SaveNow(State);
        Engine.Dispose();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    public void Save()
    {
        _lazySave?.Stop(); // 지금 전체를 저장하므로 미뤄 둔 저장은 필요 없음
        Store.SaveSoon(State);
    }

    private System.Windows.Threading.DispatcherTimer? _lazySave;

    /// <summary>움직이는 GIF의 위치처럼 자주 바뀌는 값: 최대 60초 모아서 저장 (종료할 때는 항상 저장됨).</summary>
    private void SaveLazy()
    {
        if (_lazySave != null && !_lazySave.IsEnabled) _lazySave.Start();
    }

    // ── 라이브러리 ───────────────────────────────────────────

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;

        // .zip은 에셋 팩으로 가져오기
        foreach (var zip in list.Where(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList())
        {
            list.Remove(zip);
            await ImportPackAsync(zip);
        }
        if (list.Count == 0) return;

        var existing = State.Library.ToList();
        bool copy = State.Settings.CopyOnAdd;
        Notice?.Invoke(Loc.T("notice.adding"));
        ImportResult r;
        try { r = await Task.Run(() => Library.Import(list, existing, copy)); }
        catch (Exception ex)
        {
            Log.Error("Import failed", ex);
            Notice?.Invoke(Loc.F("notice.addFailed", ex.Message));
            return;
        }

        foreach (var p in r.Added)
        {
            State.Library.Add(p);
            _photoVMs[p.Id] = new PhotoVM(p);
        }
        RebuildVisible();
        if (r.Added.Count > 0) SelectPhoto(r.Added[^1].Id);
        else if (r.Existing.Count > 0) SelectPhoto(r.Existing[^1].Id);
        Save();
        Log.Info($"Imported {r.Added.Count} file(s), {r.Existing.Count} existing, {r.Skipped} skipped");
        Notice?.Invoke(ImportSummary(r.Added.Count, r.Existing.Count, r.Skipped));
    }

    private static string ImportSummary(int added, int existing, int skipped)
    {
        var parts = new List<string>();
        if (added > 0) parts.Add(Loc.F("notice.addedN", added));
        if (existing > 0) parts.Add(Loc.F("notice.existingN", existing));
        if (skipped > 0) parts.Add(Loc.F("notice.skippedN", skipped));
        return parts.Count > 0 ? string.Join(" · ", parts) : Loc.T("notice.nothingToAdd");
    }

    /// <summary>배경 지우기·스프라이트 가져오기 등으로 새로 만든 사진을 등록.</summary>
    public void AddGeneratedPhoto(PhotoItem photo)
    {
        State.Library.Add(photo);
        _photoVMs[photo.Id] = new PhotoVM(photo);
        RebuildVisible();
        SelectPhoto(photo.Id);
        Save();
        Log.Info($"Added generated photo {photo.Id} ({photo.Width}x{photo.Height}, {photo.FrameCount} frames)");
    }

    public void RemovePhoto(string photoId)
    {
        var photo = State.FindPhoto(photoId);
        if (photo == null) return;
        foreach (var a in State.Active.Where(a => a.PhotoId == photoId).ToList()) RemoveActive(a.Id);
        State.Library.Remove(photo);
        _photoVMs.Remove(photoId);
        AnimationCache.Invalidate(Store.ResolvePath(photo));
        Images.ForgetThumb(photo);
        Library.DeleteFiles(photo);
        if (SelectedPhotoId == photoId)
        {
            SelectedPhotoId = null;
            if (PanelMode == PanelMode.Photo) PanelMode = SelectedActiveId != null ? PanelMode.Active : PanelMode.None;
        }
        RebuildVisible();
        SelectionChanged?.Invoke();
        Save();
    }

    public void ToggleFavorite(string photoId)
    {
        var p = State.FindPhoto(photoId);
        if (p == null) return;
        p.Favorite = !p.Favorite;
        if (_photoVMs.TryGetValue(photoId, out var vm)) vm.RefreshAll();
        if (Filter == "favorite") RebuildVisible();
        Save();
    }

    /// <summary>도트 그래픽: 크게 보여도 픽셀이 흐려지지 않게.</summary>
    public void SetPixelated(string photoId, bool on)
    {
        var p = State.FindPhoto(photoId);
        if (p == null || p.Pixelated == on) return;
        p.Pixelated = on;
        foreach (var a in State.Active.Where(a => a.PhotoId == photoId)) SyncToEngine(a);
        Save();
    }

    public void SetFilter(string filter) { Filter = filter; RebuildVisible(); }
    public void SetSort(string sort) { Sort = sort; RebuildVisible(); }
    public void SetSearch(string text) { Search = text.Trim(); RebuildVisible(); }

    public int FavoriteCount => State.Library.Count(p => p.Favorite);

    public void RebuildVisible()
    {
        IEnumerable<PhotoItem> q = State.Library;
        if (Filter == "favorite") q = q.Where(p => p.Favorite);
        if (Filter == "recent") q = q.Where(p => p.LastUsedAt != null);
        if (Search.Length > 0) q = q.Where(p => p.Name.Contains(Search, StringComparison.OrdinalIgnoreCase));

        q = (Filter == "recent" ? "recent" : Sort) switch
        {
            "name" => q.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            "added" => q.OrderByDescending(p => p.AddedAt),
            _ => q.OrderByDescending(p => p.LastUsedAt ?? DateTimeOffset.MinValue).ThenByDescending(p => p.AddedAt),
        };

        var inUse = State.Active.Select(a => a.PhotoId).ToHashSet();
        VisiblePhotos.Clear();
        foreach (var p in q)
        {
            if (!_photoVMs.TryGetValue(p.Id, out var vm)) _photoVMs[p.Id] = vm = new PhotoVM(p);
            vm.InUse = inUse.Contains(p.Id);
            vm.IsSelected = PanelMode == PanelMode.Photo && p.Id == SelectedPhotoId;
            vm.CanReplace = SelectedActiveId != null;
            VisiblePhotos.Add(vm);
        }
    }

    // ── 에셋 팩 ──────────────────────────────────────────────

    public async Task ImportPackAsync(string zipPath)
    {
        Notice?.Invoke(Loc.T("notice.packImporting"));
        AssetPack.Extracted? pack = null;
        try
        {
            pack = await Task.Run(() => AssetPack.Extract(zipPath));
            var files = pack.Files.Select(f => f.path).ToList();
            var existing = State.Library.ToList();
            var r = await Task.Run(() => Library.Import(files, existing, copy: true));

            // 임시 파일 경로 → 팩 안의 항목 정보
            var metaByPath = pack.Files.ToDictionary(f => Path.GetFullPath(f.path), f => f.item, StringComparer.OrdinalIgnoreCase);
            var photoByItemId = new Dictionary<string, PhotoItem>();
            foreach (var p in r.Added)
            {
                if (p.SourcePath != null && metaByPath.TryGetValue(p.SourcePath, out var meta))
                {
                    p.Name = string.IsNullOrWhiteSpace(meta.Name) ? p.Name : meta.Name.Trim();
                    p.Favorite = meta.Favorite;
                    p.Pixelated = meta.Pixelated;
                    photoByItemId[meta.Id] = p;
                }
                p.SourcePath = null; // 임시 경로는 기억할 필요 없음
                State.Library.Add(p);
                _photoVMs[p.Id] = new PhotoVM(p);
            }
            RebuildVisible();
            Save();
            Log.Info($"Pack '{pack.Name}' imported: {r.Added.Count} photos, {pack.Layout.Count} layout entries");

            int skipped = pack.Skipped + r.Skipped;
            string summary = Loc.F("notice.packImported", pack.Name) + " · " + ImportSummary(r.Added.Count, 0, skipped);
            Notice?.Invoke(summary);

            var layout = pack.Layout.Where(l => photoByItemId.ContainsKey(l.Item)).ToList();
            if (layout.Count > 0)
            {
                string q = Loc.F("pack.applyLayoutQuestion", layout.Count);
                var answer = _main != null && _main.IsVisible
                    ? MessageBox.Show(_main, q, "GifPin", MessageBoxButton.YesNo, MessageBoxImage.Question)
                    : MessageBox.Show(q, "GifPin", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    MonitorList = Monitors.List();
                    foreach (var l in layout)
                    {
                        var p = photoByItemId[l.Item];
                        var a = AssetPack.ToActive(l, p, MonitorList);
                        // 팩 안의 파티클 이미지 id → 이 PC 라이브러리 id
                        a.FxParticleImages = string.Join(",", Fx.ParticleImageIds(a)
                            .Select(id => photoByItemId.TryGetValue(id, out var ph) ? ph.Id : null).OfType<string>());
                        State.Active.Add(a);
                        p.LastUsedAt = DateTimeOffset.Now;
                        SyncToEngine(a);
                    }
                    RebuildActives();
                    RebuildVisible();
                    SelectActive(State.Active[^1].Id, flash: false);
                    Save();
                }
            }
            else if (r.Added.Count > 0) SelectPhoto(r.Added[^1].Id);
        }
        catch (Exception ex)
        {
            Log.Error($"Pack import failed: {zipPath}", ex);
            Notice?.Invoke(Loc.F("notice.packFailed", ex.Message));
        }
        finally
        {
            pack?.Dispose();
        }
    }

    /// <summary>photos를 팩으로 내보낸다. withLayout이면 그 사진들의 현재 화면 배치도 함께.</summary>
    public async Task ExportPackAsync(string zipPath, string name, IReadOnlyList<PhotoItem> photos, bool withLayout)
    {
        if (photos.Count == 0)
        {
            Notice?.Invoke(Loc.T("notice.packEmpty"));
            return;
        }
        var layout = withLayout ? State.Active.Where(a => photos.Any(p => p.Id == a.PhotoId)).Select(a => a.Clone()).ToList() : null;
        if (layout != null)
        {
            // 화면 배치에 쓰인 파티클 이미지도 함께 담음
            var extra = layout.SelectMany(a => ParticlePhotos(a)).Where(p => photos.All(x => x.Id != p.Id)).Distinct().ToList();
            if (extra.Count > 0) photos = photos.Concat(extra).ToList();
        }
        try
        {
            await Task.Run(() => AssetPack.Export(zipPath, name, photos, layout));
            Log.Info($"Pack exported: {zipPath} ({photos.Count} photos, layout {layout?.Count ?? 0})");
            Notice?.Invoke(Loc.F("notice.packExported", photos.Count, Path.GetFileName(zipPath)));
        }
        catch (Exception ex)
        {
            Log.Error($"Pack export failed: {zipPath}", ex);
            Notice?.Invoke(Loc.F("notice.packFailed", ex.Message));
        }
    }

    // ── 화면 배치 ────────────────────────────────────────────

    public void AddToScreen(string photoId)
    {
        var p = State.FindPhoto(photoId);
        if (p == null) return;
        int maxSide = Math.Max(1, Math.Max(p.Width, p.Height));
        int limit = State.Settings.NewItemSize;
        double scale = maxSide > limit ? (double)limit / maxSide : 1.0;
        if (p.Pixelated && maxSide < 96) scale = Math.Floor(160.0 / maxSide); // 작은 도트는 정수배로 키움
        scale = Math.Clamp(scale, 0.1, 4.0);
        int w = (int)Math.Round(p.Width * scale), h = (int)Math.Round(p.Height * scale);
        var mon = Monitors.Primary(MonitorList.Count > 0 ? MonitorList : Monitors.List());
        int offset = (State.Active.Count % 6) * 28;
        var a = new ActiveItem();
        if (State.Settings.NewItemTemplate is ActiveItem template) a.CopyStyleFrom(template); // 새 GIF 기본값 (움직임·효과·층…)
        a.Id = Store.NewId("a");
        a.PhotoId = p.Id;
        a.Scale = Math.Round(scale, 3);
        a.X = mon.WorkRight - w - 24 - offset;
        a.Y = mon.WorkBottom - h - 24 - offset;
        a.FxParticleImages = string.Join(",", Fx.ParticleImageIds(a).Where(id => State.FindPhoto(id) != null)); // 지워진 사진은 뺌
        State.Active.Add(a);
        p.LastUsedAt = DateTimeOffset.Now;
        SyncToEngine(a);
        RebuildActives();
        SelectActive(a.Id, flash: true);
        RebuildVisible();
        Save();
        Notice?.Invoke(Loc.F("notice.addedToScreen", p.Name));
    }

    /// <summary>선택한 GIF의 사진만 바꾼다. 위치와 화면에 보이는 크기(긴 변 기준)는 유지.</summary>
    public void ReplaceActive(string activeId, string photoId)
    {
        var a = State.Active.FirstOrDefault(x => x.Id == activeId);
        var p = State.FindPhoto(photoId);
        if (a == null || p == null) return;
        var old = State.FindPhoto(a.PhotoId);
        if (old != null)
        {
            double shownMax = Math.Max(old.Width, old.Height) * a.Scale;
            a.Scale = Math.Round(Math.Clamp(shownMax / Math.Max(1, Math.Max(p.Width, p.Height)), 0.1, 4.0), 3);
        }
        a.PhotoId = p.Id;
        p.LastUsedAt = DateTimeOffset.Now;
        SyncToEngine(a);
        RebuildActives();
        SelectActive(a.Id, flash: true);
        RebuildVisible();
        Save();
        Notice?.Invoke(Loc.F("notice.replaced", p.Name));
    }

    public void RemoveActive(string activeId)
    {
        var a = State.Active.FirstOrDefault(x => x.Id == activeId);
        if (a == null) return;
        State.Active.Remove(a);
        Engine.Remove(activeId);
        if (SelectedActiveId == activeId)
        {
            SelectedActiveId = null;
            if (PanelMode == PanelMode.Active) PanelMode = PanelMode.None;
            Engine.Select(null);
        }
        RebuildActives();
        RebuildVisible();
        SelectionChanged?.Invoke();
        _toolbar?.RefreshList();
        Save();
    }

    /// <summary>속성 패널·툴바에서 값 변경.</summary>
    public void UpdateActive(string activeId, Action<ActiveItem> change)
    {
        var a = State.Active.FirstOrDefault(x => x.Id == activeId);
        if (a == null) return;
        change(a);
        a.Normalize();
        SyncToEngine(a);
        RefreshActiveVM(a);
        GeometryChanged?.Invoke(a.Clone());
        Save();
    }

    /// <summary>위치를 강제로 옮김 (움직이는 GIF도 그 자리로).</summary>
    public void Relocate(string activeId, int x, int y)
    {
        var a = State.Active.FirstOrDefault(i => i.Id == activeId);
        if (a == null) return;
        a.X = x;
        a.Y = y;
        Engine.Relocate(a.Id, x, y);
        RefreshActiveVM(a);
        GeometryChanged?.Invoke(a.Clone());
        Save();
    }

    public void SelectActive(string? activeId, bool flash)
    {
        SelectedActiveId = activeId;
        var a = State.Active.FirstOrDefault(x => x.Id == activeId);
        if (a != null)
        {
            SelectedPhotoId = a.PhotoId;
            PanelMode = PanelMode.Active;
            if (flash) Engine.Flash(a.Id);
        }
        else if (PanelMode == PanelMode.Active) PanelMode = PanelMode.None;
        Engine.Select(activeId);

        foreach (var vm in Actives) vm.IsSelected = vm.Id == activeId;
        foreach (var vm in _photoVMs.Values)
        {
            vm.CanReplace = activeId != null;
            vm.IsSelected = PanelMode == PanelMode.Photo && vm.Id == SelectedPhotoId;
        }
        SelectionChanged?.Invoke();
        _toolbar?.RefreshSelection();
    }

    public void SelectPhoto(string photoId)
    {
        SelectedPhotoId = photoId;
        PanelMode = PanelMode.Photo;
        foreach (var vm in _photoVMs.Values) vm.IsSelected = vm.Id == photoId;
        SelectionChanged?.Invoke();
    }

    public ActiveItem? SelectedActive => State.Active.FirstOrDefault(x => x.Id == SelectedActiveId);

    public void SetAllHidden(bool hidden)
    {
        State.Settings.AllHidden = hidden;
        Engine.SetAllHidden(hidden);
        _main?.RefreshHiddenToggle();
        _tray?.Refresh();
        Save();
    }

    private void SyncToEngine(ActiveItem a)
    {
        var p = State.FindPhoto(a.PhotoId);
        if (p != null) Engine.Sync(a, Store.ResolvePath(p), p.Pixelated, ParticleImagePaths(a), p.Width, p.Height);
    }

    /// <summary>내 이미지 파티클로 고른 사진들의 파일 경로 (파티클을 안 쓰면 빈 목록 → 메모리 해제).</summary>
    private List<string> ParticleImagePaths(ActiveItem a)
    {
        if (!Fx.UsesParticleImages(a)) return new List<string>();
        return Fx.ParticleImageIds(a)
            .Select(id => State.FindPhoto(id))
            .Where(ph => ph != null)
            .Select(ph => Store.ResolvePath(ph!))
            .Where(File.Exists)
            .ToList();
    }

    /// <summary>파티클 이미지로 고른 사진들 (지워진 사진은 빠짐).</summary>
    public List<PhotoItem> ParticlePhotos(ActiveItem a) =>
        Fx.ParticleImageIds(a).Select(id => State.FindPhoto(id)).OfType<PhotoItem>().ToList();

    private void RebuildActives()
    {
        Actives.Clear();
        int i = 1;
        foreach (var a in State.Active)
            Actives.Add(new ActiveVM(a, State.FindPhoto(a.PhotoId), i++) { IsSelected = a.Id == SelectedActiveId });
    }

    private void RefreshActiveVM(ActiveItem a)
    {
        int i = State.Active.IndexOf(a);
        var vm = Actives.FirstOrDefault(v => v.Id == a.Id);
        vm?.Update(a, State.FindPhoto(a.PhotoId), i + 1);
    }

    private void OnEngineChanged(ActiveItem changed, bool commit)
    {
        var a = State.Active.FirstOrDefault(x => x.Id == changed.Id);
        if (a == null) return;
        a.X = changed.X;
        a.Y = changed.Y;
        a.Scale = changed.Scale;
        RefreshActiveVM(a);
        GeometryChanged?.Invoke(a.Clone());
        if (commit) Save();
    }

    public (int w, int h) ShownSize(ActiveItem a)
    {
        var p = State.FindPhoto(a.PhotoId);
        return p == null ? (1, 1) : ((int)Math.Round(p.Width * a.Scale), (int)Math.Round(p.Height * a.Scale));
    }

    // ── 다중 모니터 ──────────────────────────────────────────

    public MonitorInfo? MonitorOf(ActiveItem a)
    {
        var (w, h) = ShownSize(a);
        return Monitors.Containing(MonitorList, a.X, a.Y, w, h);
    }

    /// <summary>선택한 GIF를 다른 모니터로 옮긴다 (작업 영역 안 상대 위치 유지).</summary>
    public void MoveSelectedToMonitor(int monitorIndex)
    {
        var a = SelectedActive;
        var to = MonitorList.FirstOrDefault(m => m.Index == monitorIndex);
        if (a == null || to == null) return;
        var (w, h) = ShownSize(a);
        var (x, y) = Monitors.MoveTo(MonitorOf(a), to, a.X, a.Y, w, h);
        Relocate(a.Id, x, y);
        Engine.Flash(a.Id);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _ui.InvokeAsync(async () =>
    {
        await Task.Delay(800); // 모니터 정보가 다 갱신될 때까지 잠깐 대기
        MonitorList = Monitors.List();
        Log.Info($"Display settings changed: {MonitorList.Count} monitor(s)");
        EnsureAllReachable(notify: true);
        MonitorsChanged?.Invoke();
        SelectionChanged?.Invoke();
    });

    /// <summary>모니터를 빼는 등으로 보이지 않게 된 GIF를 주 모니터 오른쪽 아래로 옮긴다.</summary>
    private void EnsureAllReachable(bool notify)
    {
        if (MonitorList.Count == 0) return;
        var primary = Monitors.Primary(MonitorList);
        int moved = 0;
        foreach (var a in State.Active)
        {
            var (w, h) = ShownSize(a);
            if (Monitors.IsReachable(MonitorList, a.X, a.Y, w, h)) continue;
            var (x, y) = Monitors.MoveTo(null, primary, a.X, a.Y, w, h);
            a.X = x - 24 * moved % 120;
            a.Y = y - 24 * moved % 120;
            Engine.Relocate(a.Id, a.X, a.Y);
            moved++;
        }
        if (moved == 0) return;
        Log.Warn($"Moved {moved} off-screen overlay(s) to the primary monitor");
        RebuildActives();
        Save();
        if (notify) Notice?.Invoke(Loc.F("notice.movedToPrimary", moved));
        if (notify) _tray?.ShowBalloon("GifPin", Loc.F("notice.movedToPrimary", moved));
    }

    // ── 복구 도구 ────────────────────────────────────────────

    /// <summary>모든 GIF를 주 모니터 가운데로 모은다 (조금씩 어긋나게).</summary>
    public void GatherAllToCenter()
    {
        MonitorList = Monitors.List();
        var mon = Monitors.Primary(MonitorList);
        int cx = (mon.WorkLeft + mon.WorkRight) / 2, cy = (mon.WorkTop + mon.WorkBottom) / 2;
        int i = 0, n = State.Active.Count;
        foreach (var a in State.Active)
        {
            var (w, h) = ShownSize(a);
            int off = (i - (n - 1) / 2) * 32;
            Relocate(a.Id, cx - w / 2 + off, cy - h / 2 + off);
            i++;
        }
        Log.Info($"Recovery: gathered {n} overlay(s) to center");
        Notice?.Invoke(Loc.F("notice.gathered", n));
    }

    /// <summary>숨긴 GIF와 "모두 숨기기"를 모두 해제.</summary>
    public void ShowAll()
    {
        foreach (var a in State.Active.Where(a => !a.Visible).ToList()) UpdateActive(a.Id, x => x.Visible = true);
        if (State.Settings.AllHidden) SetAllHidden(false);
        RebuildActives();
        Log.Info("Recovery: show all");
        Notice?.Invoke(Loc.T("notice.shownAll"));
    }

    /// <summary>클릭 통과를 모두 끔 → GIF를 마우스로 잡을 수 있게.</summary>
    public void DisableClickThroughAll()
    {
        foreach (var a in State.Active.Where(a => a.ClickThrough).ToList()) UpdateActive(a.Id, x => x.ClickThrough = false);
        Log.Info("Recovery: click-through disabled for all");
        Notice?.Invoke(Loc.T("notice.clickThroughOff"));
        SelectionChanged?.Invoke();
    }

    /// <summary>문제 신고용 진단 정보.</summary>
    public string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"GifPin {AppVersion}");
        sb.AppendLine($"OS: {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})");
        sb.AppendLine($".NET: {Environment.Version}");
        sb.AppendLine($"Language: {Loc.Language}");
        sb.AppendLine($"Data: {Store.DataDir}");
        sb.AppendLine($"Log: {Log.FilePath}");
        sb.AppendLine($"Library: {State.Library.Count} photos · On screen: {State.Active.Count} · All hidden: {State.Settings.AllHidden}");
        sb.AppendLine($"Effects: {State.Settings.EffectsFps} fps target · frame pacing {Engine.FramePacing} · paused for full screen: {State.Settings.PauseEffectsFullscreen}, battery: {State.Settings.PauseEffectsOnBattery}");
        sb.AppendLine($"Busy-PC saver: {(State.Settings.AutoLightEffects ? $"on at {State.Settings.AutoLightCpuPercent}%" : "off")} · now {(Engine.LightMode ? "particles off" : "normal")} · other apps CPU {Engine.OtherCpuPercent:0}%");
        foreach (var m in MonitorList)
            sb.AppendLine($"Monitor {m.Index}{(m.Primary ? " (primary)" : "")}: {m.Left},{m.Top} {m.Width}x{m.Height} work {m.WorkLeft},{m.WorkTop}-{m.WorkRight},{m.WorkBottom}");
        foreach (var a in State.Active)
        {
            var p = State.FindPhoto(a.PhotoId);
            sb.AppendLine($"  [{a.Id}] {p?.Name} {p?.Width}x{p?.Height} pos {a.X},{a.Y} scale {a.Scale} visible {a.Visible} click-through {a.ClickThrough} motion {a.MotionMode}");
        }
        if (Engine.HotkeyFailures.Count > 0) sb.AppendLine("Hotkey failures: " + string.Join(", ", Engine.HotkeyFailures));
        sb.AppendLine("--- recent log ---");
        sb.AppendLine(Log.Tail(40));
        return sb.ToString();
    }

    // ── 편집 모드 ────────────────────────────────────────────

    public void ToggleEdit()
    {
        if (IsEditing) FinishEdit(save: true);
        else EnterEdit();
    }

    public void EnterEdit()
    {
        if (IsEditing) return;
        if (State.Active.Count == 0)
        {
            ShowMain();
            Notice?.Invoke(Loc.T("notice.noGifForEdit"));
            return;
        }
        IsEditing = true;
        _editSnapshot = State.Active.Select(a => a.Clone()).ToList();
        if (SelectedActiveId == null) SelectActive(State.Active[0].Id, flash: false);

        _mainWasVisible = _main?.IsVisible == true;
        _main?.Hide();
        Engine.SetEditMode(true);
        _toolbar = new EditToolbar(this);
        _toolbar.Closed += (_, _) => _toolbar = null;
        _toolbar.Show();
        _tray?.Refresh();
    }

    /// <summary>save=false면 편집 모드에 들어오기 전 상태로 모두 되돌린다.</summary>
    public void FinishEdit(bool save)
    {
        if (!IsEditing) return;
        IsEditing = false;
        Engine.SetEditMode(false);

        if (!save && _editSnapshot != null)
        {
            foreach (var a in State.Active.Where(a => _editSnapshot.All(s => s.Id != a.Id)).ToList()) RemoveActive(a.Id);
            State.Active.Clear();
            State.Active.AddRange(_editSnapshot.Select(s => s.Clone()));
            foreach (var a in State.Active)
            {
                SyncToEngine(a);
                Engine.Relocate(a.Id, a.X, a.Y);
            }
            RebuildActives();
            RebuildVisible();
        }
        _editSnapshot = null;

        var tb = _toolbar;
        _toolbar = null;
        tb?.Close();
        SelectionChanged?.Invoke();
        Save();
        _tray?.Refresh();
        if (_mainWasVisible) ShowMain();
    }

    public void NudgeSelected(int dx, int dy)
    {
        if (SelectedActiveId != null) Engine.Nudge(SelectedActiveId, dx, dy);
    }

    public void ScaleSelected(double factor)
    {
        if (SelectedActive is ActiveItem a) ScaleSelectedTo(a.Scale * factor);
    }

    /// <summary>선택한 GIF의 크기를 가운데 기준으로 바꾼다.</summary>
    public void ScaleSelectedTo(double scale)
    {
        var a = SelectedActive;
        if (a == null) return;
        var p = State.FindPhoto(a.PhotoId);
        if (p == null) return;
        double cx = a.X + p.Width * a.Scale / 2, cy = a.Y + p.Height * a.Scale / 2;
        double s = Math.Round(Math.Clamp(scale, 0.1, 4.0), 3);
        if (Math.Abs(s - a.Scale) < 0.0005) return;
        UpdateActive(a.Id, x => x.Scale = s);
        if (!Motion.IsAutonomous(a.MotionMode))
            Relocate(a.Id, (int)Math.Round(cx - p.Width * s / 2), (int)Math.Round(cy - p.Height * s / 2));
    }

    // ── 기타 ─────────────────────────────────────────────────

    private void OnHotkey(int id)
    {
        switch (Hotkeys.ActionOf(id))
        {
            case Hotkeys.Edit: ToggleEdit(); break;
            case Hotkeys.Hide: SetAllHidden(!State.Settings.AllHidden); break;
            case Hotkeys.Quit: Quit(); break;
            case Hotkeys.ClickThrough: ToggleClickThroughAll(); break;
            case Hotkeys.Effects: SetEffectsOff(!State.Settings.EffectsOff); break;
            case Hotkeys.NextScene: ApplyNextScene(); break;
        }
    }

    // ── 단축키 ───────────────────────────────────────────────

    /// <summary>단축키 표시 글자 (설정에서 지정한 값, 없으면 빈 글자).</summary>
    public string HotkeyText(string action) => State.Settings.Hotkeys.TryGetValue(action, out var t) ? t : "";

    /// <summary>단축키가 바뀜 (설정 화면·트레이 갱신용).</summary>
    public event Action? HotkeysChanged;

    private void ApplyHotkeys()
    {
        var failures = Engine.SetHotkeys(new Dictionary<string, string>(State.Settings.Hotkeys));
        foreach (var f in failures) Log.Warn($"Hotkey unavailable: {f}");
    }

    /// <summary>동작의 단축키를 바꾼다 (빈 글자 = 없음). 다른 동작이 이미 쓰는 조합이면 false.</summary>
    public bool SetHotkey(string action, string text)
    {
        text = Hotkeys.TryParse(text, out uint m, out uint vk) ? Hotkeys.Format(m, vk) : "";
        if (text.Length > 0 && State.Settings.Hotkeys.Any(kv => kv.Key != action && kv.Value == text)) return false;
        State.Settings.Hotkeys[action] = text;
        ApplyHotkeys();
        Save();
        _tray?.Refresh();
        HotkeysChanged?.Invoke();
        return true;
    }

    /// <summary>단축키를 새로 누르는 동안: 지금 등록된 단축키가 그 키를 가로채지 않게 잠시 해제.</summary>
    public void SuspendHotkeys() => Engine.SetHotkeys(new Dictionary<string, string>());
    public void ResumeHotkeys() => ApplyHotkeys();

    public void ResetHotkeys()
    {
        State.Settings.Hotkeys = Hotkeys.Normalize(null);
        ApplyHotkeys();
        Save();
        _tray?.Refresh();
        HotkeysChanged?.Invoke();
    }

    /// <summary>클릭 통과 켜기/끄기 한 번에: 하나라도 꺼져 있으면 모두 켜고, 모두 켜져 있으면 모두 끔(→ 마우스로 잡을 수 있게 테두리 깜빡임).</summary>
    public void ToggleClickThroughAll()
    {
        if (State.Active.Count == 0) return;
        bool turnOn = State.Active.Any(a => !a.ClickThrough);
        foreach (var a in State.Active.ToList()) UpdateActive(a.Id, x => x.ClickThrough = turnOn);
        if (!turnOn) foreach (var a in State.Active) Engine.Flash(a.Id);
        Log.Info($"Click-through {(turnOn ? "on" : "off")} for all");
        Notice?.Invoke(Loc.T(turnOn ? "notice.clickThroughAllOn" : "notice.clickThroughOff"));
        SelectionChanged?.Invoke();
    }

    // ── 화면 녹화·공유 / 마우스 흐리게 / 효과 끄기 ───────────────

    public event Action? SettingsChanged;

    public void SetHideFromCapture(bool on)
    {
        State.Settings.HideFromCapture = on;
        Engine.SetHideFromCapture(on);
        Save();
    }

    public void SetHoverFade(bool on, int opacityPercent)
    {
        State.Settings.HoverFade = on;
        State.Settings.HoverFadeOpacity = Math.Clamp(opacityPercent, 0, 90);
        Engine.SetHoverFade(on, State.Settings.HoverFadeOpacity);
        Save();
    }

    public void SetEffectsOff(bool off)
    {
        State.Settings.EffectsOff = off;
        Engine.SetEffectsOff(off);
        Log.Info(off ? "All effects turned off" : "Effects turned back on");
        Notice?.Invoke(Loc.T(off ? "notice.effectsOff" : "notice.effectsOn"));
        _tray?.Refresh();
        SettingsChanged?.Invoke();
        Save();
    }

    // ── 백업·복원·초기화 ─────────────────────────────────────

    public async Task<bool> CreateBackupAsync(string zipPath)
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<AppState>(System.Text.Json.JsonSerializer.Serialize(State))!;
        try
        {
            var (photos, missing) = await Task.Run(() => Backup.Create(zipPath, snapshot, AppVersion));
            Log.Info($"Backup created: {zipPath} ({photos} photos, {missing} missing)");
            Notice?.Invoke(Loc.F("notice.backupDone", photos) + (missing > 0 ? " · " + Loc.F("notice.backupMissing", missing) : ""));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Backup failed: {zipPath}", ex);
            Notice?.Invoke(Loc.F("notice.backupFailed", ex.Message));
            return false;
        }
    }

    /// <summary>백업에서 복원: 지금 상태를 자동 백업한 뒤 백업 파일의 라이브러리·화면·장면·설정으로 바꿈.</summary>
    public async Task<bool> RestoreBackupAsync(string zipPath)
    {
        if (IsEditing) FinishEdit(save: true);
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<AppState>(System.Text.Json.JsonSerializer.Serialize(State))!;
        AppState restored;
        try
        {
            Notice?.Invoke(Loc.T("notice.restoring"));
            await Task.Run(() => Backup.AutoBackup(snapshot, AppVersion, "before-restore"));
            restored = await Task.Run(() => Backup.Restore(zipPath));
        }
        catch (Exception ex)
        {
            Log.Error($"Restore failed: {zipPath}", ex);
            Notice?.Invoke(Loc.F("notice.restoreFailed", ex.Message));
            return false;
        }
        ReplaceState(restored);
        Log.Info($"Restored backup {zipPath}: {State.Library.Count} photos, {State.Active.Count} on screen, {State.Scenes.Count} scenes");
        Notice?.Invoke(Loc.F("notice.restored", State.Library.Count, State.Active.Count));
        return true;
    }

    /// <summary>상태를 통째로 바꿈 (화면의 GIF를 모두 새로 띄움).</summary>
    private void ReplaceState(AppState next)
    {
        foreach (var a in State.Active) Engine.Remove(a.Id);
        bool autostart = Autostart.IsEnabled();
        var window = State.Settings.Window;
        State.Library = next.Library;
        State.Active = next.Active;
        State.Scenes = next.Scenes;
        State.EffectPresets = next.EffectPresets;
        State.CurrentSceneId = next.CurrentSceneId;
        State.Settings = next.Settings;
        State.Settings.StartWithWindows = autostart; // 자동 실행은 이 PC의 실제 상태를 따름
        State.Settings.Window = window;
        State.Settings.Normalize();
        _photoVMs.Clear();
        foreach (var p in State.Library)
        {
            AnimationCache.Invalidate(Store.ResolvePath(p));
            Images.ForgetThumb(p);
            _photoVMs[p.Id] = new PhotoVM(p);
        }
        SelectedActiveId = null;
        SelectedPhotoId = null;
        PanelMode = PanelMode.None;
        foreach (var a in State.Active) a.Normalize();
        MonitorList = Monitors.List();
        EnsureAllReachable(notify: false);
        foreach (var a in State.Active) SyncToEngine(a);
        ApplyEngineSettings();
        if (Loc.Normalize(State.Settings.Language) != Loc.Language) SetLanguage(State.Settings.Language);
        Themes.Apply(State.Settings.Theme);
        ThemeChanged?.Invoke();
        RebuildVisible();
        RebuildActives();
        if (State.Active.Count > 0) SelectActive(State.Active[0].Id, flash: false);
        else SelectionChanged?.Invoke();
        _tray?.Refresh();
        _main?.RefreshHiddenToggle();
        SettingsChanged?.Invoke();
        ScenesChanged?.Invoke();
        HotkeysChanged?.Invoke();
        Store.SaveNow(State);
    }

    /// <summary>설정만 기본값으로 (라이브러리·화면의 GIF·장면·프리셋은 그대로).</summary>
    public void ResetSettings()
    {
        State.Settings = State.Settings.ResetKeepingBasics();
        State.Settings.Normalize();
        ApplyEngineSettings();
        Themes.Apply(State.Settings.Theme);
        ThemeChanged?.Invoke();
        Log.Info("Settings reset to defaults");
        _tray?.Refresh();
        _main?.RefreshHiddenToggle();
        SettingsChanged?.Invoke();
        HotkeysChanged?.Invoke();
        Notice?.Invoke(Loc.T("notice.settingsReset"));
        Save();
    }

    // ── 업데이트 알림 ────────────────────────────────────────

    /// <summary>마지막 확인 결과 (확인 전·실패는 null).</summary>
    public Updates.Result? LatestRelease { get; private set; }
    public bool CheckingUpdates { get; private set; }
    public event Action? UpdateStatusChanged;
    private DispatcherTimer? _updateClock;

    private void StartUpdateClock()
    {
        _updateClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) }; // 시작 직후는 피해서 한 번, 그 뒤 6시간마다
        _updateClock.Tick += async (_, _) =>
        {
            _updateClock.Interval = TimeSpan.FromHours(6);
            var last = State.Settings.LastUpdateCheck;
            if (State.Settings.CheckUpdates && (last == null || DateTimeOffset.Now - last.Value > TimeSpan.FromHours(20)))
                await CheckForUpdatesAsync(manual: false);
        };
        if (Environment.GetEnvironmentVariable("GIFPIN_SMOKETEST") != "1") _updateClock.Start();
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (CheckingUpdates) return;
        CheckingUpdates = true;
        UpdateStatusChanged?.Invoke();
        var result = await Updates.CheckAsync(AppVersion);
        CheckingUpdates = false;
        if (result != null)
        {
            LatestRelease = result;
            State.Settings.LastUpdateCheck = DateTimeOffset.Now;
            Save();
            if (result.IsNewer && (manual || State.Settings.NotifiedVersion != result.Version))
            {
                State.Settings.NotifiedVersion = result.Version;
                Log.Info($"New version available: {result.Version}");
                if (!manual) _tray?.ShowBalloon(Loc.T("update.availableTitle"), Loc.F("update.availableText", result.Version), () => OpenUrl(result.Url));
            }
        }
        else if (manual) Notice?.Invoke(Loc.T("update.failed"));
        UpdateStatusChanged?.Invoke();
    }

    public void SetCheckUpdates(bool on)
    {
        State.Settings.CheckUpdates = on;
        Save();
    }

    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {url}", ex); }
    }

    public void SetAutostartDelay(int seconds)
    {
        State.Settings.AutostartDelaySec = Math.Clamp(seconds, 0, 300);
        Save();
    }

    // ── 새 GIF 기본값 ────────────────────────────────────────

    /// <summary>새 GIF 기본값 템플릿 (없으면 기본 설정으로 새로 만듦 — 고치면 저장됨).</summary>
    public ActiveItem EditableTemplate => State.Settings.NewItemTemplate ??= new ActiveItem();

    public void SetNewItemSize(int px)
    {
        State.Settings.NewItemSize = Math.Clamp(px, 32, 2000);
        Save();
    }

    public void ChangeTemplate(Action<ActiveItem> change)
    {
        change(EditableTemplate);
        EditableTemplate.Normalize();
        Save();
        SettingsChanged?.Invoke();
    }

    /// <summary>선택한 GIF의 움직임·효과·표시 방식을 새 GIF 기본값으로.</summary>
    public bool TemplateFromSelected()
    {
        if (SelectedActive is not ActiveItem a) return false;
        var t = new ActiveItem();
        t.CopyStyleFrom(a);
        State.Settings.NewItemTemplate = t;
        Save();
        Notice?.Invoke(Loc.T("notice.templateSaved"));
        SettingsChanged?.Invoke();
        return true;
    }

    public void ClearTemplate()
    {
        State.Settings.NewItemTemplate = null;
        Save();
        SettingsChanged?.Invoke();
    }

    /// <summary>기본값 요약 ("걷기 · 효과 2개 · 바탕화면").</summary>
    public string TemplateSummary()
    {
        var t = State.Settings.NewItemTemplate;
        if (t == null) return Loc.T("newItem.summaryDefault");
        var parts = new List<string> { Loc.T("motion." + Motion.Normalize(t.MotionMode)) };
        int fx = t.EnabledEffectCount();
        parts.Add(fx == 0 ? Loc.T("newItem.noEffects") : Loc.F("newItem.effectsN", fx));
        return string.Join(" · ", parts);
    }

    // ── 설정 복사·붙여넣기 / 효과 프리셋 ───────────────────────

    /// <summary>복사해 둔 설정 (앱 안에서만).</summary>
    public ActiveItem? CopiedStyle { get; private set; }

    public void CopyStyle(string activeId)
    {
        var a = State.Active.FirstOrDefault(x => x.Id == activeId);
        if (a == null) return;
        CopiedStyle = a.Clone();
        Notice?.Invoke(Loc.T("notice.styleCopied"));
    }

    public void PasteStyle(string activeId, bool effectsOnly)
    {
        if (CopiedStyle is not ActiveItem src) return;
        UpdateActive(activeId, a => a.CopyStyleFrom(src, effectsOnly));
        Notice?.Invoke(Loc.T(effectsOnly ? "notice.effectsPasted" : "notice.stylePasted"));
        SelectionChanged?.Invoke();
    }

    public void PasteStyleToAll(bool effectsOnly)
    {
        if (CopiedStyle is not ActiveItem src) return;
        foreach (var a in State.Active.ToList()) UpdateActive(a.Id, x => x.CopyStyleFrom(src, effectsOnly));
        Notice?.Invoke(Loc.F("notice.stylePastedAll", State.Active.Count));
        SelectionChanged?.Invoke();
    }

    /// <summary>기본 프리셋 + 내가 저장한 프리셋.</summary>
    public IEnumerable<EffectPreset> AllPresets() => EffectPreset.BuiltIns().Concat(State.EffectPresets);

    public void ApplyPreset(string activeId, string presetId)
    {
        var preset = AllPresets().FirstOrDefault(x => x.Id == presetId);
        if (preset == null) return;
        UpdateActive(activeId, a => a.CopyStyleFrom(preset.Values, effectsOnly: true));
        Notice?.Invoke(Loc.F("notice.presetApplied", preset.Name));
        SelectionChanged?.Invoke();
    }

    public EffectPreset? SavePreset(string activeId, string name)
    {
        var a = State.Active.FirstOrDefault(x => x.Id == activeId);
        if (a == null || string.IsNullOrWhiteSpace(name)) return null;
        var values = new ActiveItem();
        values.CopyStyleFrom(a, effectsOnly: true);
        var existing = State.EffectPresets.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (existing != null) existing.Values = values; // 같은 이름이면 덮어씀
        else State.EffectPresets.Add(existing = new EffectPreset { Id = Store.NewId("e"), Name = name.Trim(), Values = values });
        Save();
        Notice?.Invoke(Loc.F("notice.presetSaved", existing.Name));
        return existing;
    }

    public void DeletePreset(string presetId)
    {
        if (State.EffectPresets.RemoveAll(x => x.Id == presetId) > 0) Save();
    }

    // ── 프로그램별 숨기기 ────────────────────────────────────

    /// <summary>프로그램 규칙 때문에 숨김 상태가 바뀜.</summary>
    public event Action? RuleStateChanged;

    public void AddAppRule(string exe)
    {
        string name = AppRules.NormalizeName(exe);
        if (State.Settings.HideForApps.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) return;
        State.Settings.HideForApps.Add(name);
        ApplyAppRules();
    }

    public void RemoveAppRule(string exe)
    {
        State.Settings.HideForApps.RemoveAll(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase));
        ApplyAppRules();
    }

    public void SetAppRuleMode(bool whileRunning)
    {
        State.Settings.HideForAppsWhileRunning = whileRunning;
        ApplyAppRules();
    }

    private void ApplyAppRules()
    {
        State.Settings.Normalize();
        Engine.SetAppRules(State.Settings.HideForApps, State.Settings.HideForAppsWhileRunning);
        Save();
        SettingsChanged?.Invoke();
    }

    // ── 장면 (저장한 화면 배치) ────────────────────────────────

    /// <summary>장면 목록이 바뀜.</summary>
    public event Action? ScenesChanged;

    private static List<ActiveItem> CloneItems(IEnumerable<ActiveItem> items) => items.Select(a => a.Clone()).ToList();

    public Scene SaveScene(string name)
    {
        var scene = new Scene
        {
            Id = Store.NewId("s"),
            Name = string.IsNullOrWhiteSpace(name) ? Loc.F("scene.defaultName", State.Scenes.Count + 1) : name.Trim(),
            Items = CloneItems(State.Active),
            SavedAt = DateTimeOffset.Now,
        };
        State.Scenes.Add(scene);
        State.CurrentSceneId = scene.Id;
        Log.Info($"Scene saved: '{scene.Name}' ({scene.Items.Count} GIFs)");
        Notice?.Invoke(Loc.F("notice.sceneSaved", scene.Name));
        AfterScenesChanged();
        return scene;
    }

    /// <summary>장면을 지금 화면으로 덮어씀.</summary>
    public void OverwriteScene(string sceneId)
    {
        var sc = State.Scenes.FirstOrDefault(x => x.Id == sceneId);
        if (sc == null) return;
        sc.Items = CloneItems(State.Active);
        sc.SavedAt = DateTimeOffset.Now;
        State.CurrentSceneId = sc.Id;
        Notice?.Invoke(Loc.F("notice.sceneSaved", sc.Name));
        AfterScenesChanged();
    }

    public void RenameScene(string sceneId, string name)
    {
        var sc = State.Scenes.FirstOrDefault(x => x.Id == sceneId);
        if (sc == null || string.IsNullOrWhiteSpace(name)) return;
        sc.Name = name.Trim();
        AfterScenesChanged();
    }

    public void DeleteScene(string sceneId)
    {
        if (State.Scenes.RemoveAll(x => x.Id == sceneId) == 0) return;
        if (State.CurrentSceneId == sceneId) State.CurrentSceneId = null;
        AfterScenesChanged();
    }

    /// <summary>매일 자동으로 적용할 시각 ("HH:mm", 빈 글자 = 끔). 형식이 틀리면 false.</summary>
    public bool SetSceneTime(string sceneId, string? time, bool quiet = false)
    {
        var sc = State.Scenes.FirstOrDefault(x => x.Id == sceneId);
        if (sc == null) return false;
        string? t = Scene.NormalizeTime(time);
        if (t == null && !string.IsNullOrWhiteSpace(time)) return false;
        sc.AutoAt = t;
        if (quiet)
        {
            // 설정 화면의 시계에서 바꾼 경우: 목록을 다시 만들지 않음 (돌리던 시계가 사라지지 않게)
            Save();
            _tray?.Refresh();
        }
        else AfterScenesChanged();
        return true;
    }

    /// <summary>화면의 GIF 하나의 움직임·효과·표시 방식을 새 GIF 기본값으로.</summary>
    public bool TemplateFromActive(string activeId)
    {
        var a = State.Active.FirstOrDefault(x => x.Id == activeId);
        if (a == null) return false;
        var t = new ActiveItem();
        t.CopyStyleFrom(a);
        State.Settings.NewItemTemplate = t;
        Save();
        Notice?.Invoke(Loc.F("notice.templateFrom", State.FindPhoto(a.PhotoId)?.Name ?? ""));
        SettingsChanged?.Invoke();
        return true;
    }

    /// <summary>지금 적용 중인 장면 (없으면 null).</summary>
    public Scene? CurrentScene => State.Scenes.FirstOrDefault(x => x.Id == State.CurrentSceneId);

    private void AfterScenesChanged()
    {
        Save();
        _tray?.Refresh();
        ScenesChanged?.Invoke();
    }

    /// <summary>장면을 화면에 적용: 지금 떠 있는 GIF를 모두 내리고 장면의 GIF를 띄운다 (사진이 지워진 것은 건너뜀).</summary>
    public void ApplyScene(string sceneId)
    {
        var sc = State.Scenes.FirstOrDefault(x => x.Id == sceneId);
        if (sc == null) return;
        if (IsEditing) FinishEdit(save: true);
        foreach (var a in State.Active) Engine.Remove(a.Id);
        State.Active.Clear();
        int skipped = 0;
        foreach (var item in sc.Items)
        {
            var p = State.FindPhoto(item.PhotoId);
            if (p == null || !File.Exists(Store.ResolvePath(p))) { skipped++; continue; }
            var a = item.Clone();
            a.Id = Store.NewId("a"); // 같은 장면을 다시 적용해도 창을 새로 만듦
            a.Normalize();
            State.Active.Add(a);
            p.LastUsedAt = DateTimeOffset.Now;
        }
        MonitorList = Monitors.List();
        EnsureAllReachable(notify: false);
        foreach (var a in State.Active) SyncToEngine(a);
        State.CurrentSceneId = sc.Id;
        SelectedActiveId = null;
        RebuildActives();
        RebuildVisible();
        if (State.Active.Count > 0) SelectActive(State.Active[0].Id, flash: false);
        else SelectActive(null, flash: false);
        _toolbar?.RefreshList();
        Log.Info($"Scene applied: '{sc.Name}' ({State.Active.Count} GIFs, {skipped} skipped)");
        Notice?.Invoke(Loc.F("notice.sceneApplied", sc.Name) + (skipped > 0 ? " · " + Loc.F("notice.sceneSkipped", skipped) : ""));
        AfterScenesChanged();
    }

    /// <summary>다음 장면 (단축키): 마지막으로 적용한 장면의 다음 것, 끝이면 처음으로.</summary>
    public void ApplyNextScene()
    {
        if (State.Scenes.Count == 0)
        {
            Notice?.Invoke(Loc.T("notice.noScenes"));
            _tray?.ShowBalloon("GifPin", Loc.T("notice.noScenes"));
            return;
        }
        int i = State.Scenes.FindIndex(x => x.Id == State.CurrentSceneId);
        ApplyScene(State.Scenes[(i + 1) % State.Scenes.Count].Id);
    }

    private DispatcherTimer? _sceneClock;
    private DateTime _sceneCheckedAt;

    /// <summary>장면 자동 전환: 30초마다 지난 확인 이후 지나간 시각이 있으면 그 장면을 적용 (켜져 있는 동안만).</summary>
    private void StartSceneClock()
    {
        _sceneCheckedAt = DateTime.Now;
        _sceneClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _sceneClock.Tick += (_, _) => CheckSceneSchedule(DateTime.Now);
        _sceneClock.Start();
    }

    /// <summary>(from, to] 사이에 자동 적용 시각이 지난 장면 중 가장 늦은 것을 적용.</summary>
    internal void CheckSceneSchedule(DateTime now)
    {
        var from = _sceneCheckedAt;
        _sceneCheckedAt = now;
        if (now <= from || now - from > TimeSpan.FromHours(6)) return; // 절전에서 깨어난 경우 등은 건너뜀
        Scene? due = null;
        DateTime dueAt = DateTime.MinValue;
        foreach (var sc in State.Scenes)
        {
            if (sc.AutoAt == null) continue;
            var parts = sc.AutoAt.Split(':');
            var t = now.Date.AddHours(int.Parse(parts[0])).AddMinutes(int.Parse(parts[1]));
            if (t > now) t = t.AddDays(-1);
            if (t > from && t <= now && t > dueAt) { due = sc; dueAt = t; }
        }
        if (due != null) ApplyScene(due.Id);
    }

    internal void SetSceneClockForTest(DateTime at) => _sceneCheckedAt = at;

    /// <summary>효과 성능 설정.</summary>
    public void SetEffectsSettings(int fps, bool pauseFullscreen, bool pauseOnBattery, bool autoLight, int cpuPercent)
    {
        var s = State.Settings;
        s.EffectsFps = fps >= 45 ? 60 : 30;
        s.PauseEffectsFullscreen = pauseFullscreen;
        s.PauseEffectsOnBattery = pauseOnBattery;
        s.AutoLightEffects = autoLight;
        s.AutoLightCpuPercent = Math.Clamp(cpuPercent, 50, 95);
        s.PerfMode = PerfModes.Detect(s);
        Engine.SetEffectsSettings(s.EffectsFps, pauseFullscreen, pauseOnBattery, autoLight, s.AutoLightCpuPercent);
        Save();
        SettingsChanged?.Invoke();
    }

    /// <summary>움직이는 GIF 잡기: 눌러야 하는 시간 / 놓은 뒤 다시 움직이기까지 (ms).</summary>
    public void SetPointerTiming(int holdMs, int resumeMs)
    {
        State.Settings.HoldToGrabMs = Math.Clamp(holdMs, 0, 5000);
        State.Settings.ResumeDelayMs = Math.Clamp(resumeMs, 0, 10000);
        Engine.SetPointerTiming(State.Settings.HoldToGrabMs, State.Settings.ResumeDelayMs);
        Save();
    }

    /// <summary>Windows 시작 시 자동 실행이 바뀜 (설정 화면 갱신용).</summary>
    public event Action? AutostartChanged;

    public void SetStartWithWindows(bool on)
    {
        Autostart.Apply(on);
        State.Settings.StartWithWindows = Autostart.IsEnabled();
        Save();
        AutostartChanged?.Invoke();
    }

    public void OpenBackgroundRemover(string photoId, string? fromActiveId)
    {
        var p = State.FindPhoto(photoId);
        if (p == null) return;
        var win = new BgRemoveWindow(this, p, fromActiveId);
        if (_main != null && _main.IsVisible) win.Owner = _main;
        win.Show();
    }

    public void OpenSpriteImport()
    {
        var win = new SpriteImportWindow(this);
        if (_main != null && _main.IsVisible) win.Owner = _main;
        win.Show();
    }

    public IEnumerable<PhotoItem> RecentPhotos(int count) =>
        State.Library.Where(p => p.LastUsedAt != null).OrderByDescending(p => p.LastUsedAt).Take(count);
}
