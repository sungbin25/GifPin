using System;
using System.Linq;
using GifPin.Core;
using Forms = System.Windows.Forms;

namespace GifPin.UI;

/// <summary>시스템 트레이 아이콘과 메뉴 (WinForms NotifyIcon). 메뉴는 열 때마다 현재 언어로 다시 만든다.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppController _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly System.Drawing.Font _font = new("Segoe UI", 9.5f);
    private readonly System.Drawing.Font _bold = new("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);

    public TrayIcon(AppController app)
    {
        _app = app;
        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = _font };
        _menu.Opening += (_, _) => Rebuild();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "GifPin",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _app.ShowMain(); };
        Rebuild();
    }

    public void Refresh() => Rebuild();

    private Forms.ToolStripMenuItem Item(string text, Action onClick, string? shortcut = null)
    {
        var item = new Forms.ToolStripMenuItem(text, null, (_, _) => onClick());
        if (shortcut != null) item.ShortcutKeyDisplayString = shortcut;
        return item;
    }

    private void Rebuild()
    {
        _menu.Items.Clear();
        var open = Item(Loc.T("tray.open"), _app.ShowMain);
        open.Font = _bold;
        _menu.Items.Add(open);
        _menu.Items.Add(Item(_app.IsEditing ? Loc.T("tray.editEnd") : Loc.T("tray.edit"), _app.ToggleEdit, "Ctrl+Alt+E"));
        _menu.Items.Add(Item(_app.State.Settings.AllHidden ? Loc.T("tray.showAll") : Loc.T("tray.hideAll"),
            () => _app.SetAllHidden(!_app.State.Settings.AllHidden), "Ctrl+Alt+H"));

        var recent = _app.RecentPhotos(5).ToList();
        if (recent.Count > 0)
        {
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add(new Forms.ToolStripMenuItem(Loc.T("tray.recent")) { Enabled = false });
            foreach (var p in recent)
            {
                var id = p.Id;
                _menu.Items.Add(Item("   " + p.Name, () => _app.AddToScreen(id)));
            }
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        var recovery = new Forms.ToolStripMenuItem(Loc.T("tray.recovery"));
        recovery.DropDownItems.Add(Item(Loc.T("rec.gather"), _app.GatherAllToCenter));
        recovery.DropDownItems.Add(Item(Loc.T("rec.showAll"), _app.ShowAll));
        recovery.DropDownItems.Add(Item(Loc.T("rec.clickThroughOff"), _app.DisableClickThroughAll));
        _menu.Items.Add(recovery);
        _menu.Items.Add(Item(Loc.T("tray.quit"), _app.Quit, "Ctrl+Alt+Q"));
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
        _font.Dispose();
        _bold.Dispose();
    }
}
