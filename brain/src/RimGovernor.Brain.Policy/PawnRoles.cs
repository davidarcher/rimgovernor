using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// Situational roles: pure answers to the questions other goals ask of the roster (who operates, who wardens,
// who trades, who tames, who holds the line), all from the same PawnProfile the planner scores. Each returns
// the chosen pawn in a deterministic order (fitness, then ID) and null when no pawn qualifies.

public static partial class PolicyConstants
{
    /// <summary>The prey one hunter keeps designated at once.</summary>
    public const int HuntsPerHunter = 3;

    /// <summary>Bounds the hunt rows of one acquisition method, whatever the roster.</summary>
    public const int MaxHuntRows = 48;
}

public static partial class PolicyFunctions
{
    private readonly record struct RoleCandidate(PawnId Id, double Score);

    private static PawnId? BestRole(List<RoleCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }
        return candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Id).First().Id;
    }

    /// <summary>
    /// Restricts profiles to the pawns native already found eligible for a role (a trade census's
    /// negotiators, a squad's defenders), so a role picks only among them. An empty id list admits nobody.
    /// </summary>
    public static IReadOnlyList<PawnProfile> Among(IReadOnlyList<PawnProfile> profiles, IReadOnlyList<PawnId> ids)
    {
        var allowed = new HashSet<PawnId>(ids);
        return [.. profiles.Where(p => allowed.Contains(p.Id))];
    }

    /// <summary>
    /// Picks the warden: the highest Social, Kind preferred, Abrasive never; for an execution a Psychopath or
    /// Bloodlust pawn takes no mood loss and is preferred.
    /// </summary>
    public static PawnId? WardenFor(IReadOnlyList<PawnProfile> profiles, bool execution)
    {
        var candidates = new List<RoleCandidate>();
        foreach (var p in profiles)
        {
            if (!p.Capable(WorkType.Warden, 0))
            {
                continue;
            }
            var score = p.SkillFor(WorkType.Warden).Level + (5 * (double)p.Effects.Sociable);
            if (execution && p.Effects.Execution)
            {
                score += 10;
            }
            candidates.Add(new RoleCandidate(p.Id, score));
        }
        return BestRole(candidates);
    }

    /// <summary>Picks the negotiator: the highest Social, never Abrasive while anyone else can talk.</summary>
    public static PawnId? TraderFor(IReadOnlyList<PawnProfile> profiles)
    {
        var candidates = new List<RoleCandidate>();
        var abrasive = new List<RoleCandidate>();
        foreach (var p in profiles)
        {
            var s = p.Skill("Social");
            if (s.Disabled || p.Incapable.GetValueOrDefault(WorkType.Warden))
            {
                continue;
            }
            var c = new RoleCandidate(p.Id, s.Level + (5 * (double)p.Effects.Sociable));
            if (p.Effects.Sociable < 0)
            {
                abrasive.Add(c);
                continue;
            }
            candidates.Add(c);
        }
        if (candidates.Count == 0)
        {
            candidates = abrasive;
        }
        return BestRole(candidates);
    }

    /// <summary>Picks the handler for an animal needing Animals at least minimum (the animal read's minimum_handling_skill).</summary>
    public static PawnId? TamerFor(IReadOnlyList<PawnProfile> profiles, int minimum)
    {
        var candidates = new List<RoleCandidate>();
        foreach (var p in profiles)
        {
            var s = p.SkillFor(WorkType.Handling);
            if (!p.Capable(WorkType.Handling, minimum))
            {
                continue;
            }
            candidates.Add(new RoleCandidate(p.Id, s.Level));
        }
        return BestRole(candidates);
    }

    /// <summary>Picks the hunter: Shooting with a ranged weapon, never a Brawler, a fast walker preferred for the walk to the carcass.</summary>
    public static PawnId? HunterFor(IReadOnlyList<PawnProfile> profiles)
    {
        var candidates = new List<RoleCandidate>();
        foreach (var p in profiles)
        {
            if (!p.Ranged || !p.Capable(WorkType.Hunting, 0))
            {
                continue;
            }
            candidates.Add(new RoleCandidate(p.Id, p.SkillFor(WorkType.Hunting).Level + (5 * p.Effects.MoveSpeed)));
        }
        return BestRole(candidates);
    }

    /// <summary>
    /// The hunts a goal may still designate: HuntsPerHunter for each ranged Hunting-capable colonist (the
    /// HunterFor profiles), less the hunts already designated, at most MaxHuntRows.
    /// </summary>
    public static int HuntBudget(IReadOnlyList<PawnProfile> profiles, int pendingHunts) =>
        Math.Max(0, Math.Min(PolicyConstants.MaxHuntRows, Hunters(profiles) * PolicyConstants.HuntsPerHunter) - pendingHunts);

    /// <summary>Counts the ranged Hunting-capable colonists (HunterFor's profiles).</summary>
    public static int Hunters(IReadOnlyList<PawnProfile> profiles) =>
        profiles.Count(p => p.Ranged && p.Capable(WorkType.Hunting, 0));

    /// <summary>
    /// Scales a body part's surgery value by the pawn's role: shooters' eyes by Shooting, workers' arms and
    /// hands by their best manual skill (Construction, Mining, Crafting), haulers' legs and feet by half
    /// again. Any other part, or a pawn outside the role, is 1.
    /// </summary>
    public static double UpgradeRoleWeight(PawnProfile p, string part)
    {
        if (part.Contains("Eye", StringComparison.Ordinal))
        {
            var s = p.Skill("Shooting");
            if (p.Ranged && !s.Disabled)
            {
                return 1 + ((double)s.Level / 10);
            }
        }
        else if (part.Contains("Arm", StringComparison.Ordinal) || part.Contains("Hand", StringComparison.Ordinal)
            || part.Contains("Shoulder", StringComparison.Ordinal))
        {
            var best = -1;
            foreach (var name in new[] { "Construction", "Mining", "Crafting" })
            {
                var s = p.Skill(name);
                if (!s.Disabled && s.Level > best)
                {
                    best = s.Level;
                }
            }
            if (best >= 0)
            {
                return 1 + ((double)best / 10);
            }
        }
        else if (part.Contains("Leg", StringComparison.Ordinal) || part.Contains("Foot", StringComparison.Ordinal))
        {
            if (p.Capable(WorkType.Hauling, 0))
            {
                return 1.5;
            }
        }
        return 1;
    }

    /// <summary>
    /// Splits a defending roster: melee pawns (Tough, Nimble, Brawler, or Melee over Shooting) hold the line
    /// (front) and the rest shoot from behind it (rear). Every non-violent-incapable pawn lands in exactly
    /// one.
    /// </summary>
    public static (IReadOnlyList<PawnId> Front, IReadOnlyList<PawnId> Rear) FrontLine(IReadOnlyList<PawnProfile> profiles)
    {
        var front = new List<PawnId>();
        var rear = new List<PawnId>();
        foreach (var p in profiles)
        {
            var melee = p.Skill("Melee");
            var shooting = p.Skill("Shooting");
            if (melee.Disabled && shooting.Disabled)
            {
                continue;
            }
            if (p.Effects.FrontLine || (!p.Effects.RearRanged && !p.Ranged && melee.Level >= shooting.Level))
            {
                front.Add(p.Id);
                continue;
            }
            rear.Add(p.Id);
        }
        return (front, rear);
    }
}
