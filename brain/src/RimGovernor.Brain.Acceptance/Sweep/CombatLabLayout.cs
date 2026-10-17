using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;

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
        var world = new World(
            new ColonyId(identity["colonyId"].AsString()),
            new LoadId(identity["loadToken"].AsString()),
            new MapId((int)identity["mapId"].NumberOrZero()));
        var record = new DefenseLayoutRecord
        {
            World = world,
            Firing = [.. l.Firing.Select(c => new Cell(c.X, c.Z))],
            Retreat = [.. l.Retreat.Select(c => new Cell(c.X, c.Z))],

            // Validate wants a tier; a fixture's structures are staged, not built by the planner, so its one tier names
            // no buildings.
            Tiers = [new DefenseTierRecord { Name = PolicyFunctions.TierFiringLine, Built = true }],
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
        var read = await h.WireAsync("store-layout-read", "lifecycle_read_governor_state", new JsonObject(), cancellationToken).ConfigureAwait(false);
        JsonObject loaded;
        try
        {
            loaded = Harness.Outcome(read, "loaded").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"store-layout-read: {e.Message}", e);
        }

        var blobs = new JsonObject();
        foreach (var (key, value) in loaded["blobs"].AsMap() ?? [])
        {
            blobs[key] = value.AsString();
        }

        blobs[Store.Store.GovernorDefenseLayoutKey] = blob;

        // Combat reads the direction toward home from the plan's killbox opening, so the staged world holds a plan whose
        // only content is that opening.
        var plan = PolicyFunctions.OpeningPlan(Toward(l.Toward));
        blobs[Store.Store.GovernorLayoutPlanKey] = FamilyBlob(StoreJson.Serialize(plan), (world.Colony.Value, world.Map.Value));
        var reply = await h.WireAsync("store-layout", "lifecycle_put_governor_state_batch", new JsonObject { ["blobs"] = blobs }, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = Harness.Outcome(reply, "loaded");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"store-layout: {e.Message}", e);
        }

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
