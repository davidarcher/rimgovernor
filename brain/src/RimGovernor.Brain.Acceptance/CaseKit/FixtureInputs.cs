using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The test fixture sources (<c>scripts/fixtures/&lt;Name&gt;Fixture.cs</c>; no saves are committed, the harness
/// generates them) and which of them a case depends on (Go <c>inputs/fixture_inputs.go</c>). Which fixture source a
/// case depends on follows from the case's sources: it feeds a case when the case names one of its
/// <c>[Tool("test/...")]</c> ops, while any other file directly under the root feeds every case.
/// </summary>
public static partial class FixtureInputs
{
    /// <summary>The fixture sources, repo-relative.</summary>
    public const string FixtureRoot = "scripts/fixtures";

    /// <summary>
    /// The native mod's project, repo-relative: the fixture sources it compiles are each conditioned on a build flag,
    /// and several sources share one flag (SleepingFixture.cs under UpkeepFixture).
    /// </summary>
    public const string NativeProject = "integrations/rimgovernor-native/src/Bridge/RimGovernor.Bridge.csproj";

    /// <summary>A fixture op's registration in C#.</summary>
    [GeneratedRegex(@"\[Tool\(\s*""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex FixtureToolAttribute();

    /// <summary>A fixture source's Compile item in the native project, conditioned on the build flag that admits it.</summary>
    [GeneratedRegex(@"scripts/fixtures/([A-Za-z0-9_]+)\.cs""\s+Condition=""'\$\(([A-Za-z0-9_]+)\)'\s*==\s*'true'""", RegexOptions.CultureInvariant)]
    private static partial Regex FixtureCompileItem();

    /// <summary>
    /// Maps each fixture source (file base) <see cref="NativeProject"/> compiles to the build flag admitting it; a
    /// missing project maps nothing, and a source the project does not list is taken as its own flag.
    /// </summary>
    private static Dictionary<string, string> FixtureBuildFlags(string repo)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        string source;
        try
        {
            source = File.ReadAllText(Path.Combine(repo, NativeProject.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (IOException)
        {
            return flags;
        }
        catch (UnauthorizedAccessException)
        {
            return flags;
        }

        foreach (Match m in FixtureCompileItem().Matches(source))
        {
            flags[m.Groups[1].Value] = m.Groups[2].Value;
        }

        return flags;
    }

    private static string[] FixtureFiles(string repo)
    {
        try
        {
            return Directory.GetFiles(Path.Combine(repo, FixtureRoot.Replace('/', Path.DirectorySeparatorChar)), "*.cs", SearchOption.TopDirectoryOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"fixture sources {FixtureRoot}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Every -Fixture name the checkout still builds: each class under <see cref="FixtureRoot"/> plus the build flag
    /// the native project maps it to. An unreadable fixture root is an error.
    /// </summary>
    public static ISet<string> FixtureNames(string repo)
    {
        var files = FixtureFiles(repo);
        var flags = FixtureBuildFlags(repo);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var cls = Path.GetFileNameWithoutExtension(file);
            names.Add(cls);
            if (flags.TryGetValue(cls, out var flag))
            {
                names.Add(flag);
            }
        }

        return names;
    }

    /// <summary>
    /// Lists, sorted, the fixture build flags (the names build_native_mod.ps1 -Fixture takes and a package manifest
    /// records) admitting the sources under <see cref="FixtureRoot"/> that register any of <paramref name="ops"/> as a
    /// <c>[Tool("test/...")]</c>: what a build must include for a case whose Start calls those ops. A source is its
    /// own flag unless <see cref="NativeProject"/> conditions it on another one. An op no fixture registers is left
    /// out; a missing <see cref="FixtureRoot"/> is an error.
    /// </summary>
    public static IReadOnlyList<string> FixtureClasses(string repo, IEnumerable<string> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        var wanted = new HashSet<string>(ops, StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return [];
        }

        var files = FixtureFiles(repo);
        var flags = FixtureBuildFlags(repo);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (Match m in FixtureToolAttribute().Matches(source))
            {
                if (!wanted.Contains(m.Groups[1].Value))
                {
                    continue;
                }

                var cls = Path.GetFileNameWithoutExtension(file);
                seen.Add(flags.TryGetValue(cls, out var flag) ? flag : cls);
                break;
            }
        }

        return [.. seen.Order(StringComparer.Ordinal)];
    }
}
