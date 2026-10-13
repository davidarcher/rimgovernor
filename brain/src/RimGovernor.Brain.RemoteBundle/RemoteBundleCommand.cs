using System.Globalization;
using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Brain.RemoteBundle;

/// <summary>
/// The <c>remotebundle</c> command (Go <c>cmd/remotebundle</c>): <c>RimGovernor.cmd remotebundle
/// pack|publish|verify|extract|bootstrap [flags]</c>. It packages, verifies and bootstraps the encrypted acceptance
/// dependencies.
/// </summary>
public static class RemoteBundleCommand
{
    private const string Usage = """
        usage: RimGovernor.cmd remotebundle pack|publish|verify|extract|bootstrap [flags]

          pack      -repo <checkout> -game <dir> -harmony <0Harmony.dll> -harmony-mod <dir> -out <new dir>
                    -game-version <v> -harmony-version <v> -origin <owner/repo> -release-id <id>
                    -recipient <age key> -key-id <label> [-starts <dir>] [-part-bytes <n>] [-7z <exe>] [-age <exe>]
          publish   -manifest <bundle.draft.json>
          verify    -manifest <file> -sha256 <digest> -parts <dir> [-local-draft]
          extract   -manifest <file> -sha256 <digest> -parts <dir> -out <new dir> -identity <file> [-local-draft] [-7z <exe>] [-age <exe>]
          bootstrap -repo <checkout> -out <new job dir> -cache <dir> -manifest <file> -sha256 <digest>
                    -trust <authorization.json> -identity <file> [-role fixture] [-7z <exe>] [-age <exe>]
        """;

    private static readonly HashSet<string> BoolFlags = ["local-draft"];

    private static readonly HashSet<string> ValueFlags =
    [
        "7z", "age", "manifest", "sha256", "repo", "starts", "game", "harmony", "harmony-mod", "out", "game-version",
        "harmony-version", "origin", "release-id", "recipient", "key-id", "part-bytes", "parts", "identity", "cache",
        "trust", "role",
    ];

    /// <summary>Runs one subcommand and returns the process exit code (2 usage, 1 failure).</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Count == 0 || args[0] is "-h" or "-help" or "--help" or "help")
        {
            await (args.Count == 0 ? stderr : stdout).WriteLineAsync(Usage).ConfigureAwait(false);
            return args.Count == 0 ? 2 : 0;
        }

        try
        {
            return await RunCommandAsync(args[0], args.Skip(1).ToList(), stdout, cancellationToken).ConfigureAwait(false);
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync($"remotebundle: {e.Message}\n\n{Usage}").ConfigureAwait(false);
            return 2;
        }
        catch (Exception e) when (e is BundleException or AcceptanceException or IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync($"remotebundle: {e.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> RunCommandAsync(string command, List<string> rest, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (command is not ("pack" or "publish" or "verify" or "extract" or "bootstrap"))
        {
            throw new UsageException($"unknown command \"{command}\"");
        }

        var flags = ParseFlags(rest);
        var tools = new Tools(flags.String("7z", "7z"), flags.String("age", "age"));
        var manifestPath = command == "pack" ? "" : flags.Path("manifest");
        var digest = command is "verify" or "extract" or "bootstrap" ? flags.Path("sha256") : "";
        switch (command)
        {
            case "pack":
                return await PackAsync(flags, tools, stdout, cancellationToken).ConfigureAwait(false);
            case "publish":
                var draft = ManifestRules.ReadDraft(manifestPath);
                var name = await BundleOrigin.PublishAsync(Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".", draft, cancellationToken).ConfigureAwait(false);
                await stdout.WriteLineAsync(name).ConfigureAwait(false);
                return 0;
            case "bootstrap":
                return await BootstrapAsync(flags, tools, manifestPath, digest, stdout, cancellationToken).ConfigureAwait(false);
            default:
                break;
        }

        var localDraft = flags.Bool("local-draft");
        if (localDraft && command is not ("verify" or "extract"))
        {
            throw new UsageException("-local-draft is only valid for offline verify/extract");
        }

        var manifest = localDraft ? ManifestRules.ReadLocalDraft(manifestPath, digest) : ManifestRules.ReadManifest(manifestPath, digest);
        var inventory = ManifestRules.ReadInventory(Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".", manifest);
        if (command == "verify")
        {
            BundleArchive.VerifyParts(flags.Path("parts"), manifest);
            return 0;
        }

        await BundleArchive.ExtractAsync(tools, flags.Path("parts"), flags.Path("out"), flags.Path("identity"), manifest, inventory, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> PackAsync(Flags flags, Tools tools, TextWriter stdout, CancellationToken cancellationToken)
    {
        var options = new PackOptions
        {
            Repo = flags.Path("repo"),
            StartsDir = flags.String("starts", ""),
            GameDir = flags.Path("game"),
            HarmonyDll = flags.Path("harmony"),
            HarmonyMod = flags.String("harmony-mod", ""),
            Output = Path.GetFullPath(flags.Path("out")),
            GameVersion = flags.String("game-version", ""),
            HarmonyVersion = flags.String("harmony-version", ""),
            Origin = new Origin(flags.String("origin", ""), flags.Long("release-id", 0)),
            Recipient = flags.String("recipient", ""),
            KeyId = flags.String("key-id", ""),
            PartBytes = flags.Long("part-bytes", 1_000_000_000),
            Tools = tools,
        };
        var manifest = await BundlePack.PackAsync(options, cancellationToken).ConfigureAwait(false);
        var compressed = manifest.Parts.Sum(p => p.Bytes);
        await stdout.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"draft: {Path.Combine(options.Output, "bundle.draft.json")}\nunpacked: {manifest.UnpackedBytes} bytes; encrypted compressed: {compressed} bytes; parts: {manifest.Parts.Count}")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> BootstrapAsync(Flags flags, Tools tools, string manifestPath, string digest, TextWriter stdout, CancellationToken cancellationToken)
    {
        var repo = flags.Path("repo");
        var trustPath = flags.Path("trust");
        Bootstrapper.CheckAuthorizationPath(repo, trustPath);
        var trust = BundleJson.DecodeTrust(await File.ReadAllBytesAsync(trustPath, cancellationToken).ConfigureAwait(false));
        var report = await Bootstrapper.RunAsync(
            new GitHubOriginClient(),
            new BootstrapOptions
            {
                Repo = repo,
                Work = flags.Path("out"),
                Cache = flags.Path("cache"),
                Manifest = manifestPath,
                ManifestSha256 = digest,
                Identity = flags.Path("identity"),
                Trust = trust,
                Tools = tools,
                Role = flags.String("role", "fixture"),
                Log = stdout,
            },
            cancellationToken).ConfigureAwait(false);
        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"ready: {report.Root}\ncache hit: {report.CacheHit}; bootstrap: {report.TotalMs} ms")).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Go-flag-style parsing: <c>-name value</c> or <c>-name=value</c> (also <c>--name</c>); no positional arguments.</summary>
    private static Flags ParseFlags(List<string> args)
    {
        var flags = new Flags();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Length < 2 || arg[0] != '-')
            {
                throw new UsageException("unexpected positional arguments");
            }

            var name = arg.TrimStart('-');
            string? inline = null;
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                inline = name[(eq + 1)..];
                name = name[..eq];
            }

            if (BoolFlags.Contains(name))
            {
                flags.Set(name, inline ?? "true");
            }
            else if (ValueFlags.Contains(name))
            {
                if (inline is null)
                {
                    i++;
                    if (i >= args.Count)
                    {
                        throw new UsageException($"flag needs an argument: -{name}");
                    }

                    inline = args[i];
                }

                flags.Set(name, inline);
            }
            else
            {
                throw new UsageException($"flag provided but not defined: -{name}");
            }
        }

        return flags;
    }

    private sealed class Flags
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public void Set(string name, string value) => _values[name] = value;

        public string String(string name, string fallback) => _values.GetValueOrDefault(name, fallback);

        public string Path(string name) =>
            _values.TryGetValue(name, out var v) && v.Length > 0 ? v : throw new UsageException($"flag is required: -{name}");

        public bool Bool(string name) => _values.TryGetValue(name, out var v) && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

        public long Long(string name, long fallback) =>
            !_values.TryGetValue(name, out var v) ? fallback
            : long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : throw new UsageException($"invalid value \"{v}\" for flag -{name}");
    }
}
