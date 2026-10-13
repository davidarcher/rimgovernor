using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>acceptance warm</c> (Go <c>cmd/acceptance/warm.go</c>): prepares the root's profile and boots its game to the main
/// menu so the next run attaches instead of launching. <c>-background</c> hands the work to a detached copy of this
/// command, logging to <c>&lt;root&gt;/acceptance/warm/warm.log</c>, and returns at once with its pid; a post-build hook
/// uses it after the native mod is installed.
/// </summary>
public static class WarmCommand
{
    /// <summary>The usage line for the command's help text.</summary>
    public const string Usage = "  acceptance warm -root <dir> [-game <id> -headless=false -background]";

    private static readonly TimeSpan ForegroundTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Runs the subcommand; returns the process exit code (2 usage, 1 a failed warm).</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var fs = new FlagSet("warm");
        var root = fs.String("root", "");
        var game = fs.String("game", "rimgovernor-trial");
        var headless = fs.Bool("headless", true);
        var background = fs.Bool("background", false);
        try
        {
            fs.Parse(args);
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        if (root.Value.Length == 0)
        {
            await stderr.WriteLineAsync("-root is required").ConfigureAwait(false);
            return 2;
        }

        if (!Path.IsPathFullyQualified(root.Value))
        {
            await stderr.WriteLineAsync($"-root must be absolute: {root.Value}").ConfigureAwait(false);
            return 2;
        }

        var output = Path.Combine(root.Value, "acceptance", "warm");
        if (background.Value)
        {
            int pid;
            try
            {
                pid = StartDetached(output, root.Value, game.Value, headless.Value);
            }
            catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
            {
                await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
                return 1;
            }

            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"warming {root.Value} in pid {pid} (log: {Path.Combine(output, "warm.log")})")).ConfigureAwait(false);
            return 0;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ForegroundTimeout);
        Warmed warmed;
        try
        {
            warmed = await Game.WarmGameAsync(
                new Config { Root = root.Value, Output = output, GameId = game.Value, Headless = headless.Value },
                timeout.Token).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }

        var open = GoDuration.Format(TimeSpan.FromMilliseconds(Math.Round(warmed.Open.TotalMilliseconds)));
        if (warmed.Reused)
        {
            await stdout.WriteLineAsync($"warm\t{root.Value}\treused a running process at the menu\t{open}").ConfigureAwait(false);
        }
        else if (warmed.Relaunched.Length > 0)
        {
            await stdout.WriteLineAsync($"warm\t{root.Value}\trelaunched ({warmed.Relaunched}) to the menu\t{open}").ConfigureAwait(false);
        }
        else
        {
            await stdout.WriteLineAsync($"warm\t{root.Value}\tlaunched to the menu\t{open}").ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Starts this executable's foreground warm as a process that outlives the caller's console, its output appended to
    /// <c>output/warm.log</c>. The shell owns the redirection, so the child keeps logging after this process exits.
    /// </summary>
    internal static int StartDetached(string output, string root, string game, bool headless)
    {
        var logPath = Path.Combine(output, "warm.log");
        Directory.CreateDirectory(output);
        var (self, leading) = SelfInvocation.Resolve();
        var childArgs = new List<string>(leading) { "acceptance", "warm", "-root", root, "-game", game, $"-headless={(headless ? "true" : "false")}" };
        File.AppendAllText(
            logPath,
            string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O} warm -root {root} -game {game} -headless={(headless ? "true" : "false")}\n"));
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var inner = string.Join(' ', new[] { self }.Concat(childArgs).Select(Quote));
        if (OperatingSystem.IsWindows())
        {
            info.FileName = "cmd.exe";
            info.Arguments = $"/d /s /c \"{inner} >> {Quote(logPath)} 2>&1\"";
        }
        else
        {
            info.FileName = "/bin/sh";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"{inner} >> {Quote(logPath)} 2>&1");
        }

        try
        {
            using var process = Process.Start(info)
                ?? throw new AcceptanceException("start detached warm: the process did not start");
            return process.Id;
        }
        catch (Win32Exception e)
        {
            throw new AcceptanceException($"start detached warm: {e.Message}", e);
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
