using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Describes one game launch. Env entries override the inherited environment.
/// </summary>
public sealed class GameLaunchSpec
{
    public string GameId { get; init; } = "";

    public string Executable { get; init; } = "";

    public string WorkingDir { get; init; } = "";

    public IReadOnlyList<string> Args { get; init; } = [];

    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string StateDir { get; init; } = "";
}

/// <summary>Refuses a launch while a live endpoint exists for the game; the caller decides between attaching and stopping.</summary>
public sealed class GameAlreadyRunningException : AcceptanceException
{
    public GameAlreadyRunningException(string gameId, int pid)
        : base(string.Create(CultureInfo.InvariantCulture, $"gamehost: game \"{gameId}\" already running (pid {pid})"))
    {
        GameId = gameId;
        Pid = pid;
    }

    public string GameId { get; }

    public int Pid { get; }
}

/// <summary>
/// A launched or attached game process. The game is spawned detached, so it outlives the
/// controller that started it, and the endpoint record lets a later controller find the same process.
/// </summary>
public sealed class GameProcess
{
    /// <summary>Caps how long <see cref="StopAsync(CancellationToken)"/> waits for the killed process to exit.</summary>
    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(30);

    private readonly string _stateDir;

    /// <summary>Completed once the launcher has reaped the child and written exit.json; null for an attached game.</summary>
    private readonly Task? _reaped;

    private GameProcess(string stateDir, GameHostRecord record, Task? reaped)
    {
        _stateDir = stateDir;
        Record = record;
        _reaped = reaped;
    }

    /// <summary>The recorded endpoint.</summary>
    public GameHostRecord Record { get; }

    public int Pid => Record.Pid;

    /// <summary>Counts launches of this game id under the state dir.</summary>
    public long Generation => Record.Generation;

    /// <summary>Whether the recorded process is still running with the recorded start time.</summary>
    public bool Alive => IsRunning(Record);

    /// <summary>Whether <paramref name="record"/>'s process is still running with the recorded start time.</summary>
    public static bool IsRunning(GameHostRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return TryProcessStartTime(record.Pid, out var start) && start == record.PidStartTime;
    }

    /// <summary>The record under <paramref name="stateDir"/> for <paramref name="gameId"/> as it stands, without removing a stale one; null when absent or unreadable.</summary>
    public static GameHostRecord? PeekRecord(string stateDir, string gameId)
    {
        try
        {
            var record = GameHostRecord.Parse(File.ReadAllText(EndpointPath(stateDir, gameId)));
            return record is not null && string.Equals(record.GameId, gameId, StringComparison.Ordinal) ? record : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The directory holding a game's records and log.</summary>
    public static string GameDir(string stateDir, string gameId) => Path.Combine(stateDir, gameId);

    /// <summary>
    /// Where launch captures the game process's stdout and stderr, the per-instance game log (the state dir is per
    /// root, so instances never share it).
    /// </summary>
    public static string LogPath(string stateDir, string gameId) => Path.Combine(GameDir(stateDir, gameId), "game.log");

    public static string EndpointPath(string stateDir, string gameId) => Path.Combine(GameDir(stateDir, gameId), "endpoint.json");

    private static string ExitPath(string stateDir, string gameId) => Path.Combine(GameDir(stateDir, gameId), "exit.json");

    /// <summary>
    /// Reattaches to the game recorded under <paramref name="stateDir"/>. Returns null, and removes the stale record,
    /// when no endpoint exists or its pid is gone or now names a different process.
    /// </summary>
    public static GameProcess? Attach(string stateDir, string gameId)
    {
        var path = EndpointPath(stateDir, gameId);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            TryDelete(path);
            return null;
        }

        GameHostRecord? record;
        try
        {
            record = GameHostRecord.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            TryDelete(path);
            return null;
        }

        var game = record is null ? null : new GameProcess(stateDir, record, null);
        if (game is null || !string.Equals(record!.GameId, gameId, StringComparison.Ordinal) || !game.Alive)
        {
            TryDelete(path);
            return null;
        }

        return game;
    }

    /// <summary>
    /// Starts the game detached (no console window, own process group, broken away from the caller's job so closing the
    /// controller never takes the game down) with stdout and stderr in <see cref="LogPath"/>, and records its endpoint.
    /// Throws <see cref="GameAlreadyRunningException"/> while a live endpoint exists for the game id.
    /// </summary>
    public static GameProcess StartDetached(GameLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.GameId.Length == 0 || spec.Executable.Length == 0 || spec.StateDir.Length == 0)
        {
            throw new ArgumentException("gamehost: Spec needs GameId, Executable and StateDir", nameof(spec));
        }

        var existing = Attach(spec.StateDir, spec.GameId);
        if (existing is not null)
        {
            throw new GameAlreadyRunningException(spec.GameId, existing.Pid);
        }

        var dir = GameDir(spec.StateDir, spec.GameId);
        Directory.CreateDirectory(dir);
        var generation = NextGeneration(dir);
        var env = ChildEnv(Environment.GetEnvironmentVariables(), spec.Env);
        var commandLine = CommandLine(spec.Executable, spec.Args);

        GameHostNative.ProcessInformation info;
        using (var log = new FileStream(LogPath(spec.StateDir, spec.GameId), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Inheritable))
        {
            info = CreateDetached(commandLine, env, spec.WorkingDir, log.SafeFileHandle.DangerousGetHandle());
        }

        _ = GameHostNative.CloseHandle(info.Thread);
        var pid = info.ProcessId;
        var processHandle = info.Process;

        // Reap in the background so a child that dies early is not left a zombie; the endpoint names the process by pid,
        // not by this handle. The exit code is recorded for LastExit: a player closing the game exits 0, a crash or a
        // kill does not.
        var reaped = Task.Factory.StartNew(
            () =>
            {
                _ = GameHostNative.WaitForSingleObject(processHandle, GameHostNative.Infinite);
                var code = GameHostNative.GetExitCodeProcess(processHandle, out var exit) != 0 ? unchecked((int)exit) : -1;
                _ = GameHostNative.CloseHandle(processHandle);
                try
                {
                    WriteFileAtomic(ExitPath(spec.StateDir, spec.GameId), LaunchJson.Exit(new GameExitFile { Generation = generation, Code = code }) + "\n");
                }
                catch (IOException)
                {
                    // The state dir went away under us; nothing records the exit.
                }
                catch (UnauthorizedAccessException)
                {
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        if (!TryProcessStartTime(pid, out var start))
        {
            KillTree(pid);
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"gamehost: read start time of pid {pid}"));
        }

        var record = new GameHostRecord(spec.GameId, pid, start, DateTimeOffset.UtcNow, generation);
        try
        {
            WriteFileAtomic(EndpointPath(spec.StateDir, spec.GameId), record.ToJson());
        }
        catch (IOException)
        {
            KillTree(pid);
            throw;
        }

        return new GameProcess(spec.StateDir, record, reaped);
    }

    /// <summary>
    /// Stops whatever game is recorded under <paramref name="stateDir"/> by killing its process tree and removing the
    /// record (there is no games_stop to ask
    /// for a graceful exit and no windowed-profile launch record to clean here). Nothing running is not an error; the
    /// result says whether a live process was stopped. Discarding the root's paused breakpoints
    /// belongs to the checkpoint code, not here.
    /// </summary>
    public static async Task<bool> StopAsync(string stateDir, string gameId, CancellationToken ct = default)
    {
        var game = Attach(stateDir, gameId);
        if (game is null)
        {
            return false;
        }

        await game.StopAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Kills the game's process tree, waits (bounded by <paramref name="ct"/> and 30 seconds) for it to exit and removes
    /// endpoint.json. Stopping a game that already exited only removes the file.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Alive)
        {
            if (!KillTree(Record.Pid) && Alive)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"gamehost: kill pid {Record.Pid} failed"));
            }

            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(StopBound);
            using var pace = Wait.Pace(TimeSpan.FromMilliseconds(50));
            try
            {
                while (Alive)
                {
                    await pace.NextAsync(bound.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"gamehost: pid {Record.Pid} did not exit"));
            }
        }

        if (_reaped is not null)
        {
            // The launcher's reaper writes exit.json after the process dies; wait for it so nothing writes under the
            // state dir after Stop.
            try
            {
                await _reaped.WaitAsync(StopBound, ct).ConfigureAwait(false);
            }
            catch (TimeoutException e)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"gamehost: pid {Record.Pid} not reaped"), e);
            }
        }

        RemoveEndpoint();
    }

    /// <summary>
    /// Reports the exit code of the most recent launch of <paramref name="gameId"/>; null while that launch still runs,
    /// and when a process that has since gone launched it (only the launcher can read a child's exit code).
    /// </summary>
    public static int? LastExit(string stateDir, string gameId)
    {
        try
        {
            var latest = long.Parse(File.ReadAllText(Path.Combine(GameDir(stateDir, gameId), "generation")).Trim(), CultureInfo.InvariantCulture);
            var rec = LaunchJson.ParseExit(File.ReadAllText(ExitPath(stateDir, gameId)));
            return rec is null || rec.Generation != latest ? null : rec.Code;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The creation FILETIME (100 ns since 1601) of a running pid; false for an exited or missing pid.</summary>
    public static bool TryProcessStartTime(int pid, out long startTime)
    {
        startTime = 0;
        var h = GameHostNative.OpenProcess(GameHostNative.ProcessQueryLimitedInformation, 0, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (GameHostNative.GetExitCodeProcess(h, out var code) == 0 || code != GameHostNative.StillActive)
            {
                return false;
            }

            if (GameHostNative.GetProcessTimes(h, out var creation, out _, out _, out _) == 0)
            {
                return false;
            }

            startTime = creation;
            return true;
        }
        finally
        {
            _ = GameHostNative.CloseHandle(h);
        }
    }

    /// <summary>Ends <paramref name="pid"/> and its descendants; false when neither taskkill nor TerminateProcess worked.</summary>
    public static bool KillTree(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("/T");
            psi.ArgumentList.Add("/F");
            psi.ArgumentList.Add("/PID");
            psi.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
            using var kill = Process.Start(psi);
            if (kill is not null)
            {
                kill.WaitForExit();
                if (kill.ExitCode == 0)
                {
                    return true;
                }
            }
        }
        catch (Win32Exception)
        {
            // taskkill is missing; fall through to TerminateProcess.
        }

        var h = GameHostNative.OpenProcess(GameHostNative.ProcessTerminate, 0, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return GameHostNative.TerminateProcess(h, 1) != 0;
        }
        finally
        {
            _ = GameHostNative.CloseHandle(h);
        }
    }

    /// <summary>
    /// Creates the process with the log as stdout and stderr, no console window, in its own process group and broken
    /// away from the caller's job. Breakaway is refused when the enclosing job forbids it; the start is then retried
    /// without it. The child inherits only the log handle (an explicit handle list): inheriting every inheritable
    /// handle would hand it the caller's redirected stdout/stderr pipes, and a caller waiting for those pipes to close
    /// would wait for the game to quit.
    /// </summary>
    private static unsafe GameHostNative.ProcessInformation CreateDetached(string commandLine, string environment, string workingDir, IntPtr log)
    {
        nuint size = 0;
        _ = GameHostNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = (IntPtr)NativeMemory.Alloc(size);
        var inherited = (IntPtr*)NativeMemory.Alloc((nuint)IntPtr.Size);
        var initialized = false;
        try
        {
            inherited[0] = log;
            if (!GameHostNative.InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw new AcceptanceException("gamehost: restrict inherited handles for " + commandLine, new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            initialized = true;
            if (!GameHostNative.UpdateProcThreadAttribute(attributes, 0, GameHostNative.ProcThreadAttributeHandleList, (IntPtr)inherited, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new AcceptanceException("gamehost: restrict inherited handles for " + commandLine, new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            var startup = new GameHostNative.StartupInfoEx
            {
                StartupInfo = new GameHostNative.StartupInfo
                {
                    Flags = GameHostNative.StartfUseStdHandles,
                    StdOutput = log,
                    StdError = log,
                },
                AttributeList = attributes,
            };
            startup.StartupInfo.Cb = Marshal.SizeOf<GameHostNative.StartupInfoEx>();
            var flags = GameHostNative.CreateNoWindow | GameHostNative.CreateNewProcessGroup | GameHostNative.CreateUnicodeEnvironment | GameHostNative.ExtendedStartupInfoPresent;
            var cwd = workingDir.Length == 0 ? null : workingDir;
            if (GameHostNative.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, 1, flags | GameHostNative.CreateBreakawayFromJob, environment, cwd, in startup, out var info) != 0)
            {
                return info;
            }

            if (GameHostNative.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, 1, flags, environment, cwd, in startup, out info) != 0)
            {
                return info;
            }

            throw new AcceptanceException("gamehost: start " + commandLine, new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally
        {
            if (initialized)
            {
                GameHostNative.DeleteProcThreadAttributeList(attributes);
            }

            NativeMemory.Free((void*)attributes);
            NativeMemory.Free(inherited);
        }
    }

    /// <summary>Bumps the per-game launch counter kept beside the endpoint (the endpoint itself is removed on stop, so it cannot carry the count).</summary>
    private static long NextGeneration(string dir)
    {
        var path = Path.Combine(dir, "generation");
        long n = 0;
        if (File.Exists(path))
        {
            _ = long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
        }

        n++;
        WriteFileAtomic(path, n.ToString(CultureInfo.InvariantCulture));
        return n;
    }

    /// <summary>
    /// The child's environment block: the parent, then overrides. Names compare
    /// case-insensitively (Windows). Returned as the UTF-16 block text (NUL-separated entries).
    /// </summary>
    private static string ChildEnv(System.Collections.IDictionary parent, IReadOnlyDictionary<string, string> overrides)
    {
        var vals = new SortedDictionary<string, (string Name, string Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry kv in parent)
        {
            var k = (string)kv.Key;
            if (k.Length == 0)
            {
                continue;
            }

            vals[k] = (k, (string?)kv.Value ?? "");
        }

        foreach (var (k, v) in overrides)
        {
            vals[k] = (k, v);
        }

        if (!vals.ContainsKey("SYSTEMROOT"))
        {
            vals["SYSTEMROOT"] = ("SystemRoot", @"C:\Windows");
        }

        var sb = new StringBuilder();
        foreach (var (name, value) in vals.Values)
        {
            _ = sb.Append(name).Append('=').Append(value).Append('\0');
        }

        // The marshaller adds one more terminator, completing the block's double NUL.
        return sb.Append('\0').ToString();
    }

    /// <summary>The command line for <paramref name="exe"/> and <paramref name="args"/> under the CommandLineToArgvW quoting rules.</summary>
    private static string CommandLine(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        Quote(sb, exe);
        foreach (var a in args)
        {
            _ = sb.Append(' ');
            Quote(sb, a);
        }

        return sb.ToString();
    }

    private static void Quote(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            _ = sb.Append(arg);
            return;
        }

        _ = sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                _ = sb.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                _ = sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        _ = sb.Append('\\', backslashes * 2).Append('"');
    }

    /// <summary>Deletes endpoint.json only while it still names this process, so a successor's record is never removed.</summary>
    private void RemoveEndpoint()
    {
        try
        {
            var cur = GameHostRecord.Parse(File.ReadAllText(EndpointPath(_stateDir, Record.GameId)));
            if (cur is null || cur.Pid != Record.Pid || cur.PidStartTime != Record.PidStartTime)
            {
                return;
            }

            File.Delete(EndpointPath(_stateDir, Record.GameId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // No readable record, nothing to remove.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteFileAtomic(string path, string text)
    {
        var tmp = path + ".tmp" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }
}
