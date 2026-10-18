using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Contracts.Wire;
using Wl = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A combat fixture's defense layout as the colony's standing layout in the save's governor state.</summary>
public static class CombatLabLayout
{
    /// <summary>
    /// Puts the fixture's layout, as a complete, verified defense layout record for the staged world, into the save's
    /// governor state: the service rebuilds its family tables from the save on the world change, so combat reads it as
    /// the colony's standing layout. A row written into the journal before the serve was wiped by that rebuild. Returns
    /// the stored record.
    /// </summary>
    public static async Task<JsonNode?> StoreAsync(Harness h, JsonObject identity, LabLayout l, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(l);
        var wire = Harness.IdentityOf(identity);
        var world = new World(new ColonyId(wire.ColonyId), new LoadId(wire.LoadToken), new MapId(wire.MapId));
        var record = new DefenseLayoutRecord
        {
            World = world,
            Firing = [.. l.Firing.Select(c => new Cell(c.X, c.Z))],
            Retreat = [.. l.Retreat.Select(c => new Cell(c.X, c.Z))],

            // Validate wants a tier; a fixture's structures are staged, not built by the planner, so its one tier names
            // no buildings.
            Tiers = [new DefenseTierRecord { Name = DefenseLayout.TierFiringLine, Built = true }],
            Complete = true,
        };
        if (l.Choke is { } choke)
        {
            // Combat's choke is the trap lane's last cell.
            record.TrapLane = [new Cell(choke.X, choke.Z)];
        }

        try
        {
            record.Validate();
        }
        catch (DomainException e)
        {
            throw new AcceptanceException(e.Message, e);
        }

        var blob = FamilyBlob(StoreJson.Serialize(record), null);

        // The batch put replaces the whole set, so read the save's blobs and add the layout to them.
        var read = await h.CallAsync("store-layout-read", NativeOps.ReadGovernorState, new Wl.GovernorStateRequest(), cancellationToken).ConfigureAwait(false);
        var loaded = read.Loaded ?? throw new AcceptanceException($"store-layout-read: {WireJson.Serialize(read)}");
        var blobs = new Dictionary<string, string>(loaded.Blobs, StringComparer.Ordinal)
        {
            [Store.Store.GovernorDefenseLayoutKey] = blob,
        };

        // Combat reads the direction toward home from the plan's killbox opening, so the staged world holds a plan whose
        // only content is that opening.
        var plan = LayoutPlan.OpeningPlan(Toward(l.Toward));
        blobs[Store.Store.GovernorLayoutPlanKey] = FamilyBlob(StoreJson.Serialize(plan), (world.Colony.Value, world.Map.Value));
        var reply = await h.CallAsync("store-layout", NativeOps.PutGovernorStateBatch, new Wl.PutGovernorStateBatchRequest { Blobs = blobs }, cancellationToken).ConfigureAwait(false);
        _ = reply.Loaded ?? throw new AcceptanceException($"store-layout: {WireJson.Serialize(reply)}");

        return JsonNode.Parse(StoreJson.Serialize(record));
    }

    private static Rotation Toward(string name) => name switch
    {
        "north" => Rotation.North,
        "east" => Rotation.East,
        "south" => Rotation.South,
        "west" => Rotation.West,
        _ => throw new AcceptanceException($"layout toward \"{name}\" is not a rotation"),
    };

    // One governor state family blob: {schemaVersion, scope?, record}.
    private static string FamilyBlob(string recordJson, (string Colony, int Map)? scope)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", Store.Store.GovernorStateSchemaVersion);
            if (scope is { } s)
            {
                w.WriteStartObject("scope");
                w.WriteString("colony", s.Colony);
                w.WriteNumber("map", s.Map);
                w.WriteNumber("tick", 0);
                w.WriteEndObject();
            }

            w.WritePropertyName("record");
            w.WriteRawValue(recordJson);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
