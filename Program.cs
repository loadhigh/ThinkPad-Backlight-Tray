using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThinkPadBacklightTray;

internal static class Program
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [STAThread]
    public static int Main(string[] args)
    {
        // Check for --debug before processing other args.
        var debugMode = false;
        var filteredArgs = new List<string>();
        foreach (var a in args)
            if (a.TrimStart('-', '/').ToLowerInvariant() == "debug")
                debugMode = true;
            else
                filteredArgs.Add(a);
        args = filteredArgs.ToArray();

        if (args.Length > 0)
        {
            var hasConsole = AttachConsole(ATTACH_PARENT_PROCESS);
            if (!hasConsole)
                hasConsole = AllocConsole();

            try
            {
                if (hasConsole)
                {
                    // Re-open stdout/stderr after attaching to a console.
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                    Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                }

                if (TryHandleCommand(args))
                    return 0;
            }
            finally
            {
                if (!debugMode) FreeConsole();
            }
        }

        if (debugMode)
        {
            // Allocate a new console window for the tray session.
            AllocConsole();
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

            // Route all Debug.WriteLine calls to the console.
            Trace.Listeners.Add(new TextWriterTraceListener(Console.Out) { Name = "DebugConsole" });
            Trace.AutoFlush = true;

            Debug.WriteLine("=== ThinkPad Backlight Tray — debug mode ===");
        }

        // Enable WinForms visual styles and DPI scaling before any controls are created.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        new App().Run();
        return 0;
    }

    /// <summary>
    ///     Handle CLI switches that mirror tray menu actions.
    ///     Returns true if a command was handled (caller should exit).
    /// </summary>
    private static bool TryHandleCommand(string[] args)
    {
        var cmd = args[0].TrimStart('-', '/').ToLowerInvariant();

        switch (cmd)
        {
            case "off":
                InitAndSetLevel(0);
                return true;

            case "dim":
                InitAndSetLevel(1);
                return true;

            case "full":
                InitAndSetLevel(2);
                return true;

            case "restore":
                SettingsManager.Initialize();
                BacklightController.Initialize();
                if (SessionHelper.IsConsoleSession())
                {
                    var level = SettingsManager.GetEffectiveRestoreLevel();
                    BacklightController.SetBacklightLevel((BacklightController.BacklightLevel)level);
                }

                return true;

            case "restore-to":
                SettingsManager.Initialize();
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Missing argument. Usage: --restore-to <last|dim|full>");
                    return true;
                }

                switch (args[1].ToLowerInvariant())
                {
                    case "last":
                        SettingsManager.SetRestoreLevel(0);
                        break;
                    case "dim":
                        SettingsManager.SetRestoreLevel(1);
                        break;
                    case "full":
                        SettingsManager.SetRestoreLevel(2);
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown restore-to value: {args[1]}. Use last, dim, or full.");
                        break;
                }

                return true;

            case "startup-on":
                SettingsManager.Initialize();
                SettingsManager.SetRunAtStartup(true);
                return true;

            case "startup-off":
                SettingsManager.Initialize();
                SettingsManager.SetRunAtStartup(false);
                return true;

            case "info":
                SettingsManager.Initialize();
                BacklightController.Initialize();
                Console.WriteLine(App.BuildInfoString());
                return true;

            case "help":
            case "h":
            case "?":
                PrintHelp();
                return true;

            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                PrintHelp();
                return true;
        }
    }

    private static void InitAndSetLevel(int level)
    {
        SettingsManager.Initialize();
        BacklightController.Initialize();
        SettingsManager.SetBacklightLevel(level);
        BacklightController.SetBacklightLevel((BacklightController.BacklightLevel)level);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            ThinkPad Backlight Tray - CLI

            Usage: ThinkPad-Backlight-Tray.exe [command]

            Commands:
              --off          Set backlight to Off and exit
              --dim          Set backlight to Dim and exit
              --full         Set backlight to Full and exit
              --restore      Restore backlight level and exit
              --restore-to <last|dim|full>
                             Set which level to restore to
              --startup-on   Enable Run at Startup and exit
              --startup-off  Disable Run at Startup and exit
              --info         Print diagnostic info and exit
              --debug        Launch tray with live debug logging to a console window
              --help         Show this help

            No arguments: launch tray application.
            """);
    }
}