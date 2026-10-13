namespace RimGovernor.Brain.Policy;

// Go: apparel_policy.go

/// <summary>One apparel definition the pawn can wear; CoversBody marks one covering the torso or legs.</summary>
public sealed record ApparelDefinition
{
    public string Name { get; init; } = "";

    public bool Armor { get; init; }

    public bool Child { get; init; }

    public bool Adult { get; init; }

    public bool CoversBody { get; init; }
}

/// <summary>
/// The pawn's outfit census: its short name (the outfit's label), current outfit (PolicyID, Current), the
/// definitions it can wear, those its title, ideoligion role and apparel precepts require, and whether it goes nude.
/// </summary>
public sealed record ApparelPolicyState
{
    public string Token { get; init; } = "";

    public string PawnName { get; init; } = "";

    public string PolicyID { get; init; } = "";

    public GearRoleInput Role { get; init; } = new();

    public ApparelPolicySpec Current { get; init; } = new(default, "", "", default, 0, 0, 0, 0);

    public bool ExcludesTainted { get; init; }

    public bool Overrides { get; init; }

    public EquatableArray<ApparelDefinition> Definitions { get; init; }

    public EquatableArray<string> Required { get; init; }

    public bool Nude { get; init; }
}

public static partial class PolicyFunctions
{
    // Outfit bounds: the hit-point floor swaps a garment before the tattered thought (below 50%); the quality
    // floor starts at Awful and rises with stock (apparelQualityFloor).
    internal const double ApparelMinHP = .51;
    internal const int QualityAwful = 0;
    internal const int QualityNormal = 2;
    internal const int QualityGood = 3;
    internal const int QualityLegendary = 6;

    /// <summary>
    /// The pawn's own outfit, labelled with its short name: the definitions its role permits (stage-compatible;
    /// combat armor for soldiers only; nothing covering torso or legs for a nude pawn) plus every definition its
    /// title, role or precepts require. All roles exclude tainted and tattered gear; pawns still choose by
    /// temperature inside the filter.
    /// </summary>
    public static bool RoleApparelPolicy(PawnId pawn, GearRole role, ApparelPolicyState state, out ApparelPolicy? policy)
    {
        var s = new ApparelPolicySpec(
            new Domain.PawnId(pawn.Value),
            state.Token,
            state.PawnName,
            EquatableArray.From(RoleApparelDefinitions(role, state)),
            ApparelMinHP,
            1,
            QualityAwful,
            QualityLegendary
        );
        try
        {
            policy = new Domain.ApparelPolicy(s);
            return true;
        }
        catch (DomainException)
        {
            policy = null;
            return false;
        }
    }

    /// <summary>The filter RoleApparelPolicy allows.</summary>
    public static IReadOnlyList<string> RoleApparelDefinitions(GearRole role, ApparelPolicyState state)
    {
        var required = new HashSet<string>(state.Required, StringComparer.Ordinal);
        var r = new List<string>();
        foreach (var d in state.Definitions)
        {
            var stage = (role == GearRole.Child && d.Child) || (role != GearRole.Child && d.Adult);
            if (required.Contains(d.Name) || (stage && (!d.Armor || role == GearRole.Soldier) && (!state.Nude || !d.CoversBody)))
            {
                r.Add(d.Name);
            }
        }

        return r;
    }

    /// <summary>
    /// Raises the outfit's minimum quality from Awful to Normal to Good as stock allows: the highest floor at
    /// which every slot the pawn now wears an allowed garment in still has an allowed, untainted, untattered
    /// garment worn or on hand (loose or stored) at that quality.
    /// </summary>
    internal static int ApparelQualityFloor(GearLoadoutInput model, IReadOnlyCollection<string> allowed)
    {
        bool Permits(GearOption o) =>
            allowed.Contains(o.Definition.ToString()) && !o.Tainted && o.Condition >= ApparelMinHP && o.Source != GearSource.Bill;

        var slots = new HashSet<GearSlot>();
        foreach (var w in model.Worn)
        {
            if (Permits(w))
            {
                slots.Add(w.Slot);
            }
        }

        if (slots.Count == 0)
        {
            return QualityAwful;
        }

        var held = model.Worn.Concat(model.Options).ToList();
        foreach (var floor in new[] { QualityGood, QualityNormal })
        {
            var covered = new HashSet<GearSlot>();
            foreach (var o in held)
            {
                if (Permits(o) && o.Quality >= floor)
                {
                    covered.Add(o.Slot);
                }
            }

            if (slots.All(covered.Contains))
            {
                return floor;
            }
        }

        return QualityAwful;
    }

    public static bool DesiredApparelPolicy(GearPawn p, out ApparelPolicy? desired)
    {
        desired = null;
        if (!p.Policy.TryGetValue(out var state) || p.Blocked)
        {
            return false;
        }

        var role = DeriveGearRole(state.Role);
        var modelled = p.LoadoutModel.TryGetValue(out var model);
        if (modelled)
        {
            role = DeriveGearRole(model.Role);
        }

        if (!RoleApparelPolicy(p.Pawn, role, state, out desired))
        {
            desired = null;
            return false;
        }

        var spec = desired!.Spec;
        if (modelled)
        {
            spec = spec with { MinQuality = ApparelQualityFloor(model, spec.Definitions) };
            try
            {
                desired = new Domain.ApparelPolicy(spec);
            }
            catch (DomainException)
            {
                desired = null;
                return false;
            }
        }

        var current = state.Current;
        var defs = current.Definitions.OrderBy(d => d, StringComparer.Ordinal).ToList();
        if (
            current.Name == spec.Name
            && defs.SequenceEqual(spec.Definitions)
            && current.MinHP >= ApparelMinHP - .00001
            && current.MinHP <= ApparelMinHP + .00001
            && current.MaxHP == spec.MaxHP
            && current.MinQuality == spec.MinQuality
            && current.MaxQuality == spec.MaxQuality
            && state.ExcludesTainted
            && !state.Overrides
        )
        {
            desired = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The outfit prune's keep set: every pawn's own outfit load id, known only once every pawn is observed,
    /// unblocked and already on its desired outfit, so the prune never deletes an outfit a pending write would
    /// reuse. Returns false (and an empty set) when unknown.
    /// </summary>
    public static bool OutfitKeep(IEnumerable<GearPawn> pawns, out IReadOnlyDictionary<string, bool> keepSet)
    {
        var keep = new Dictionary<string, bool>(StringComparer.Ordinal);
        keepSet = new Dictionary<string, bool>();
        foreach (var p in pawns)
        {
            if (!p.Policy.TryGetValue(out var state) || p.Blocked || state.PolicyID.Length == 0)
            {
                return false;
            }

            if (DesiredApparelPolicy(p, out _))
            {
                return false;
            }

            keep[state.PolicyID] = true;
        }

        keepSet = keep;
        return keep.Count > 0;
    }
}
