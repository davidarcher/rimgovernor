using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>animals/containment</c> (Go <c>containment.go</c>): the MaintainAnimalContainment native dispatch vertical end to
/// end. One <c>operations_apply</c> batch of building intents authors a durable Fence/FenceGate enclosure and one
/// PenMarker inside it, then real game ticks build it and carry a genuinely uncontained, pen-requiring herd animal into
/// that pen, observed through <c>observations_read_colony_facts</c> (Contained=true, a non-empty PenId), not just a
/// receipt. A non-pen-requiring pet confirms RequiresPen=false is left alone. It uses the private disposable fixture
/// <c>test/containment_construct_prepare</c>, since a deterministic legal 6x6 pen-shell room with reachable WoodLog and a
/// capable Construction/Handling colonist cannot be relied on from native random pawn generation.
/// </summary>
internal static class AnimalsContainment
{
    private const string SessionOwner = "native-animal-containment-acceptance";

    internal static AcceptanceCase Case() => new()
    {
        Name = "animals/containment",
        Scope = "Native MaintainAnimalContainment dispatch: a real Fence/FenceGate enclosure and "
            + "PenMarker authored as Actions/Apply building intents, then a genuinely uncontained "
            + "pen-requiring herd animal carried into that pen by real native ticks (observed via "
            + "observations_read_colony_facts, not just a receipt), while a non-pen-requiring pet is left alone.",

        // Sited on the audited baseline: an unpinned debug start draws a fresh world each run and may offer no legal pen room.
        Start = new FixtureStart { Op = "test/containment_construct_prepare", On = new LabStart() },
        Keep = [NeedDef.LiveNeeds.Value],
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private sealed record CellPlan(string DefName, string Stuff, int X, int Z);

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        // Fixture: clears a legal 6x6 room, spawns just enough WoodLog for the full shell, spawns the uncontained herd
        // animal + non-pen pet, and enables one colonist's Construction/Handling. Run this BEFORE granting Auto so the
        // fixture's own direct spawning bumps the native generation before the first WritePrecondition reads it. Needs
        // stay live: with every colonist's needs pinned the PenMarker blueprint never gets built within its day budget,
        // while the unfrozen colony finishes it as it always has.
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        if (!Dyn.Contains(s.Names, "rimgovernor/operations_apply"))
        {
            throw new AcceptanceException("missing rimgovernor/operations_apply in discovery");
        }

        var animalId = prepared["animal"].AsString();
        var petId = prepared["pet"].AsString();
        if (animalId.Length == 0 || petId.Length == 0)
        {
            throw new AcceptanceException($"prepare: missing fixture animal/pet ids: {prepared.ToJson()}");
        }

        report["fixture_animal"] = animalId;
        report["fixture_pet"] = petId;
        var room = prepared["room"].AsMap() ?? [];
        var roomX = (int)room["x"].AsNumber();
        var roomZ = (int)room["z"].AsNumber();
        var roomWidth = (int)room["width"].AsNumber();
        var roomHeight = (int)room["height"].AsNumber();
        if (roomWidth != 6 || roomHeight != 6)
        {
            throw new AcceptanceException($"prepare: unexpected pen shell room: {room.ToJson()}");
        }

        report["fixture_room"] = room;

        // Authority is a single Auto/Manual mode switch (SIMP02): Actions/Apply applies under current native authority,
        // so Auto is granted once.
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "grant-auto", identity, cancellationToken).ConfigureAwait(false);

        // Author the whole pen in one Actions/Apply batch: 19 Fence + 1 FenceGate matching the pen ring's perimeter/door
        // layout (door anchoring the south wall's center, Fence elsewhere), and the PenMarker at the shell's
        // nearest-northwest interior corner.
        var door = new CellPlan("FenceGate", "WoodLog", roomX + (roomWidth / 2), roomZ);
        var pen = new List<CellPlan> { door };
        for (var x = roomX; x < roomX + roomWidth; x++)
        {
            for (var z = roomZ; z < roomZ + roomHeight; z++)
            {
                if (x == door.X && z == door.Z)
                {
                    continue;
                }

                if (x == roomX || x == roomX + roomWidth - 1 || z == roomZ || z == roomZ + roomHeight - 1)
                {
                    pen.Add(new CellPlan("Fence", "WoodLog", x, z));
                }
            }
        }

        if (pen.Count != 20)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"expected a 20-cell pen shell perimeter (19 Fence + 1 FenceGate), got {pen.Count}"));
        }

        pen.Add(new CellPlan("PenMarker", "", roomX + 1, roomZ + 1));
        var actions = new JsonArray();
        for (var i = 0; i < pen.Count; i++)
        {
            var cell = pen[i];
            var placement = new JsonObject { ["defName"] = cell.DefName, ["x"] = cell.X, ["z"] = cell.Z, ["rotation"] = "ROTATION_NORTH" };
            if (cell.Stuff.Length > 0)
            {
                placement["stuff"] = cell.Stuff;
            }

            actions.Add(new JsonObject
            {
                ["key"] = string.Create(CultureInfo.InvariantCulture, $"{SessionOwner}-pen-{i}"),
                ["building"] = new JsonObject { ["placement"] = placement },
            });
        }

        var request = new JsonObject { ["identity"] = identity.DeepClone(), ["actions"] = actions };
        var reply = await h.WireAsync("apply-pen", "operations_apply", request, cancellationToken).ConfigureAwait(false);
        var results = reply["results"].AsSlice();
        if (results.Count != pen.Count)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"apply-pen: expected {pen.Count} results, got {reply.ToJson()}"));
        }

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i].AsMap();
            var observed = result?["applied"].AsMap()?["applied"].AsMap()?["observed"].AsMap();
            var construction = observed?["construction"].AsMap()
                ?? throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"apply-pen {i} ({pen[i].DefName}): expected an applied construction effect, got {result.ToJson()}"));
            if (construction["stage"].AsString() != "CONSTRUCTION_STAGE_BLUEPRINT")
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"apply-pen {i} ({pen[i].DefName}): expected an initial Blueprint stage, got {construction.ToJson()}"));
            }
        }

        report["pen_cells_placed"] = results.Count;

        // Replay: resending the same keys returns the first results unchanged.
        var replay = await h.WireAsync("replay-pen", "operations_apply", request, cancellationToken).ConfigureAwait(false);
        if (!Dyn.DeepEqual(replay, reply))
        {
            throw new AcceptanceException("replay-pen: resent keys returned different results");
        }

        // --- Observe real containment: run ticks forward until the fixture's prepared handler is independently
        // observed to have actually carried the herd animal into the new pen (Contained=true, a real PenId), not merely
        // inferred from the shell/marker construction finishing. The budget covers building the shell (up to 4 days) and
        // marker as well. ---
        async Task<JsonObject> AnimalRow(string label, string id)
        {
            var facts = await h.WireAsync(
                label,
                "observations_read_colony_facts",
                new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
                cancellationToken).ConfigureAwait(false);
            var (_, observedSnapshot) = Harness.Outcome(facts, "observed");
            var upkeepSection = observedSnapshot["upkeep"].AsMap() ?? [];
            JsonObject upkeep;
            try
            {
                (_, upkeep) = Harness.Outcome(upkeepSection, "observed");
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{label}: upkeep section unavailable: {e.Message}", e);
            }

            foreach (var raw in upkeep["animals"].AsSlice())
            {
                var row = raw.AsMap();
                if (row is not null && PawnTable.PawnRef(row) == id)
                {
                    // The herd facts ride the pawn table row.
                    await h.JoinPawnAsync(label, identity, row, cancellationToken).ConfigureAwait(false);
                    return row;
                }
            }

            throw new AcceptanceException($"{label}: animal {id} not found in upkeep census");
        }

        JsonObject? animalContained = null;
        try
        {
            _ = await RunAt.RunUntilAsync(
                h,
                "observe-contain",
                8UL * Wait.TicksPerDay,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var row = await AnimalRow("contain-poll", animalId).ConfigureAwait(false);
                    var state = row["pawn"].AsMap()?["animalState"].AsMap();
                    if (state?["contained"].AsBool() == true && state["penId"].AsString().Length > 0)
                    {
                        animalContained = row;
                        return ("", true);
                    }

                    return ("", false);
                },
                quiet: true,
                report,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"observe-contain: animal did not become contained: {e.Message}", e);
        }

        var animalState = animalContained?["pawn"].AsMap()?["animalState"].AsMap() ?? [];
        report["animal_contained"] = true;
        report["animal_pen_id"] = animalState["penId"].AsString();
        if (animalState["release"].AsBool() == true)
        {
            throw new AcceptanceException("observe-contain: expected the contained animal to have no release designation");
        }

        if (animalState["slaughter"].AsBool() == true)
        {
            throw new AcceptanceException("observe-contain: expected the contained animal to have no slaughter designation");
        }

        // Independently confirm the fixture's non-pen-requiring pet was left alone: no pen was ever assigned to it.
        var petRowFinal = await AnimalRow("pet-after", petId).ConfigureAwait(false);
        var petAnimalState = petRowFinal["pawn"].AsMap()?["animalState"].AsMap() ?? [];
        if (petRowFinal["requiresPen"].AsBool() == true)
        {
            throw new AcceptanceException($"pet-after: expected the fixture pet to not require a pen: {petRowFinal.ToJson()}");
        }

        var penId = petAnimalState["penId"].AsString();
        if (penId.Length > 0)
        {
            throw new AcceptanceException($"pet-after: expected the non-pen pet to have no pen assigned, got \"{penId}\"");
        }

        report["pet_requires_pen"] = false;
    }
}
