using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Drawing.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using SystemFonts = System.Drawing.SystemFonts;
using TextBox = System.Windows.Controls.TextBox;

namespace ThinkPadBacklightTray;

public class App : Application
{
    private const int DwmaUseImmersiveDarkMode = 20;
    private EventMonitor? _eventMonitor;
    private NotifyIcon? _notifyIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        SettingsManager.Initialize();
        BacklightController.Initialize();

        _eventMonitor = new EventMonitor();
        _eventMonitor.OnRestoreBacklight += () => RestoreBacklight();
        _eventMonitor.OnResumeRestoreBacklight += () => KickAndRestoreBacklight();
        _eventMonitor.OnFnSpaceLevelChanged += level =>
        {
            Debug.WriteLine($"Fn+Space: persisting new level {level} to registry");
            SettingsManager.SetBacklightLevel(level);
        };
        _eventMonitor.Start();

        BuildTrayIcon();

        // Restore backlight immediately on startup (e.g. after a reboot or log-on).
        RestoreBacklight();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _eventMonitor?.Stop();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        base.OnExit(e);
    }

    // ── tray icon ────────────────────────────────────────────────

    private void BuildTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "ThinkPad Keyboard Backlight",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };
        _notifyIcon.MouseDoubleClick += (_, _) => RestoreBacklight(true);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        // Read from registry when building so menu always reflects persisted state.
        var runAtStartupEnabled = SettingsManager.GetRunAtStartup();
        var autoRestoreEnabled = SettingsManager.GetAutoRestore();
        var restoreLevel = SettingsManager.GetRestoreLevel();

        var restoreNow = new ToolStripMenuItem("Restore Now");
        restoreNow.Click += (_, _) => RestoreBacklight(true);

        var autoRestoreItem = new ToolStripMenuItem("Auto Restore")
        {
            CheckOnClick = false,
            Checked = autoRestoreEnabled
        };
        autoRestoreItem.Click += (_, _) =>
        {
            var newState = !autoRestoreEnabled;
            SettingsManager.SetAutoRestore(newState);
            RebuildTrayMenu();
        };

        // ── Restore To submenu (Last / Dim / Full) ──
        var restoreToLast = new ToolStripMenuItem("Last")
        {
            CheckOnClick = false,
            Checked = restoreLevel == 0
        };
        restoreToLast.Click += (_, _) =>
        {
            SettingsManager.SetRestoreLevel(0);
            RebuildTrayMenu();
        };

        var restoreToDim = new ToolStripMenuItem("Dim")
        {
            CheckOnClick = false,
            Checked = restoreLevel == 1
        };
        restoreToDim.Click += (_, _) =>
        {
            SettingsManager.SetRestoreLevel(1);
            RebuildTrayMenu();
        };

        var restoreToFull = new ToolStripMenuItem("Full")
        {
            CheckOnClick = false,
            Checked = restoreLevel == 2
        };
        restoreToFull.Click += (_, _) =>
        {
            SettingsManager.SetRestoreLevel(2);
            RebuildTrayMenu();
        };

        var restoreToMenu = new ToolStripMenuItem("Restore To");
        restoreToMenu.DropDownItems.Add(restoreToLast);
        restoreToMenu.DropDownItems.Add(restoreToDim);
        restoreToMenu.DropDownItems.Add(restoreToFull);

        var runAtStartupItem = new ToolStripMenuItem("Run at Startup")
        {
            CheckOnClick = false,
            Checked = runAtStartupEnabled
        };
        runAtStartupItem.Click += (_, _) =>
        {
            var newState = !runAtStartupEnabled;
            SettingsManager.SetRunAtStartup(newState);
            RebuildTrayMenu();
        };

        var info = new ToolStripMenuItem("Info...");
        info.Click += (_, _) => ShowInfo();

        var about = new ToolStripMenuItem("About...");
        about.Click += (_, _) => ShowAbout();

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApplication();

        var menu = new ContextMenuStrip
        {
            Font = SystemFonts.MenuFont,
            ShowImageMargin = true,
            ShowCheckMargin = true,
            RenderMode = ToolStripRenderMode.Professional
        };
        menu.Items.Add(restoreNow);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoRestoreItem);
        menu.Items.Add(restoreToMenu);
        menu.Items.Add(runAtStartupItem);
        menu.Items.Add(info);
        menu.Items.Add(about);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        ApplyThemeToContextMenu(menu);
        menu.Opening += (_, _) => ApplyThemeToContextMenu(menu);

        return menu;
    }

    private void RebuildTrayMenu()
    {
        if (_notifyIcon == null) return;

        var oldMenu = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = BuildContextMenu();
        oldMenu?.Dispose();
    }

    private static void ApplyThemeToContextMenu(ContextMenuStrip menu)
    {
        var darkMode = IsSystemDarkMode();
        var renderer = new ToolStripProfessionalRenderer(new ThemedColorTable(darkMode));
        var bg = darkMode ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xFA, 0xFA, 0xFA);
        var fg = darkMode ? Color.FromArgb(0xF2, 0xF2, 0xF2) : Color.FromArgb(0x1C, 0x1C, 0x1C);

        menu.Renderer = renderer;
        menu.BackColor = bg;
        menu.ForeColor = fg;

        if (menu.IsHandleCreated)
            _ = SetWindowTheme(menu.Handle, darkMode ? "DarkMode_Explorer" : "Explorer", null);

        // Apply theming to any submenu dropdowns.
        foreach (ToolStripItem item in menu.Items)
            if (item is ToolStripMenuItem { HasDropDownItems: true } parent)
            {
                parent.DropDown.Renderer = renderer;
                parent.DropDown.BackColor = bg;
                parent.DropDown.ForeColor = fg;
            }
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icoPath))
                return new Icon(icoPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load tray icon: {ex.Message}");
        }

        return SystemIcons.Application;
    }

    // ── actions ──────────────────────────────────────────────────

    private void RestoreBacklight(bool force = false)
    {
        if (!force && !SettingsManager.GetAutoRestore())
        {
            Debug.WriteLine("RestoreBacklight skipped: auto restore is disabled");
            return;
        }

        if (!SessionHelper.IsConsoleSession())
        {
            Debug.WriteLine("RestoreBacklight skipped: not a physical console session");
            return;
        }

        _ = Task.Run(() =>
        {
            var level = SettingsManager.GetEffectiveRestoreLevel();
            BacklightController.SetBacklightLevel((BacklightController.BacklightLevel)level);
        });
    }

    /// <summary>
    ///     Resume-only restore: sets a different level first to break the IBMPmDrv
    ///     post-sleep desync, waits briefly, then sets the real target.
    /// </summary>
    private void KickAndRestoreBacklight()
    {
        if (!SettingsManager.GetAutoRestore())
        {
            Debug.WriteLine("KickAndRestoreBacklight skipped: auto restore is disabled");
            return;
        }

        if (!SessionHelper.IsConsoleSession())
        {
            Debug.WriteLine("KickAndRestoreBacklight skipped: not a physical console session");
            return;
        }

        _ = Task.Run(() =>
        {
            var target = SettingsManager.GetEffectiveRestoreLevel();

            // Kick with Dim or Full (never Off) to break the post-sleep desync.
            // Use the level adjacent to the target so the final set is a real change.
            var kick = target == (int)BacklightController.BacklightLevel.Full
                ? BacklightController.BacklightLevel.Dim
                : BacklightController.BacklightLevel.Full;

            Debug.WriteLine($"KickAndRestoreBacklight: kick={kick} target={target}");
            BacklightController.SetBacklightLevel(kick);
            Thread.Sleep(500);
            BacklightController.SetBacklightLevel((BacklightController.BacklightLevel)target);
        });
    }

    private static void ShowInfo()
    {
        var info = BuildInfoString();
        var darkMode = IsSystemDarkMode();

        var textBox = new TextBox
        {
            Text = info,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 13,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(12, 12, 12, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        // Keep text selection-friendly while matching the current OS theme.
        ApplyThemeToTextBox(textBox, darkMode);

        var copyButton = new Button
        {
            Content = "Copy to Clipboard",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
            Padding = new Thickness(16, 6, 16, 6)
        };

        ApplyThemeToButton(copyButton, darkMode);

        var panel = new DockPanel();
        DockPanel.SetDock(copyButton, Dock.Bottom);
        panel.Children.Add(copyButton);
        panel.Children.Add(textBox);

        var window = new Window
        {
            Title = "ThinkPad Backlight Tray — Info",
            Content = panel,
            Width = 520,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.CanResize
        };

        ApplyThemeToWindow(window, darkMode);

        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(info);
                copyButton.Content = "Copied ✓";
            }
            catch
            {
                copyButton.Content = "Copy failed";
            }
        };

        window.Show();
    }

    private static void ShowAbout()
    {
        var darkMode = IsSystemDarkMode();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v1.0.0";

        // ── title ────────────────────────────────────────────────────────────
        var titleText = new TextBlock
        {
            Text = "ThinkPad Backlight Tray",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var versionText = new TextBlock
        {
            Text = versionString,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var descText = new TextBlock
        {
            Text = "Automatically restores ThinkPad keyboard backlight\nafter lid-close, power, and display events.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var licenseText = new TextBlock
        {
            Text = "Released under the MIT License.",
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // ── close button ─────────────────────────────────────────────────────
        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(20, 6, 20, 6),
            IsDefault = true,
            IsCancel = true
        };
        ApplyThemeToButton(closeButton, darkMode);

        // ── layout ───────────────────────────────────────────────────────────
        var stack = new StackPanel
        {
            Margin = new Thickness(24, 24, 24, 20)
        };
        stack.Children.Add(titleText);
        stack.Children.Add(versionText);
        stack.Children.Add(descText);
        stack.Children.Add(licenseText);
        stack.Children.Add(closeButton);

        var window = new Window
        {
            Title = "About ThinkPad Backlight Tray",
            Content = stack,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize
        };

        ApplyThemeToWindow(window, darkMode);

        // propagate foreground to text blocks after theme sets window foreground
        var fg = window.Foreground;
        titleText.Foreground = fg;
        versionText.Foreground = fg;
        descText.Foreground = fg;
        licenseText.Foreground = fg;

        closeButton.Click += (_, _) => window.Close();
        window.Show();
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            var value = key?.GetValue("AppsUseLightTheme", 1);
            return Convert.ToInt32(value) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyThemeToWindow(Window window, bool darkMode)
    {
        var bg = darkMode
            ? System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)
            : System.Windows.Media.Color.FromRgb(0xF9, 0xF9, 0xF9);
        var fg = darkMode
            ? System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2)
            : System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1C);

        window.Background = new SolidColorBrush(bg);
        window.Foreground = new SolidColorBrush(fg);

        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // DWM non-client area dark mode integration.
            var useDark = darkMode ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmaUseImmersiveDarkMode, ref useDark, sizeof(int));

            // UxTheme integration for native-themed window parts.
            _ = SetWindowTheme(hwnd, darkMode ? "DarkMode_Explorer" : "Explorer", null);
        };
    }

    private static void ApplyThemeToTextBox(TextBox textBox, bool darkMode)
    {
        textBox.Background = new SolidColorBrush(
            darkMode
                ? System.Windows.Media.Color.FromRgb(0x2B, 0x2B, 0x2B)
                : System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
        textBox.Foreground = new SolidColorBrush(
            darkMode
                ? System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2)
                : System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1C));
        textBox.CaretBrush = textBox.Foreground;
    }

    private static void ApplyThemeToButton(Button button, bool darkMode)
    {
        button.Background = new SolidColorBrush(
            darkMode
                ? System.Windows.Media.Color.FromRgb(0x31, 0x31, 0x31)
                : System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3));
        button.Foreground = new SolidColorBrush(
            darkMode
                ? System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2)
                : System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1C));
        button.BorderBrush = new SolidColorBrush(
            darkMode
                ? System.Windows.Media.Color.FromRgb(0x4A, 0x4A, 0x4A)
                : System.Windows.Media.Color.FromRgb(0xD0, 0xD0, 0xD0));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    internal static string BuildInfoString()
    {
        var savedLevel = SettingsManager.GetBacklightLevel();
        var currentLevel = BacklightController.GetBacklightLevel();
        var autoRestore = SettingsManager.GetAutoRestore();
        var restoreLevel = SettingsManager.GetRestoreLevel();
        var runAtStartup = SettingsManager.GetRunAtStartup();
        var isConsoleSession = SessionHelper.IsConsoleSession();

        var sb = new StringBuilder();
        sb.AppendLine("ThinkPad Backlight Tray");
        sb.AppendLine();
        sb.AppendLine($"Machine: {Environment.MachineName}");
        sb.AppendLine($"User: {Environment.UserName}");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"Process: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        sb.AppendLine($"CLR: {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine("Settings:");
        sb.AppendLine($"  Saved Backlight Level: {FormatLevel(savedLevel)} ({savedLevel})");
        sb.AppendLine(
            $"  Current Backlight Level: {(currentLevel.HasValue ? $"{FormatLevel((int)currentLevel.Value)} ({(int)currentLevel.Value})" : "Unknown")}");
        sb.AppendLine($"  Auto Restore: {autoRestore}");
        sb.AppendLine($"  Restore To: {FormatRestoreMode(restoreLevel)}");
        sb.AppendLine($"  Run at Startup: {runAtStartup}");
        sb.AppendLine($"  Console Session: {isConsoleSession}");
        sb.AppendLine();
        sb.AppendLine(BacklightController.GetProviderStatusSummary());
        return sb.ToString();

        static string FormatLevel(int level)
        {
            return level switch
            {
                0 => "Off",
                1 => "Dim",
                2 => "Full",
                _ => "Unknown"
            };
        }

        static string FormatRestoreMode(int mode)
        {
            return mode switch
            {
                0 => "Last",
                1 => "Dim",
                2 => "Full",
                _ => "Unknown"
            };
        }
    }

    private void ExitApplication()
    {
        _eventMonitor?.Stop();
        _eventMonitor = null;

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        Shutdown();
    }

    private sealed class ThemedColorTable(bool darkMode) : ProfessionalColorTable
    {
        private readonly bool _dark = darkMode;

        private Color Bg => _dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xFA, 0xFA, 0xFA);
        private Color Border => _dark ? Color.FromArgb(0x44, 0x44, 0x44) : Color.FromArgb(0xD0, 0xD0, 0xD0);
        private Color ItemHover => _dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xE8, 0xE8, 0xE8);
        private Color ItemPressed => _dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.FromArgb(0xDE, 0xDE, 0xDE);
        private Color Sep => _dark ? Color.FromArgb(0x4A, 0x4A, 0x4A) : Color.FromArgb(0xDB, 0xDB, 0xDB);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => ItemHover;
        public override Color MenuItemSelectedGradientBegin => ItemHover;
        public override Color MenuItemSelectedGradientEnd => ItemHover;
        public override Color MenuItemPressedGradientBegin => ItemPressed;
        public override Color MenuItemPressedGradientMiddle => ItemPressed;
        public override Color MenuItemPressedGradientEnd => ItemPressed;
        public override Color SeparatorDark => Sep;
        public override Color SeparatorLight => Sep;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
    }
}