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
        Engine.LiveChanged += a => _ui.InvokeAsync(() => OnEngineChanged(a, commit: false));
        Engine.Clicked += id => _ui.InvokeAsync(() => SelectActive(id, flash: false));
        Engine.HotkeyPressed += id => _ui.InvokeAsync(() => OnHotkey(id));
        Engine.LoadFailed += (id, msg) => _ui.InvokeAsync(() => Notice?.Invoke(Loc.F("notice.loadFailed", msg)));
    }

    // ── 시작 / 종료 ──────────────────────────────────────────

    public void Start(bool showWindow)
    {
        Log.Info($"GifPin {AppVersion} starting · {Environment.OSVersion} · {State.Library.Count} photos, {State.Active.Count} on screen");
        Engine.Start();
        Engine.SetAllHidden(State.Settings.AllHidden);
        foreach (var f in Engine.HotkeyFailures) Log.Warn($"Hotkey unavailable: {f}");

        // 파일이 사라진 사진을 쓰는 GIF는 화면에서 내림
        int missing = State.Active.RemoveAll(a => State.FindPhoto(a.PhotoId) is not PhotoItem p || !File.Exists(Store.ResolvePath(p)));
        if (missing > 0) Log.Warn($"Removed {missing} overlay(s) whose image file is missing");

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

        if (showWindow) ShowMain();
        if (Engine.HotkeyFailures.Count > 0)
            _tray.ShowBalloon(Loc.T("tray.hotkeyFailTitle"), Loc.F("tray.hotkeyFailText", string.Join(", ", Engine.HotkeyFailures)));
        Save();
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
        if (IsEditing) FinishEdit(save: true);
        if (_main != null) State.Settings.Window = _main.CurrentBounds();
        Store.SaveNow(State);
        Engine.Dispose();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    public void Save() => Store.SaveSoon(State);

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
        double scale = Math.Max(p.Width, p.Height) > 400 ? 400.0 / Math.Max(p.Width, p.Height) : 1.0;
        if (p.Pixelated && Math.Max(p.Width, p.Height) < 96) scale = Math.Floor(160.0 / Math.Max(p.Width, p.Height)); // 작은 도트는 정수배로 키움
        int w = (int)Math.Round(p.Width * scale), h = (int)Math.Round(p.Height * scale);
        var mon = Monitors.Primary(MonitorList.Count > 0 ? MonitorList : Monitors.List());
        int offset = (State.Active.Count % 6) * 28;
        var a = new ActiveItem
        {
            Id = Store.NewId("a"),
            PhotoId = p.Id,
            Scale = Math.Round(scale, 3),
            X = mon.WorkRight - w - 24 - offset,
            Y = mon.WorkBottom - h - 24 - offset,
        };
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
        a.Scale = Math.Round(Math.Clamp(a.Scale, 0.1, 4.0), 3);
        a.Opacity = Math.Clamp(a.Opacity, 0.1, 1.0);
        a.MotionMode = Motion.Normalize(a.MotionMode);
        a.MotionSpeed = Math.Clamp(a.MotionSpeed, Motion.MinSpeed, Motion.MaxSpeed);
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
        if (p != null) Engine.Sync(a, Store.ResolvePath(p), p.Pixelated);
    }

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
        if (!Motion.IsMoving(a.MotionMode))
            Relocate(a.Id, (int)Math.Round(cx - p.Width * s / 2), (int)Math.Round(cy - p.Height * s / 2));
    }

    // ── 기타 ─────────────────────────────────────────────────

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case OverlayEngine.HotkeyEdit: ToggleEdit(); break;
            case OverlayEngine.HotkeyHide: SetAllHidden(!State.Settings.AllHidden); break;
            case OverlayEngine.HotkeyQuit: Quit(); break;
        }
    }

    public void SetStartWithWindows(bool on)
    {
        State.Settings.StartWithWindows = on;
        Autostart.Apply(on);
        Save();
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
