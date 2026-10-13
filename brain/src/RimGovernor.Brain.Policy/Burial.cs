namespace RimGovernor.Brain.Policy;

// Go: burial.go
// MaintainBurial is the People department's burial: the tomb's sarcophagus or plain-grave step, the morgue's
// shell and the graveyard's fence and graves, and the tomb, morgue and graveyard stores. It is a Standard
// whose target is no human corpse owed a shell, a sarcophagus or a grave. Vanilla haulers carry the corpses;
// nothing here hauls.

/// <summary>
/// What the burial concern reads of the colony: the plan, the waste census, the colony's buildings and
/// whether a sarcophagus can be had.
/// </summary>
public sealed record BurialCensus
{
    public EquatableArray<WasteItem> Waste { get; init; }

    public EquatableArray<CurrentBuilding> Built { get; init; }

    public required PieceShapes Shapes { get; init; }

    public bool Sarcophagus { get; init; }
}

public static partial class PolicyFunctions
{
    public static readonly ConcernId MaintainBurial = new("MaintainBurial");

    /// <summary>Ranks MaintainBurial with the other priority-3 chores.</summary>
    internal const int BurialPriority = 3;

    /// <summary>
    /// Ranks the morgue below graves and sarcophagi, which take a colonist corpse the morgue holds until one
    /// has room, and above the Low corpse dump. The native acceptance case burial/grave_over_morgue verifies
    /// the ranking.
    /// </summary>
    public const StockpilePriority MorguePriority = StockpilePriority.Normal;

    /// <summary>Raises MaintainBurial while a tomb, grave or morgue step is due.</summary>
    internal static void InspectBurial(RoundsRun c)
    {
        var recovered = Unknown<bool>();
        if (c.F.BurialOwed.TryGetValue(out var owed))
        {
            recovered = Known(!owed);
        }

        c.Assess(MaintainBurial, BurialPriority, recovered);
        if (!Positive(recovered))
        {
            c.Raise(MaintainBurial, BurialPriority);
        }
    }

    /// <summary>
    /// The graveyards the plan should hold: 0 for no demand, else the planned count plus one. A further
    /// graveyard is asked for only while no sarcophagus can be had (once one can, the tomb rooms take new
    /// burials) and when the empty graves run out: fewer free grave slots than the unburied dead still owed
    /// one, or the graveyards at StockpileFurtherRoomFill of their slots.
    /// </summary>
    public static int GraveyardsWanted(
        LayoutPlan plan,
        IReadOnlyList<WasteItem> waste,
        IReadOnlyList<CurrentBuilding> built,
        PieceShapes shapes,
        bool sarcophagus
    )
    {
        var have = plan.RoomsOf(PlannedRole.Graveyard).Count;
        if (have == 0 || sarcophagus)
        {
            return 0;
        }

        var (standing, slots) = GravesStanding(plan, built);

        // No sarcophagus can be had here, so no stranger is staged (StrangerTomb).
        var (step, _) = TombCensus(waste, built, shapes.Furniture.Sarcophagus, 0);
        var owed = Math.Max(step.Dead - step.Empty, 0);
        if (owed > slots - standing || standing >= StockpileFurtherRoomFill * slots)
        {
            return have + 1;
        }

        return 0;
    }
}

/// <summary>
/// The People department's stores: the tomb and the morgue; the graveyard's graves are buildings and need no
/// zone. The graveyard's room demand is the further graveyard (<see cref="PolicyFunctions.GraveyardsWanted"/>).
/// </summary>
public sealed class BurialOwner : IStoreOwner
{
    public Department Department => Policy.Department.People;

    public IReadOnlyList<Store> Stores(StoreView view)
    {
        if (view.Layout is null)
        {
            return [];
        }

        var output = new List<Store>();
        var roles = new (PlannedRole Role, string Prefix, StockpileFilter Filter, StockpilePriority Priority)[]
        {
            (PlannedRole.Tomb, DomainConstants.TombRolePrefix, StockpileFilter.TombCorpsesFilter(), StockpilePriority.Critical),
            (PlannedRole.Morgue, DomainConstants.MorgueRolePrefix, StockpileFilter.MorgueCorpsesFilter(), PolicyFunctions.MorguePriority),
        };
        foreach (var s in roles)
        {
            // The first planned room of the role holds the store.
            foreach (var room in view.Layout.RoomsOf(s.Role))
            {
                var bounds = room.Region.Bounds();
                var key = $"{bounds.X}_{bounds.Z}";
                output.Add(
                    new Store
                    {
                        Site = new StoreSite
                        {
                            Role = s.Prefix + key,
                            Interior = bounds,
                            Region = room.Region,
                            Filter = s.Filter,
                            Priority = s.Priority,
                        },
                    }
                );
                break;
            }
        }

        return output;
    }

    public RoomDemand RoomDemand(StoreView view)
    {
        if (view.Layout is null || view.Burial is null)
        {
            return new RoomDemand();
        }

        var b = view.Burial;
        return new RoomDemand { Graveyards = PolicyFunctions.GraveyardsWanted(view.Layout, b.Waste.Items, b.Built.Items, b.Shapes, b.Sarcophagus) };
    }
}
