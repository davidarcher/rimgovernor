namespace RimGovernor.Brain.Policy;

/// <summary>A coarse function a planned room can host. Rooms host capabilities, not fixed roles; nothing here is persisted, and free capacity per room is recomputed each review.</summary>
public enum Capability
{
    Sleep,
    Craft,
    Research,
    Cook,
    Store,
    Heat,
}

// The shelter's heat and cooling pieces: the game offers no signal tying a capability to a def, so these two
// names are judgment tables (DefTables).
public static partial class PolicyConstants
{
    /// <summary>The campfire that provides Capability.Heat.</summary>
    public const string ShelterHeatDef = "Campfire";

    /// <summary>The passive cooler a hot map's shelter holds.</summary>
    public const string ShelterCoolDef = "PassiveCooler";

    /// <summary>
    /// The census room role whose catalog row defines each capability. Heat has no catalog row: only the
    /// shelter's campfire provides it.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Capability, RoomRole> CapabilityHomes = new Dictionary<Capability, RoomRole>
    {
        [Capability.Sleep] = RoomRole.Bedroom,
        [Capability.Craft] = RoomRole.Workshop,
        [Capability.Research] = RoomRole.Laboratory,
        [Capability.Cook] = RoomRole.Kitchen,
        [Capability.Store] = RoomRole.Storeroom,
    };

    /// <summary>
    /// The shelter template's contents: sleeping spots, a work bench, a research bench and a campfire to cook
    /// and warm by. The catalog's shelter row is Pending, so the template is the source.
    /// </summary>
    internal static readonly IReadOnlyList<Capability> ShelterCapabilities = [Capability.Sleep, Capability.Craft, Capability.Research, Capability.Cook, Capability.Heat];

    /// <summary>The floor a capability's piece takes in the shelter template: the crafting spot one cell, the plain research bench 3x2.</summary>
    internal static readonly IReadOnlyDictionary<Capability, int> CapabilityCells = new Dictionary<Capability, int>
    {
        [Capability.Craft] = 1,
        [Capability.Research] = 6,
    };
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// What a room of each planned role can host, derived from FacilityCatalog's Compatible lists through
    /// ModuleRoomRoles. Every planned role has an entry, possibly empty.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<PlannedRole, IReadOnlyList<Capability>>> RoleCapabilities = new(DeriveRoleCapabilities);

    private static Dictionary<PlannedRole, IReadOnlyList<Capability>> DeriveRoleCapabilities()
    {
        Capability[] caps = [Capability.Sleep, Capability.Craft, Capability.Research, Capability.Cook, Capability.Store, Capability.Heat];
        var output = new Dictionary<PlannedRole, IReadOnlyList<Capability>>(PlannedRoleTier.Count);
        foreach (var role in PlannedRoleTier.Keys)
        {
            var hosted = new List<Capability>();
            output[role] = hosted;
            if (!ModuleRoomRoles.TryGetValue(role, out var room))
            {
                continue;
            }
            foreach (var c in caps)
            {
                if (role == PlannedRole.Shelter && ShelterCapabilities.Contains(c))
                {
                    hosted.Add(c);
                    continue;
                }
                if (!CapabilityHomes.TryGetValue(c, out var home))
                {
                    continue;
                }
                try
                {
                    if (Facility(home).Hosts(room))
                    {
                        hosted.Add(c);
                    }
                }
                catch (DomainException)
                {
                    // A role with no catalog row hosts nothing through it.
                }
            }
        }
        return output;
    }

    /// <summary>Lists the capabilities a room of the planned role can host.</summary>
    public static IReadOnlyList<Capability> CapabilitiesOf(PlannedRole role) => [.. RoleCapabilities.Value.GetValueOrDefault(role) ?? []];

    /// <summary>Reports whether the shelter template hosts the capability.</summary>
    public static bool ShelterHosts(Capability c) => ShelterCapabilities.Contains(c);
}
