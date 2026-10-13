using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>선택이 바뀜 → 속성 패널 다시 그리기.</summary>
    public event Action? SelectionChanged;
    /// <summary>선택된 GIF의 위치·크기가 바뀜 (드래그 확정, 실시간 포함).</summary>
    public event Action<ActiveItem>? GeometryChanged;
    /// <summary>안내 메시지 (메인 창 하단 토스트).</summary>
    public event Action<string>? Notice;

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
        Engine.LoadFailed += (id, msg) => _ui.InvokeAsync(() => Notice?.Invoke($"GIF를 열 수 없습니다: {msg}"));
    }

    // ── 시작 / 종료 ──────────────────────────────────────────

    public void Start(bool showWindow)
    {
        Engine.Start();
        Engine.SetAllHidden(State.Settings.AllHidden);

        // 파일이 사라진 사진을 쓰는 GIF는 화면에서 내림
        State.Active.RemoveAll(a => State.FindPhoto(a.PhotoId) is not PhotoItem p || !System.IO.File.Exists(Store.ResolvePath(p)));
        foreach (var a in State.Active) SyncToEngine(a);

        foreach (var p in State.Library) _photoVMs[p.Id] = new PhotoVM(p);
        RebuildVisible();
        RebuildActives();
        if (State.Active.Count > 0) SelectActive(State.Active[0].Id, flash: false);

        _tray = new TrayIcon(this);
        State.Settings.StartWithWindows = Autostart.IsEnabled(); // 설치 프로그램에서 켰을 수도 있음
        if (State.Settings.StartWithWindows) Autostart.Apply(true); // 설치 위치가 바뀌었을 수 있어 경로 갱신

        if (showWindow) ShowMain();
        if (Engine.HotkeyFailures.Count > 0)
            _tray.ShowBalloon("단축키 일부를 쓸 수 없습니다",
                string.Join(", ", Engine.HotkeyFailures) + " 단축키를 다른 프로그램이 쓰고 있습니다.");
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
            _tray?.ShowBalloon("GifPin이 트레이에서 계속 실행됩니다", "화면의 GIF는 그대로 표시됩니다. 완전히 끄려면 트레이 아이콘 메뉴의 \"종료\"를 누르세요.");
            Save();
        }
    }

    public void Quit()
    {
        if (IsQuitting) return;
        IsQuitting = true;
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
        var existing = State.Library.ToList();
        bool copy = State.Settings.CopyOnAdd;
        Notice?.Invoke("사진을 추가하는 중…");
        ImportResult r;
        try { r = await Task.Run(() => Library.Import(list, existing, copy)); }
        catch (Exception ex)
        {
            Notice?.Invoke("추가하지 못했습니다: " + ex.Message);
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

        var parts = new List<string>();
        if (r.Added.Count > 0) parts.Add($"{r.Added.Count}개 추가");
        if (r.Existing.Count > 0) parts.Add($"{r.Existing.Count}개는 이미 있음");
        if (r.Skipped > 0) parts.Add($"{r.Skipped}개는 지원하지 않는 파일이라 건너뜀");
        Notice?.Invoke(parts.Count > 0 ? string.Join(" · ", parts) : "추가할 사진이 없습니다");
    }

    /// <summary>배경 지우기 등으로 새로 만든 사진을 등록.</summary>
    public void AddGeneratedPhoto(PhotoItem photo)
    {
        State.Library.Add(photo);
        _photoVMs[photo.Id] = new PhotoVM(photo);
        RebuildVisible();
        SelectPhoto(photo.Id);
        Save();
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

    // ── 화면 배치 ────────────────────────────────────────────

    public void AddToScreen(string photoId)
    {
        var p = State.FindPhoto(photoId);
        if (p == null) return;
        double scale = Math.Max(p.Width, p.Height) > 400 ? 400.0 / Math.Max(p.Width, p.Height) : 1.0;
        int w = (int)Math.Round(p.Width * scale), h = (int)Math.Round(p.Height * scale);
        var work = Native.WorkAreaAt(1, 1); // 주 모니터
        int offset = (State.Active.Count % 6) * 28;
        var a = new ActiveItem
        {
            Id = Store.NewId("a"),
            PhotoId = p.Id,
            Scale = Math.Round(scale, 3),
            X = work.Right - w - 24 - offset,
            Y = work.Bottom - h - 24 - offset,
        };
        State.Active.Add(a);
        p.LastUsedAt = DateTimeOffset.Now;
        SyncToEngine(a);
        RebuildActives();
        SelectActive(a.Id, flash: true);
        RebuildVisible();
        Save();
        Notice?.Invoke($"\"{p.Name}\"을(를) 화면에 추가했습니다");
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
        Notice?.Invoke($"\"{p.Name}\"(으)로 교체했습니다");
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
        SyncToEngine(a);
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
        if (p != null) Engine.Sync(a, Store.ResolvePath(p));
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
            Notice?.Invoke("화면에 표시 중인 GIF가 없습니다. 먼저 사진을 화면에 추가하세요.");
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
            // 편집 중 내린 GIF 복원, 위치·크기 되돌리기
            foreach (var a in State.Active.Where(a => _editSnapshot.All(s => s.Id != a.Id)).ToList()) RemoveActive(a.Id);
            State.Active.Clear();
            State.Active.AddRange(_editSnapshot.Select(s => s.Clone()));
            foreach (var a in State.Active) SyncToEngine(a);
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
        UpdateActive(a.Id, x =>
        {
            x.Scale = s;
            x.X = (int)Math.Round(cx - p.Width * s / 2);
            x.Y = (int)Math.Round(cy - p.Height * s / 2);
        });
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

    public IEnumerable<PhotoItem> RecentPhotos(int count) =>
        State.Library.Where(p => p.LastUsedAt != null).OrderByDescending(p => p.LastUsedAt).Take(count);
}
