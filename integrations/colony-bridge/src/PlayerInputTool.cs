using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using UnityEngine;
using Verse;
using HarmonyLib;

namespace HomeBridge.BridgeTools
{
    // Only private, process-owned X displays are eligible. No desktop focus changes.
    public sealed class PlayerInputTool
    {
        [Tool("home/player_input", Description = "Explicit local player input on the private rendered worker. Never a model gameplay tool.")]
        public async Task<object> Input(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action, string owner, string source = "", long frame = 0, long order = 0,
            string kind = "", int x = 0, int y = 0, int button = 0, string key = "", int delta = 0)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                try { return PrivatePlayerInput.Apply(action, owner, source, frame, order, kind, x, y, button, key, delta); }
                catch (ArgumentException error) { return (object)new { success = false, message = error.Message }; }
                catch (InvalidOperationException error) { return (object)new { success = false, message = error.Message }; }
            }, cancellationToken);
        }
    }

    public sealed class PlayerFrame
    {
        public Game Game;
        public Map Map;
        public Matrix4x4 View, Projection;
        public int Width, Height;
        public double Captured;
        public string Windows;
        public long UiRevision;
        public int Selection;
        static long uiRevision;
        internal static long CurrentUiRevision => uiRevision;
        static bool observingUi;

        public static void ObserveUi()
        {
            if (observingUi) return;
            new Harmony("rimgovernor.player-frame-ui").Patch(AccessTools.Method(typeof(Root), "OnGUI"),
                prefix: new HarmonyMethod(typeof(PlayerFrame), nameof(BeforeGui)));
            observingUi = true;
        }
        static void BeforeGui()
        {
            var current = Event.current;
            if (current != null && (current.type == EventType.MouseDown || current.type == EventType.MouseUp
                || current.type == EventType.ScrollWheel || (current.type == EventType.KeyDown
                    && current.keyCode != KeyCode.LeftShift && current.keyCode != KeyCode.RightShift
                    && current.keyCode != KeyCode.LeftControl && current.keyCode != KeyCode.RightControl
                    && current.keyCode != KeyCode.LeftAlt && current.keyCode != KeyCode.RightAlt))) uiRevision++;
        }
        public bool CurrentUi() => UiRevision == uiRevision;
        public static readonly Dictionary<long, PlayerFrame> Frames = new Dictionary<long, PlayerFrame>();
        public static string Source;

        static string WindowState() => string.Join("|", Find.WindowStack.Windows
            .Where(w => !(w is ImmediateWindow))
            .Select(w => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(w) + ":" + w.windowRect));

        public static PlayerFrame Capture(double captured)
        {
            if (Current.Game == null || Find.CurrentMap == null || Find.Camera == null) return null;
            return new PlayerFrame { Game = Current.Game, Map = Find.CurrentMap,
                View = Find.Camera.worldToCameraMatrix, Projection = Find.Camera.projectionMatrix,
                Width = Screen.width, Height = Screen.height, Captured = captured, Windows = WindowState(), UiRevision = uiRevision,
                Selection = Find.Selector.SelectedObjects.Aggregate(17, (hash, item) => unchecked(hash * 31 +
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item))) };
        }

        public static void Remember(string source, long sequence, PlayerFrame frame)
        {
            if (Source != source) { Frames.Clear(); Source = source; }
            if (frame == null) return;
            Frames[sequence] = frame;
            foreach (long old in Frames.Keys.Where(k => k < sequence - 90).ToArray()) Frames.Remove(old);
        }

        public bool SameScene() => ReferenceEquals(Game, Current.Game) && ReferenceEquals(Map, Find.CurrentMap)
            && Width == Screen.width && Height == Screen.height;
        public bool Matches() => SameScene() && Find.Camera != null && View == Find.Camera.worldToCameraMatrix
            && Projection == Find.Camera.projectionMatrix && Width == Screen.width && Height == Screen.height
            && Windows == WindowState();
    }

    public sealed class PrivatePlayerInput : MonoBehaviour
    {
        static PrivatePlayerInput instance;
        IntPtr display;
        string owner;
        float until;
        long lastOrder;
        Game game;
        Map map;
        readonly HashSet<uint> buttons = new HashSet<uint>();
        readonly HashSet<uint> keys = new HashSet<uint>();
        FileStream channelFile;
        MemoryMappedFile channelMap;
        MemoryMappedViewAccessor channel;
        string channelName;
        long command;
        [DllImport("libc", SetLastError = true)] static extern int flock(int fd, int operation);

        [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(string name);
        [DllImport("libX11.so.6")] static extern int XCloseDisplay(IntPtr d);
        [DllImport("libX11.so.6")] static extern int XFlush(IntPtr d);
        [DllImport("libX11.so.6")] static extern int XGetInputFocus(IntPtr d, out UIntPtr w, out int revert);
        [DllImport("libX11.so.6")] static extern int XSetInputFocus(IntPtr d, UIntPtr w, int revert, UIntPtr time);
        [DllImport("libX11.so.6")] static extern UIntPtr XDefaultRootWindow(IntPtr d);
        [DllImport("libX11.so.6")] static extern UIntPtr XInternAtom(IntPtr d, string name, bool only);
        [DllImport("libX11.so.6")] static extern int XGetWindowProperty(IntPtr d, UIntPtr w, UIntPtr property,
            IntPtr offset, IntPtr length, bool delete, UIntPtr requestedType, out UIntPtr type,
            out int format, out UIntPtr count, out UIntPtr remaining, out IntPtr data);
        [DllImport("libX11.so.6")] static extern int XFree(IntPtr data);
        [DllImport("libX11.so.6")] static extern int XQueryTree(IntPtr d, UIntPtr w, out UIntPtr root,
            out UIntPtr parent, out IntPtr children, out uint count);
        [DllImport("libX11.so.6")] static extern int XTranslateCoordinates(IntPtr d, UIntPtr src, UIntPtr dest,
            int x, int y, out int rx, out int ry, out UIntPtr child);
        [DllImport("libX11.so.6")] static extern int XGetGeometry(IntPtr d, UIntPtr w, out UIntPtr root,
            out int x, out int y, out uint width, out uint height, out uint border, out uint depth);
        [DllImport("libX11.so.6")] static extern UIntPtr XStringToKeysym(string key);
        [DllImport("libX11.so.6")] static extern byte XKeysymToKeycode(IntPtr d, UIntPtr key);
        [DllImport("libXtst.so.6")] static extern int XTestFakeMotionEvent(IntPtr d, int screen, int x, int y, UIntPtr delay);
        [DllImport("libXtst.so.6")] static extern int XTestFakeButtonEvent(IntPtr d, uint button, bool down, UIntPtr delay);
        [DllImport("libXtst.so.6")] static extern int XTestFakeKeyEvent(IntPtr d, uint key, bool down, UIntPtr delay);

        bool Owns(UIntPtr window)
        {
            var atom = XInternAtom(display, "_NET_WM_PID", true);
            if (atom == UIntPtr.Zero || window.ToUInt64() <= 1) return false;
            XGetWindowProperty(display, window, atom, IntPtr.Zero, new IntPtr(1), false, UIntPtr.Zero,
                out UIntPtr type, out int format, out UIntPtr count, out UIntPtr remaining, out IntPtr data);
            try
            {
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    return data != IntPtr.Zero && format == 32 && count.ToUInt64() == 1
                        && Marshal.ReadIntPtr(data).ToInt64() == process.Id;
            }
            finally { if (data != IntPtr.Zero) XFree(data); }
        }

        public static UIntPtr VideoWindow(out IntPtr connection)
        {
            var self = Get(); connection = self.display;
            var root = XDefaultRootWindow(connection);
            XQueryTree(connection, root, out UIntPtr ignored, out UIntPtr parent, out IntPtr children, out uint count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var candidate = new UIntPtr(unchecked((ulong)Marshal.ReadIntPtr(children, i * IntPtr.Size).ToInt64()));
                    if (self.Owns(candidate)) return candidate;
                }
            }
            finally { if (children != IntPtr.Zero) XFree(children); }
            throw new InvalidOperationException("Private game window is unavailable");
        }

        void AcquirePrivateFocus()
        {
            // Xvfb has no window manager. Explicit handoff focuses only this PID's
            // window on the private display; an existing different focus is refused.
            XGetInputFocus(display, out UIntPtr focused, out int revert);
            var root = XDefaultRootWindow(display);
            if (focused.ToUInt64() > 1 && focused != root) return;
            XQueryTree(display, root, out UIntPtr ignored, out UIntPtr parent, out IntPtr children, out uint count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var candidate = new UIntPtr(unchecked((ulong)Marshal.ReadIntPtr(children, i * IntPtr.Size).ToInt64()));
                    if (!Owns(candidate)) continue;
                    XSetInputFocus(display, candidate, 1, UIntPtr.Zero);
                    XFlush(display);
                    return;
                }
            }
            finally { if (children != IntPtr.Zero) XFree(children); }
        }

        UIntPtr Focus(out int offsetX, out int offsetY)
        {
            XGetInputFocus(display, out UIntPtr window, out int revert);
            var root = XDefaultRootWindow(display);
            var atom = XInternAtom(display, "_NET_WM_PID", true);
            if (window.ToUInt64() <= 1 || atom == UIntPtr.Zero)
                throw new InvalidOperationException("Private game window has not acquired input focus");
            for (int level = 0; level < 8 && window != UIntPtr.Zero && window != root; level++)
            {
                XGetWindowProperty(display, window, atom, IntPtr.Zero, new IntPtr(1), false, UIntPtr.Zero,
                    out UIntPtr type, out int format, out UIntPtr count, out UIntPtr remaining, out IntPtr data);
                long pid = 0;
                try { if (data != IntPtr.Zero && format == 32 && count.ToUInt64() > 0) pid = Marshal.ReadIntPtr(data).ToInt64(); }
                finally { if (data != IntPtr.Zero) XFree(data); }
                if (pid == System.Diagnostics.Process.GetCurrentProcess().Id)
                {
                    XGetGeometry(display, window, out UIntPtr ignored, out int wx, out int wy,
                        out uint width, out uint height, out uint border, out uint depth);
                    if (width != Screen.width || height != Screen.height) break;
                    XTranslateCoordinates(display, window, root, 0, 0, out offsetX, out offsetY, out UIntPtr child);
                    return window;
                }
                XQueryTree(display, window, out UIntPtr root2, out UIntPtr parent, out IntPtr children, out uint n);
                if (children != IntPtr.Zero) XFree(children);
                window = parent;
            }
            throw new InvalidOperationException("Private game window does not own input focus or changed size");
        }

        static PrivatePlayerInput Get()
        {
            if (Application.platform != RuntimePlatform.LinuxPlayer || Application.isBatchMode
                || Environment.GetEnvironmentVariable("RIMGOVERNOR_PRIVATE_DISPLAY") != "1")
                throw new InvalidOperationException("Direct input requires the isolated rendered Docker worker");
            if (instance == null)
            {
                PlayerFrame.ObserveUi();
                instance = new GameObject("RimGovernorPlayerInput").AddComponent<PrivatePlayerInput>();
                DontDestroyOnLoad(instance.gameObject);
                instance.display = XOpenDisplay(null);
                if (instance.display == IntPtr.Zero)
                {
                    Destroy(instance.gameObject); instance = null;
                    throw new InvalidOperationException("Private display unavailable");
                }
            }
            return instance;
        }

        public static object Apply(string action, string requestedOwner, string source, long frame, long order,
            string kind, int x, int y, int button, string key, int delta)
        {
            var self = Get();
            if (string.IsNullOrEmpty(requestedOwner) || requestedOwner.Length > 100) throw new ArgumentException("Invalid input owner");
            if (action == "release" && (self.owner == requestedOwner || self.owner == null))
            { self.Release(); return new { success = true, released = true }; }
            if (action == "take")
            {
                if (self.owner != null && Time.realtimeSinceStartup < self.until && self.owner != requestedOwner)
                    throw new InvalidOperationException("Another player owns native input");
                self.AcquirePrivateFocus();
                self.Focus(out int ox, out int oy);
                self.Release();
                self.owner = requestedOwner; self.game = Current.Game; self.map = Find.CurrentMap; self.lastOrder = 0;
                self.OpenChannel();
            }
            if (self.owner != requestedOwner || !ReferenceEquals(self.game, Current.Game)
                || !ReferenceEquals(self.map, Find.CurrentMap) || (action != "take" && Time.realtimeSinceStartup >= self.until))
            { if (Time.realtimeSinceStartup >= self.until || !ReferenceEquals(self.game, Current.Game) || !ReferenceEquals(self.map, Find.CurrentMap)) self.Release();
                throw new InvalidOperationException("Native player lease changed or expired"); }
            if (action == "release") { self.Release(); return new { success = true, released = true }; }
            if (action != "take" && action != "renew" && action != "event") throw new ArgumentException("Unknown input action");
            if (action == "event")
            {
                if (order != self.lastOrder + 1) throw new InvalidOperationException("Input order is stale or has a gap");
                self.lastOrder = order;
                bool keyRelease = kind == "keyUp" && self.keys.Contains(self.Key(key));
                // Right-down opens native context menus. Its matching release must
                // still clear the held button after that expected window change.
                bool contextRelease = kind == "up" && button == 2 && self.buttons.Contains(3);
                if (source != PlayerFrame.Source || !PlayerFrame.Frames.TryGetValue(frame, out PlayerFrame viewed))
                    throw new InvalidOperationException("Displayed frame source or sequence changed; input was not sent");
                if ((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds - viewed.Captured > .75)
                    throw new InvalidOperationException("Displayed frame expired; input was not sent");
                if (!(keyRelease || contextRelease ? viewed.SameScene() : viewed.Matches()))
                    throw new InvalidOperationException("Displayed view changed: scene=" + viewed.SameScene()
                        + ", camera=" + (Find.Camera != null && viewed.View == Find.Camera.worldToCameraMatrix
                            && viewed.Projection == Find.Camera.projectionMatrix) + "; input was not sent");
                if ((kind == "down" || kind == "wheel") && !viewed.CurrentUi())
                    throw new InvalidOperationException("Native UI changed after the displayed frame; input was not sent");
                self.Focus(out int ox, out int oy);
                if (x < 0 || y < 0 || x >= viewed.Width || y >= viewed.Height) throw new ArgumentException("Pointer outside displayed frame");
                if (kind == "move" || kind == "down" || kind == "up" || kind == "wheel")
                    XTestFakeMotionEvent(self.display, -1, x + ox, y + oy, UIntPtr.Zero);
                if (kind == "down" || kind == "up")
                {
                    if (button < 0 || button > 2) throw new ArgumentException("Invalid pointer button");
                    uint native = button == 0 ? 1u : button == 1 ? 2u : 3u;
                    if (kind == "down") self.buttons.Add(native); else self.buttons.Remove(native);
                    XTestFakeButtonEvent(self.display, native, kind == "down", UIntPtr.Zero);
                }
                else if (kind == "wheel")
                {
                    if (delta != -1 && delta != 1) throw new ArgumentException("Invalid scroll direction");
                    uint native = delta < 0 ? 4u : 5u;
                    XTestFakeButtonEvent(self.display, native, true, UIntPtr.Zero);
                    XTestFakeButtonEvent(self.display, native, false, UIntPtr.Zero);
                }
                else if (kind == "keyDown" || kind == "keyUp")
                {
                    uint code = self.Key(key);
                    if (kind == "keyDown") self.keys.Add(code); else self.keys.Remove(code);
                    XTestFakeKeyEvent(self.display, code, kind == "keyDown", UIntPtr.Zero);
                }
                else if (kind != "move") throw new ArgumentException("Unknown input event");
                XFlush(self.display);
            }
            self.until = Time.realtimeSinceStartup + 8;
            return new { success = true, order = self.lastOrder, heldButtons = self.buttons.Count, heldKeys = self.keys.Count,
                channel = self.channelName, capacity = 4096 };
        }

        uint Key(string code)
        {
            string name;
            if (code.Length == 4 && code.StartsWith("Key") && code[3] >= 'A' && code[3] <= 'Z') name = code.Substring(3).ToLowerInvariant();
            else if (code.Length == 6 && code.StartsWith("Digit") && char.IsDigit(code[5])) name = code.Substring(5);
            else if (code.StartsWith("F") && int.TryParse(code.Substring(1), out int function) && function >= 1 && function <= 12) name = code;
            else
            {
                var names = new Dictionary<string, string> { { "Escape", "Escape" }, { "Enter", "Return" }, { "Space", "space" },
                    { "Backspace", "BackSpace" }, { "Delete", "Delete" }, { "Tab", "Tab" }, { "ArrowLeft", "Left" },
                    { "ArrowRight", "Right" }, { "ArrowUp", "Up" }, { "ArrowDown", "Down" }, { "ShiftLeft", "Shift_L" },
                    { "ShiftRight", "Shift_R" }, { "ControlLeft", "Control_L" }, { "ControlRight", "Control_R" },
                    { "AltLeft", "Alt_L" }, { "AltRight", "Alt_R" }, { "Comma", "comma" }, { "Period", "period" },
                    { "Minus", "minus" }, { "Equal", "equal" }, { "Home", "Home" }, { "End", "End" },
                    { "PageUp", "Page_Up" }, { "PageDown", "Page_Down" }, { "BracketLeft", "bracketleft" },
                    { "BracketRight", "bracketright" }, { "Slash", "slash" }, { "Backslash", "backslash" },
                    { "Semicolon", "semicolon" }, { "Quote", "apostrophe" }, { "Backquote", "grave" } };
                if (!names.TryGetValue(code, out name)) throw new ArgumentException("Unsupported player key");
            }
            uint value = XKeysymToKeycode(display, XStringToKeysym(name));
            if (value == 0) throw new InvalidOperationException("Key unavailable in private display keymap");
            return value;
        }

        void Release()
        {
            if (display == IntPtr.Zero) return;
            // Cancel an unfinished designation before releasing the button that would commit it.
            if (buttons.Count > 0 && ReferenceEquals(game, Current.Game) && Find.DesignatorManager != null)
                Find.DesignatorManager.Deselect();
            foreach (uint button in buttons) XTestFakeButtonEvent(display, button, false, UIntPtr.Zero);
            foreach (uint key in keys) XTestFakeKeyEvent(display, key, false, UIntPtr.Zero);
            XFlush(display); buttons.Clear(); keys.Clear(); owner = null; until = 0;
        }
        void Update()
        {
            if (owner != null && (Time.realtimeSinceStartup >= until || !ReferenceEquals(game, Current.Game)
                || !ReferenceEquals(map, Find.CurrentMap))) Release();
            ReadChannel();
        }
        void OpenChannel()
        {
            if (channel != null) return;
            channelName = "/dev/shm/RimGovernorInput-" + Guid.NewGuid().ToString("N");
            channelFile = new FileStream(channelName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            channelFile.SetLength(4096);
            channelMap = MemoryMappedFile.CreateFromFile(channelFile, null, 4096, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
            channel = channelMap.CreateViewAccessor();
        }
        static string ReadText(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 300) throw new ArgumentException("Invalid input text length");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new ArgumentException("Truncated input text");
            return Encoding.UTF8.GetString(bytes);
        }
        void ReadChannel()
        {
            if (channel == null) return;
            int fd = channelFile.SafeFileHandle.DangerousGetHandle().ToInt32();
            if (flock(fd, 2 | 4) != 0) return;
            try
            {
                long nextCommand = channel.ReadInt64(0);
                if (nextCommand <= command) return;
                command = nextCommand;
                string error = "";
                try
                {
                    int length = channel.ReadInt32(32);
                    if (length < 32 || length > 2048) throw new ArgumentException("Invalid input message size");
                    var bytes = new byte[length]; channel.ReadArray(36, bytes, 0, length);
                    using (var reader = new BinaryReader(new MemoryStream(bytes)))
                    {
                        long frame = reader.ReadInt64(), order = reader.ReadInt64();
                        int x = reader.ReadInt32(), y = reader.ReadInt32(), button = reader.ReadInt32(), delta = reader.ReadInt32();
                        string action = ReadText(reader), requestedOwner = ReadText(reader), source = ReadText(reader), kind = ReadText(reader), key = ReadText(reader);
                        if (action != "event" && action != "renew" && action != "release") throw new ArgumentException("Mailbox cannot acquire ownership");
                        if (reader.BaseStream.Position != length) throw new ArgumentException("Unexpected input bytes");
                        Apply(action, requestedOwner, source, frame, order, kind, x, y, button, key, delta);
                    }
                }
                catch (Exception failure) { error = failure.Message; Release(); }
                byte[] message = Encoding.UTF8.GetBytes(error.Length > 300 ? error.Substring(0, 300) : error);
                channel.Write(16, error.Length == 0 ? 1 : -1);
                channel.Write(20, message.Length);
                channel.Write(24, buttons.Count); channel.Write(28, keys.Count);
                channel.WriteArray(2084, message, 0, message.Length);
                channel.Write(8, command);
            }
            finally { flock(fd, 8); }
        }
        void OnDestroy()
        {
            Release(); if (display != IntPtr.Zero) XCloseDisplay(display);
            channel?.Dispose(); channelMap?.Dispose(); channelFile?.Dispose();
            if (channelName != null) File.Delete(channelName);
            instance = null;
        }
    }
}
