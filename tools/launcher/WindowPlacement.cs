using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RimGovernor.Launcher;

/// <summary>A window's outer bounds in screen pixels (the file keeps the X, Y, W, H names; the read is case-insensitive).</summary>
public readonly record struct WindowRect(int X, int Y, int W, int H)
{
    /// <summary>Enough of the rectangle lies inside the virtual desktop to grab and drag.</summary>
    public bool OnScreen(Rectangle desktop) =>
        W >= 200 && H >= 200 && X + W > desktop.X + 80 && X < desktop.X + desktop.Width - 80 && Y + 80 < desktop.Y + desktop.Height && Y >= desktop.Y - 20;

    /// <summary>A real window, not a screen-sized one.</summary>
    public bool Windowed(Rectangle desktop, Size primary) => OnScreen(desktop) && (W < primary.Width || H < primary.Height);

    public static WindowRect? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<WindowRect>(File.ReadAllBytes(path), LauncherJson.Compact);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, LauncherJson.Compact));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A window position is a convenience; losing one write loses nothing else.
        }
    }
}

/// <summary>
/// RimWorld is a Unity player and does not remember where its window was, so the launcher does: whenever the game's
/// window appears it is moved to the bounds it last had, and the bounds are saved as the player moves it. Fullscreen or
/// screen-sized windows are neither saved nor restored.
/// </summary>
internal sealed partial class GameWindowWatcher(string path) : IDisposable
{
    private const uint NoZOrder = 0x4;
    private const uint NoActivate = 0x10;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private nint _known;
    private WindowRect _last;

    public void Start()
    {
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Dispose() => _timer.Dispose();

    private static nint GameWindow()
    {
        foreach (var process in Process.GetProcessesByName("RimWorld"))
        {
            using (process)
            {
                if (process.MainWindowHandle != 0 && IsWindowVisible(process.MainWindowHandle))
                {
                    return process.MainWindowHandle;
                }
            }
        }

        return 0;
    }

    private void Tick()
    {
        var hwnd = GameWindow();
        if (hwnd == 0)
        {
            _known = 0;
            return;
        }

        var desktop = SystemInformation.VirtualScreen;
        var primary = SystemInformation.PrimaryMonitorSize;
        if (hwnd != _known)
        {
            (_known, _last) = (hwnd, default);
            if (WindowRect.Load(path) is { } saved && saved.Windowed(desktop, primary))
            {
                _ = SetWindowPos(hwnd, 0, saved.X, saved.Y, saved.W, saved.H, NoZOrder | NoActivate);
                _last = saved;
            }

            return;
        }

        if (IsIconic(hwnd) || !GetWindowRect(hwnd, out var rc))
        {
            return;
        }

        var now = new WindowRect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
        if (now == _last || !now.Windowed(desktop, primary))
        {
            return;
        }

        _last = now;
        now.Save(path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);
}
