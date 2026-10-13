using System;
using System.Linq;
using Forms = System.Windows.Forms;

namespace GifPin.UI;

/// <summary>시스템 트레이 아이콘과 메뉴 (WinForms NotifyIcon).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppController _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _edit, _hide;

    public TrayIcon(AppController app)
    {
        _app = app;
        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = new System.Drawing.Font("Malgun Gothic", 9.5f) };

        var open = new Forms.ToolStripMenuItem("GifPin 열기", null, (_, _) => _app.ShowMain()) { Font = new System.Drawing.Font("Malgun Gothic", 9.5f, System.Drawing.FontStyle.Bold) };
        _edit = new Forms.ToolStripMenuItem("편집 모드", null, (_, _) => _app.ToggleEdit()) { ShortcutKeyDisplayString = "Ctrl+Alt+E" };
        _hide = new Forms.ToolStripMenuItem("모두 숨기기", null, (_, _) => _app.SetAllHidden(!_app.State.Settings.AllHidden)) { ShortcutKeyDisplayString = "Ctrl+Alt+H" };
        var quit = new Forms.ToolStripMenuItem("종료", null, (_, _) => _app.Quit()) { ShortcutKeyDisplayString = "Ctrl+Alt+Q" };

        _menu.Items.AddRange(new Forms.ToolStripItem[] { open, _edit, _hide, new Forms.ToolStripSeparator(), quit });
        _menu.Opening += (_, _) => RebuildRecent();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "GifPin",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _app.ShowMain(); };
        Refresh();
    }

    public void Refresh()
    {
        _edit.Text = _app.IsEditing ? "편집 모드 끝내기 (저장)" : "편집 모드";
        _hide.Text = _app.State.Settings.AllHidden ? "모두 보이기" : "모두 숨기기";
    }

    /// <summary>메뉴를 열 때마다 "최근 사용" 5개를 다시 채운다.</summary>
    private void RebuildRecent()
    {
        Refresh();
        for (int i = _menu.Items.Count - 1; i >= 0; i--)
            if (_menu.Items[i].Tag as string == "recent") _menu.Items.RemoveAt(i);

        var recent = _app.RecentPhotos(5).ToList();
        if (recent.Count == 0) return;
        int at = 3; // "모두 숨기기" 다음
        _menu.Items.Insert(at++, new Forms.ToolStripSeparator { Tag = "recent" });
        _menu.Items.Insert(at++, new Forms.ToolStripMenuItem("최근 사용 · 눌러서 화면에 추가") { Enabled = false, Tag = "recent" });
        foreach (var p in recent)
        {
            var id = p.Id;
            _menu.Items.Insert(at++, new Forms.ToolStripMenuItem("   " + p.Name, null, (_, _) => _app.AddToScreen(id)) { Tag = "recent" });
        }
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(4000);
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/GifPin.ico"));
            if (info != null) return new System.Drawing.Icon(info.Stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
