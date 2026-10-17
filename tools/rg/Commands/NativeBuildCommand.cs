using NativeModBuild = RimGovernor.Brain.Acceptance.NativeModBuild;
using NativeModBuildOptions = RimGovernor.Brain.Acceptance.NativeModBuildOptions;
using NativeSourceInputs = RimGovernor.Brain.Acceptance.NativeSourceInputs;
using SetupDiscovery = RimGovernor.Brain.Acceptance.SetupDiscovery;
using SetupException = RimGovernor.Brain.Acceptance.SetupException;
using SetupOverrides = RimGovernor.Brain.Acceptance.SetupOverrides;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands;

/// <summary>
/// <c>RimGovernor.cmd native-build -out &lt;dir&gt; [-fixtures A,B] [-rimworld &lt;dir&gt;] [-harmony &lt;dll&gt;] [-if-current]</c>:
/// builds the native mod package (<see cref="NativeModBuild"/>) from this checkout into <c>&lt;dir&gt;/RimGovernor</c>.
/// The game and Harmony are found as acceptance setup finds them. With <c>-if-current</c> an existing build there of
/// these sources, game inputs and fixtures is kept and any other is replaced (the test gate's use).
/// </summary>
internal static class NativeBuildCommand
{
    private const string Usage = """
        usage: native-build -out <dir> [-fixtures A,B] [-rimworld <dir>] [-harmony <0Harmony.dll>] [-if-current]
          -out         a fresh directory; receives build/ (logs, the source copy) and the package RimGovernor/
          -fixtures    comma-separated fixture build flags (each a scripts/fixtures class); none is the production build
          -rimworld    the RimWorld install (default: RIMGOVERNOR_RIMWORLD_DIR, then Steam)
          -harmony     0Harmony.dll (default: RIMGOVERNOR_HARMONY_DLL, then the Steam workshop item)
          -if-current  keep -out when it holds a build of these sources, inputs and fixtures; else replace it
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string> { "if-current" }, new HashSet<string> { "out", "fixtures", "rimworld", "harmony" });
        if (flags.HelpRequested)
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        if (flags.String("out").Length == 0 || flags.Positional.Count > 0)
        {
            throw new UsageException(Usage);
        }

        var repo = NativeSourceInputs.FindRepo(Environment.CurrentDirectory) ?? throw new ToolException("not inside a checkout");
        var output = Path.GetFullPath(flags.String("out"));
        try
        {
            var inputs = SetupDiscovery.Discover(new SetupOverrides { Repo = repo, RimWorldDir = flags.String("rimworld"), Harmony = flags.String("harmony") });
            var options = new NativeModBuildOptions
            {
                Repo = repo,
                RimWorldDir = inputs.RimWorldDir,
                Harmony = inputs.Harmony,
                Fixtures = flags.String("fixtures").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                OutputRoot = output,
                Log = Console.Out,
            };
            if (flags.Bool("if-current"))
            {
                if (NativeModBuild.IsCurrent(Path.Combine(output, "RimGovernor"), options))
                {
                    Console.Out.WriteLine("native build: current " + Path.Combine(output, "RimGovernor"));
                    return 0;
                }

                if (Directory.Exists(output))
                {
                    Directory.Delete(output, recursive: true);
                }
            }

            Console.Out.WriteLine(await NativeModBuild.BuildAsync(options, ct).ConfigureAwait(false));
            return 0;
        }
        catch (SetupException e)
        {
            throw new ToolException(e.Message);
        }
    }
}
