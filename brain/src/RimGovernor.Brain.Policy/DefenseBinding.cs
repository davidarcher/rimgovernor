using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Policy;

// Binding the chosen defense (task 7.4 of redefine-habitat-requirements, RG-HABITAT-009). The alternative the comparison
// (DefenseChoiceRules.Choose) picks over Retain enters the LayoutPlan as one planned site, a DefChoice node, on the shared
// ground ledger: a DefChoice reservation holds its footprint (the tiler and every planner keep off it; only a defense
// candidate may take it, DefenseGround), its pieces and firing cells are its TierFacts. From then on it is
// what Retain retains, so the next review keeps it while it is still the cheapest (ties go to Retain) and re-selects only
// when fresh facts make another alternative cheaper. Its work is a chain of dependent steps (HostChoice.Chain's shape):
// the pieces on free ground, then the removal of standing buildings on the cells it needs, held until its observed
// protection covers what the defense it replaces gave, then the pieces on the cleared cells, then the supporting
// service its protection names (turret power through the utility family, shells through military equipment, staffing).

/// <summary>The next step of a bound defense's chain, re-derived each review from fresh facts.</summary>
public abstract record DefenseStep
{
    private DefenseStep() { }

    /// <summary>Its unbuilt pieces whose cells hold no standing building.</summary>
    public sealed record Build(IReadOnlyList<DefenseStructure> Pieces) : DefenseStep;

    /// <summary>The standing buildings on the cells its remaining pieces need, admitted for removal.</summary>
    public sealed record Remove(IReadOnlyList<DefenseRemoval> Removals) : DefenseStep;

    /// <summary>Nothing it can order now: the removal hold or the supporting service its protection names.</summary>
    public sealed record Held(string Subject, string State) : DefenseStep;

    /// <summary>Every piece stands and its protection names no missing service.</summary>
    public sealed record Ready : DefenseStep;
}

/// <summary>A standing building a bound defense's piece replaces: its cell and its definition.</summary>
public sealed record DefenseRemoval(Cell Cell, string Definition);

public static class DefenseBindingRules
{
    /// <summary>The chosen defense's plan node.</summary>
    public static NodeKind NodeDefChoice => NodeKind.DefChoice;

    /// <summary>The tier name a bound choice's TierFacts carry.</summary>
    public static readonly DefenseTierName BoundTier = new("defense_choice");

    /// <summary>A bound node's id: this prefix and the chosen candidate's key.</summary>
    public const string NodePrefix = "defense/";

    /// <summary>The step's subjects: the removal hold, turret power (the utility family's), shells (military equipment's) and staffing.</summary>
    public const string RemovalHeld = "defense_removal_held", TurretPower = "turret_power", TurretShells = "turret_shells", Staffing = "defense_staffing";

    /// <summary>The bound choice's build Methods: this prefix, the candidate kind's wire name and the pieces' digest.</summary>
    public const string ChoicePrefix = "defense-choice-";

    /// <summary>The removal Methods of the standing buildings a bound choice's pieces replace.</summary>
    public const string RemovalPrefix = "defense-removal-";

    private const double Tolerance = 1e-9;

    /// <summary>Whether <paramref name="method"/> is a bound choice's work (its builds or its removals).</summary>
    public static bool ChainMethod(string method) =>
        method.StartsWith(ChoicePrefix, StringComparison.Ordinal) || method.StartsWith(RemovalPrefix, StringComparison.Ordinal);

    /// <summary>
    /// What a kept comparison read, so a change re-derives it before the re-verify window ends: the observed buildings (a
    /// piece lost or completed), the construction sites, the powered buildings (turret power), the raid tracks (lords,
    /// edges, ground, weapon range) and raid points, the able shooters and the bound node.
    /// </summary>
    public static string ReviewKey(ReviewFacts f, LayoutPlan plan, int shooters)
    {
        var text = new StringBuilder();
        foreach (var b in f.CurrentConstruction.Buildings)
        {
            text.Append(b.Id).Append(';');
        }
        text.Append('|').Append(f.CurrentConstruction.Sites.Count).Append('|');
        foreach (var b in f.EssentialInputs.Power.Buildings.Where(b => b.Powered))
        {
            text.Append(b.Id).Append(';');
        }
        text.Append('|');
        try
        {
            var t = f.Census.Threat.Observed;
            text.Append(t.RaidPoints.ToString("R", CultureInfo.InvariantCulture)).Append(':');
            foreach (var r in t.Raids)
            {
                text.Append(CultureInfo.InvariantCulture, $"{r.LordId}@{r.Spawn.X},{r.Spawn.Z}/{r.Ground}/{r.Range:R};");
            }
        }
        catch (RequiredFactException e)
        {
            text.Append("threat:").Append(e.Fact);
        }
        text.Append('|').Append(shooters).Append('|').Append(Bound(plan)?.Node ?? "");
        return text.ToString();
    }

    /// <summary>The plan's bound defense node; null when none is bound.</summary>
    public static PlannedSite? Bound(LayoutPlan plan) => plan.Sites.FirstOrDefault(s => s.Kind == NodeKind.DefChoice && s.Tier is not null);

    /// <summary>
    /// The plan with <paramref name="chosen"/> bound in place of any earlier choice: one DefChoice node holding its cells, with
    /// its pieces, its firing cells and the protection <paramref name="owed"/> (what of the defense it replaces stood and
    /// covered) its removals wait for.
    /// </summary>
    public static LayoutPlan Bind(LayoutPlan plan, DefenseCandidate chosen, double owed)
    {
        if (chosen.Kind == DefenseCandidateKind.Retain)
        {
            return plan;
        }
        var cells = chosen.Cells.Count > 0 ? chosen.Cells : [.. chosen.Structures.Select(s => s.Cell).Concat(chosen.Firing).Distinct()];
        var node = LayoutPerimeterSitesRules.NewSite(NodePrefix + chosen.Key, NodeKind.DefChoice, RegionRules.RegionOf(cells), Wire.Name(chosen.Kind)) with
        {
            Tier = new TierFacts
            {
                Tier = BoundTier,
                Places = [.. chosen.Structures.Select(s => new TierPlacement(s.Definition, s.Cell, Rotation.North, s.Stuff))],
                Reserved = [.. cells],
                Firing = [.. chosen.Firing.Select(f => new FiringPosition(f, f, f))],
                Owed = owed,
            },
        };
        var stripped = Unbound(plan);
        return stripped with
        {
            Sites = [.. stripped.Sites, node],
            Reservations = [.. stripped.Reservations, new LayoutReservation { Kind = ReservationKind.DefChoice, Area = node.Footprint }],
        };
    }

    /// <summary>The plan without its bound defense: no DefChoice node and no DefChoice reservation.</summary>
    private static LayoutPlan Unbound(LayoutPlan plan)
    {
        var stripped = plan.WithoutSites(NodeKind.DefChoice);
        return stripped with { Reservations = [.. stripped.Reservations.Where(r => r.Kind != ReservationKind.DefChoice)] };
    }

    /// <summary>The replanned plan <paramref name="next"/> with <paramref name="prev"/>'s bound defense (its node and the reservation holding its ground): a replan never unbinds or moves it.</summary>
    public static LayoutPlan WithBoundDefenseOf(this LayoutPlan next, LayoutPlan prev)
    {
        var stripped = Unbound(next);
        return stripped with
        {
            Sites = [.. stripped.Sites, .. prev.Sites.Where(s => s.Kind == NodeKind.DefChoice)],
            Reservations = [.. stripped.Reservations, .. prev.Reservations.Where(r => r.Kind == ReservationKind.DefChoice)],
        };
    }

    /// <summary>The bound node's pieces as structures (closing their cell when the definition blocks).</summary>
    public static IReadOnlyList<DefenseStructure> Structures(PlannedSite node, DefenseDefinitions defs, string conduit) =>
        [.. (node.Tier?.Places ?? []).Select(p => new DefenseStructure(p.Definition, p.Cell, p.Stuff, DefenseCandidateRules.Blocks(defs, p.Definition, conduit)))];

    /// <summary>The bound node's firing cells.</summary>
    public static IReadOnlyList<Cell> Firing(PlannedSite node) => [.. (node.Tier?.Firing ?? []).Select(f => f.Cell)];

    /// <summary>
    /// The next step of the chosen row's chain over the standing buildings (cell to definition): its pieces on free ground; else
    /// the removal of the standing buildings its remaining pieces need, unless <see cref="RemovalHold"/> holds it; else the
    /// supporting service its protection names; else ready.
    /// </summary>
    public static DefenseStep Next(DefenseChoiceRow chosen, IReadOnlyDictionary<Cell, string> standing, double owed)
    {
        var free = chosen.Unbuilt.Where(s => !standing.ContainsKey(s.Cell)).ToList();
        if (free.Count > 0)
        {
            return new DefenseStep.Build(free);
        }
        var blocked = chosen.Unbuilt.Where(s => standing.ContainsKey(s.Cell)).ToList();
        if (blocked.Count > 0)
        {
            return RemovalHold(chosen, owed) is { } hold
                ? new DefenseStep.Held(RemovalHeld, hold)
                : new DefenseStep.Remove([.. blocked.Select(s => new DefenseRemoval(s.Cell, standing[s.Cell])).Distinct()]);
        }
        foreach (var state in chosen.Alternative.Protection.States)
        {
            if (state.StartsWith("power:", StringComparison.Ordinal))
            {
                return new DefenseStep.Held(TurretPower, state);
            }
            if (state.StartsWith("shells:", StringComparison.Ordinal))
            {
                return new DefenseStep.Held(TurretShells, state);
            }
            if (state.StartsWith("staffing:", StringComparison.Ordinal))
            {
                return new DefenseStep.Held(Staffing, state);
            }
        }
        return new DefenseStep.Ready();
    }

    /// <summary>
    /// Why a standing building the chosen defense replaces stays (HostRemovalHold's shape): while any of its other pieces is
    /// unbuilt, while what of it stands is unprobed, or while that observed coverage is below <paramref name="owed"/>, the
    /// coverage the defense it replaces gave. Null once the removal may go.
    /// </summary>
    public static string? RemovalHold(DefenseChoiceRow chosen, double owed)
    {
        if (chosen.Alternative.Standing is not { } stood)
        {
            return RemovalHeld + ":replacement_unprobed";
        }
        if (stood.Covered is DefenseTerm.Unassessed u)
        {
            return RemovalHeld + ":" + u.Fact;
        }
        var covered = ((DefenseTerm.Assessed)stood.Covered).Value;
        return covered < owed - Tolerance
            ? string.Create(CultureInfo.InvariantCulture, $"{RemovalHeld}:replacement_deficit:{owed - covered:0.###}")
            : null;
    }

    /// <summary>
    /// What the replaced defense covered that the binding owes: the baseline's observed coverage (what of it stands; its whole
    /// coverage when it places nothing or what stands is unprobed). The comparison chooses over the baseline only once its
    /// coverage is assessed, so 0 is returned only for a baseline no alternative was chosen over.
    /// </summary>
    public static double Owed(DefenseChoiceRow baseline)
    {
        var a = baseline.Alternative;
        return a.Standing?.Covered is DefenseTerm.Assessed s ? s.Value
            : a.Protection.Covered is DefenseTerm.Assessed full ? full.Value
            : 0;
    }
}
