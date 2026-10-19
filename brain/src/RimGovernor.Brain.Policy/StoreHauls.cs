using RimGovernor.Brain.Policy.Methods.Binding;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One standby colonist's store haul (#3624): the stack, the row it is carried by and what it is worth.</summary>
public sealed record StoreHaulProposal(StoreHaul Haul, ConstructionActivityOption Row, StoreHaulValue Value);

/// <summary>
/// Store hauls as standby work (#3624): a colonist the review gave nothing more valuable to do carries one loose stack to
/// the storage native chose for it, when the haul's value (<see cref="ForecastValue.StoreHaulValueOf"/>) is positive.
/// </summary>
public static class StoreHaulRules
{
    /// <summary>
    /// The planned-site uses the store read is asked about: each (room, op) of <paramref name="sites"/> once, in the stuff
    /// <paramref name="selection"/> chose for it, or else its first alternative (the cheapest stocked ring stuff), each
    /// material at the site's first position. Sites native cannot place are left out.
    /// </summary>
    public static EquatableArray<StoreHaulUse> PlannedUses(PlannedSiteFacts sites, PlannedSelection selection)
    {
        var chosen = selection.Sites.Select(s => (s.Room, s.Op, s.Definition, s.Stuff)).ToHashSet();
        var uses = new List<StoreHaulUse>();
        foreach (var op in sites.Sites.Where(s => s.Positions.Count > 0 && s.CanPlace != Known(false)).GroupBy(s => (s.Room, s.Op, s.Zone)))
        {
            var site = op.FirstOrDefault(s => chosen.Contains((s.Room, s.Op, s.Definition, s.Stuff))) ?? op.First();
            foreach (var material in site.Material.Where(m => m.Count > 0))
                uses.Add(new(material.Resource.Value, site.Positions[0], (int)Math.Min(int.MaxValue, material.Count)));
        }
        return EquatableArray.From(uses);
    }

    /// <summary>
    /// The stacks a standby haul must leave alone (#3624): every target a committed Method claims, and every stack a pending
    /// service can use: a need task's target (the food an eater takes), and a cook's, a supply's or a care treatment's
    /// target, inputs, medicine and source cell. Kept off them, a haul never starves a meal or a cook of its stack, whatever
    /// the forecast did not see of the haul's claim. Construction materials are not services: a planned site's stock may be hauled.
    /// </summary>
    public static (IReadOnlySet<string> Things, IReadOnlySet<Cell> Cells) Reserved(RoundsFacts facts)
    {
        var things = new HashSet<string>(StringComparer.Ordinal);
        var cells = new HashSet<Cell>();
        if (facts.WorkView is { } view) things.UnionWith(view.Claims.Targets);
        if (facts.CoupledAlternatives is not { } coupled) return (things, cells);
        things.UnionWith(coupled.Context.NeedTasks.Select(t => t.Target));
        foreach (var binding in coupled.Context.Bindings.Values.Where(b => b.Kind is PawnActivityKind.Eat or PawnActivityKind.Recipe or PawnActivityKind.Supply
            || b.Cooking is not null || b.Recipe is not null || b.Medicine.Length != 0 || b.SupportDemand.Length != 0))
        {
            things.Add(binding.Target);
            things.UnionWith(binding.Targets);
            things.Add(binding.Medicine);
            things.UnionWith(binding.SelectedInputs.Select(i => i.Lot));
            things.UnionWith(binding.Recipe?.Ingredients.Select(i => i.Thing) ?? []);
            cells.UnionWith(binding.SelectedInputs.Select(i => i.Location));
            if (binding.Source is { } source) cells.Add(source);
        }
        things.Remove("");
        return (things, cells);
    }

    /// <summary>
    /// The best positive haul for each of <paramref name="pawns"/>, best first, each stack once, among the family's offers
    /// (<see cref="StoreHaulCandidates.Offers"/>): never a <paramref name="reserved"/> stack or one on a
    /// <paramref name="reservedCells"/> cell, so a standby haul never takes a meal's or a cook's stack.
    /// </summary>
    public static IReadOnlyList<StoreHaulProposal> Propose(StoreHaulRead read, IReadOnlyCollection<string> pawns,
        IReadOnlySet<string> reserved, IReadOnlySet<Cell> reservedCells, double horizonDays)
    {
        var offers = StoreHaulCandidates.Offers(read, pawns, reserved, reservedCells)
            .Select(o => new StoreHaulProposal(o.Haul, o.Row, ForecastValue.StoreHaulValueOf(o.Haul, o.Row, read.ValuePerWork, horizonDays)))
            .Where(p => p.Value.Total > 0)
            .OrderByDescending(p => p.Value.Total).ThenBy(p => p.Row.Pawn, StringComparer.Ordinal).ThenBy(p => p.Haul.Thing, StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var stacks = new HashSet<string>(StringComparer.Ordinal);
        var chosen = new List<StoreHaulProposal>();
        foreach (var offer in offers)
            if (!taken.Contains(offer.Row.Pawn) && !stacks.Contains(offer.Haul.Thing) && taken.Add(offer.Row.Pawn) && stacks.Add(offer.Haul.Thing))
                chosen.Add(offer);
        return chosen;
    }

    /// <summary>The haul's activity: one order, the Deliver step of the stack to its storage cell, until it is done or the comparison horizon (<paramref name="deadline"/>) ends.</summary>
    public static PawnActivities Activity(StoreHaulProposal proposal, ulong authority, string method, long tick, long deadline) =>
        CoordinatedActivityBinding.StartAlone(StoreHaulCandidates.Task(proposal.Haul, proposal.Row, deadline), authority, method, tick,
            proposal.Row.ApproachTicks.Value + proposal.Row.DeliveryTicks.Value);
}
