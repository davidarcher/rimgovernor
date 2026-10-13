using System.Runtime.InteropServices;
using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Launcher;

internal static partial class Program
{
    private const string Title = "RimGovernor launcher";

    [STAThread]
    private static int Main()
    {
        HideConsole();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var repo = FindRepo();
        if (repo is null)
        {
            return Fatal("RimGovernorLauncher.exe must sit inside the RimGovernor checkout (no .git found above it).");
        }

        using var app = new LauncherApp(repo);
        Application.Run(new MainForm(app));
        return 0;
    }

    /// <summary>The checkout the launcher belongs to: the one enclosing its executable, else the working directory's.</summary>
    private static string? FindRepo()
    {
        if (Environment.ProcessPath is { } exe && Path.GetDirectoryName(exe) is { } dir && NativeSourceInputs.FindRepo(dir) is { } found)
        {
            return found;
        }

        return NativeSourceInputs.FindRepo(Directory.GetCurrentDirectory());
    }

    private static int Fatal(string message)
    {
        _ = MessageBox.Show(message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }

    /// <summary>
    /// Gives this GUI process a hidden console: every console child it (or the setup it runs) spawns inherits it
    /// instead of flashing a window of its own.
    /// </summary>
    private static void HideConsole()
    {
        _ = AllocConsole();
        var window = GetConsoleWindow();
        if (window != 0)
        {
            _ = ShowWindow(window, 0);
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);
}
