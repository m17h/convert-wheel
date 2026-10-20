using System.Drawing;
using System.Windows.Forms;
using ConvertWheel.Input;

namespace ConvertWheel.UI;

/// <summary>Notification-area icon with a small dark context menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly AppSettings _settings;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _pause;
    readonly Icon _normalIcon = IconFactory.TrayIcon(paused: false);
    readonly Icon _pausedIcon = IconFactory.TrayIcon(paused: true);

    public TrayIcon(AppSettings settings, Action openSettings, Action exit)
    {
        _settings = settings;
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false },
            ShowImageMargin = false,
            BackColor = Color.FromArgb(32, 32, 39),
            ForeColor = Color.FromArgb(236, 236, 242),
            Font = new Font("Segoe UI", 9.75f),
            Padding = new Padding(2, 4, 2, 4),
        };
        var title = new ToolStripLabel("Convert Wheel")
        {
            Font = new Font("Segoe UI Semibold", 9.75f),
            ForeColor = Color.FromArgb(160, 160, 176),
            Padding = new Padding(6, 4, 6, 4),
        };
        _pause = Item("Pause", () =>
        {
            _settings.Paused = !_settings.Paused;
            _settings.Save();
        });
        menu.Items.Add(title);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Settings…", openSettings));
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Exit", exit));

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) openSettings(); };
        settings.Changed += Refresh;
        Refresh();
    }

    static ToolStripMenuItem Item(string text, Action action) => new(text, null, (_, _) => action())
    {
        ForeColor = Color.FromArgb(236, 236, 242),
        Padding = new Padding(4, 3, 4, 3),
    };

    void Refresh()
    {
        bool paused = _settings.Paused;
        _icon.Icon = paused ? _pausedIcon : _normalIcon;
        _icon.Text = paused
            ? "Convert Wheel (paused)"
            : $"Convert Wheel: hold {VirtualKeys.Describe(_settings.TriggerKeys)} over a file";
        _pause.Text = paused ? "Resume" : "Pause";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(32, 32, 39);
        static readonly Color Hover = Color.FromArgb(52, 52, 63);
        static readonly Color Line = Color.FromArgb(58, 58, 68);

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Back;
    }
}
