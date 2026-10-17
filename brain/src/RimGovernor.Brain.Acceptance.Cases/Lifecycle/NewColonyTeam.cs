using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/new_colony_team</c> (Go <c>new_colony_team.go</c>): the built-in team-composition policy over three
/// seeds, and the first seed twice. Each start needs a fresh main menu, so each opens its own game (four launches).
/// </summary>
internal static class LifecycleNewColonyTeam
{
    private const int ColonistCount = 4;

    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/new_colony_team",
        Scope = "Team-composition policy (#2024): three seeds give four-colonist teams with no rejected trait, Construction and Hauling capability on everyone, "
            + "Plants/Cooking/Construction/Medicine/Mining coverage, two Shooting-passion and one Melee-passion colonist; the first seed twice gives the same team; "
            + "Permanent health rejection is covered by the native-team-policy probe.",
        Start = new OwnedStart(),
        Reason = "each start needs a fresh main menu process, so every seed opens and retires its own game",
        NoKeep = true,
        Budget = TimeSpan.FromMinutes(45),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static JsonObject TeamSpec(string seed)
    {
        var spec = NewColonyKit.Spec();
        spec["colonistCount"] = ColonistCount;
        spec["seed"] = seed;
        spec["saveName"] = seed;
        return spec;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        string[] seeds = ["team-accept-a", "team-accept-b", "team-accept-c", "team-accept-a"];
        var first = "";
        for (var i = 0; i < seeds.Length; i++)
        {
            var label = string.Create(CultureInfo.InvariantCulture, $"{seeds[i]}-{i}");
            var team = await LaunchAsync(s, label, seeds[i], cancellationToken).ConfigureAwait(false);
            try
            {
                CheckPolicy(team);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{label}: {e.Message}", e);
            }

            var digest = Digest(team);
            s.Report["team_" + label] = digest;
            if (i == 0)
            {
                first = digest;
            }
            else if (i == seeds.Length - 1 && digest != first)
            {
                throw new AcceptanceException($"seed {seeds[i]} twice gave different teams:\nfirst:  {first}\nsecond: {digest}");
            }
        }
    }

    internal sealed class TeamPawn
    {
        internal string Name { get; init; } = "";

        /// <summary>"Def:degree".</summary>
        internal List<string> Traits { get; } = [];

        internal Dictionary<string, int> Levels { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, string> Passions { get; } = new(StringComparer.Ordinal);

        /// <summary>Skills totally disabled.</summary>
        internal HashSet<string> Disabled { get; } = new(StringComparer.Ordinal);

        /// <summary>Incapable work types.</summary>
        internal HashSet<string> Incapable { get; } = new(StringComparer.Ordinal);
    }

    internal static string Digest(IReadOnlyList<TeamPawn> team)
    {
        var rows = new List<string>();
        foreach (var p in team)
        {
            var skills = p.Levels.Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={pair.Value}/{p.Passions.GetValueOrDefault(pair.Key, "")}")).Order(StringComparer.Ordinal);
            var traits = p.Traits.Order(StringComparer.Ordinal);
            rows.Add($"{p.Name}{{{string.Join(',', traits)}}}[{string.Join(',', skills)}]");
        }

        rows.Sort(StringComparer.Ordinal);
        return string.Join(';', rows);
    }

    private static async Task<IReadOnlyList<TeamPawn>> LaunchAsync(ISession s, string label, string seed, CancellationToken cancellationToken)
    {
        IReadOnlyList<TeamPawn> team = [];

        // The Go case opens the game without the discovery check; the empty tool list keeps that.
        await NewColonyKit.MenuAsync(
            s,
            label,
            [],
            async (h, row) =>
            {
                var started = Stopwatch.StartNew();
                _ = await NewColonyKit.RunAsync(h, $"team-{label}-{LifecycleCases.Stamp()}", TeamSpec(seed), label, cancellationToken).ConfigureAwait(false);
                row["finish_ms"] = started.ElapsedMilliseconds;
                team = await ReadAsync(h, label, ColonistCount, cancellationToken).ConfigureAwait(false);
                row["team"] = Digest(team);
                s.Report["team_run_" + label] = row;
            },
            cancellationToken).ConfigureAwait(false);
        return team;
    }

    /// <summary>Lists the live colony's colonists with biography.</summary>
    internal static async Task<IReadOnlyList<TeamPawn>> ReadAsync(Harness h, string label, int count, CancellationToken cancellationToken)
    {
        var identity = await Session.ReadIdentityAsync(h, label + "-identity", cancellationToken).ConfigureAwait(false);
        var team = new List<TeamPawn>();
        foreach (var raw in await NewColonyKit.ListColonistsAsync(h, label + "-pawns", new JsonObject { ["expectedIdentity"] = identity }, cancellationToken).ConfigureAwait(false))
        {
            var pawn = raw.AsMap();
            var bio = pawn?["biography"].AsMap();
            var p = new TeamPawn { Name = pawn?["pawn"].AsMap()?["label"].AsString() ?? "" };
            foreach (var skill in (bio?["skills"]).AsSlice())
            {
                var def = skill.Member("defName").AsString();
                p.Levels[def] = (int)Math.Max(skill.Member("level").AsNumber(), 0);
                var passion = skill.Member("passion").AsString();
                p.Passions[def] = passion.StartsWith("PASSION_", StringComparison.Ordinal) ? passion["PASSION_".Length..] : passion;
                if (skill.Member("disabled").AsBool() == true)
                {
                    _ = p.Disabled.Add(def);
                }
            }

            foreach (var trait in (bio?["traits"]).AsSlice())
            {
                p.Traits.Add(string.Create(CultureInfo.InvariantCulture, $"{trait.Member("defName").AsString()}:{(int)(trait.Member("degree") is null ? 0 : trait.Member("degree").AsNumber())}"));
            }

            foreach (var work in (bio?["incapableWorkTypes"]).AsSlice())
            {
                _ = p.Incapable.Add(work.AsString());
            }

            team.Add(p);
        }

        return team.Count == count ? team : throw new AcceptanceException($"{label}: pawn list has {team.Count} colonists, want {count}");
    }

    /// <summary>Asserts the observable parts of the policy on a read team.</summary>
    private static void CheckPolicy(IReadOnlyList<TeamPawn> team)
    {
        HashSet<string> rejected = ["Pyromaniac:0", "Nerves:-2", "Wimp:0", "Industriousness:-2", "DrugDesire:2"];
        var workTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Plants"] = "Growing",
            ["Cooking"] = "Cooking",
            ["Construction"] = "Construction",
            ["Medicine"] = "Doctor",
            ["Mining"] = "Mining",
        };
        var covered = new HashSet<string>(StringComparer.Ordinal);
        int shooters = 0, melee = 0;
        foreach (var p in team)
        {
            foreach (var trait in p.Traits.Where(rejected.Contains))
            {
                throw new AcceptanceException($"{p.Name} has the rejected trait {trait}");
            }

            if (p.Incapable.Contains("Construction") || p.Incapable.Contains("Hauling"))
            {
                throw new AcceptanceException($"{p.Name} is incapable of Construction or Hauling (incapable: [{string.Join(' ', p.Incapable)}])");
            }

            foreach (var (skill, work) in workTypes)
            {
                if (!p.Disabled.Contains(skill) && !p.Incapable.Contains(work))
                {
                    _ = covered.Add(skill);
                }
            }

            if (p.Passions.GetValueOrDefault("Shooting", "") is "MINOR" or "MAJOR")
            {
                shooters++;
            }

            if (p.Passions.GetValueOrDefault("Melee", "") is "MINOR" or "MAJOR")
            {
                melee++;
            }
        }

        foreach (var skill in workTypes.Keys.Where(skill => !covered.Contains(skill)))
        {
            throw new AcceptanceException($"no colonist is capable of {skill}");
        }

        if (shooters < 2 || melee < 1)
        {
            throw new AcceptanceException($"combat passions: {shooters} Shooting (want 2), {melee} Melee (want 1)");
        }
    }
}
