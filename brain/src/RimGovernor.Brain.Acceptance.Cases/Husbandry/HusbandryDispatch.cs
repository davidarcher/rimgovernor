using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>husbandry/dispatch</c> (Go <c>dispatch.go</c>): the animals/herd native husbandry vertical end to end. Each
/// direct-write animal order (train, slaughter, tame, release, allowed area, master, following and the designation
/// cancels) is one HusbandryIntent on <c>operations_apply</c>, against animals read live through
/// <c>observations_read_husbandry</c>. All of them are immediate settings writes with no native job, so applied is the
/// effect and the case reads the settings back at once. It uses the private disposable fixture
/// <c>test/husbandry_setup</c>, since a deterministic animal with a known one-step-remaining trainable, a
/// safe-to-slaughter surplus candidate, a releasable animal, a factionless tameable animal and a protected (pregnant)
/// animal cannot be relied on from native random pawn generation.
/// </summary>
internal static class HusbandryDispatch
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "husbandry/dispatch",
        Scope = "Native animals/herd husbandry vertical: train/slaughter/tame/release HusbandryIntents applied through "
            + "operations_apply, pregnant-animal-protected and wrong-faction refusals, immediate settings readback (not a "
            + "native job), the wild census the tame planner reads, allowed-area/master/following writes with their "
            + "obedience and area-existence refusals, designation cancels, and resends of orders that already hold applying again.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/husbandry_setup" },
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        // Fixture: single-handler colony, roofed enclosure with bed/food, a near-term-pregnant mother, an unrelated
        // father, a full-producing cow and a dog one training step short of Obedience. Run this BEFORE acquiring
        // authority: the fixture spawns/despawns pawns directly outside any authority scope, and native control
        // authority revokes Auto for such external activity.
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        foreach (var required in new[] { "rimgovernor/operations_apply", "rimgovernor/observations_read_husbandry" })
        {
            if (!Dyn.Contains(s.Names, required))
            {
                throw new AcceptanceException($"missing {required} in discovery");
            }
        }

        var motherId = prepared["mother"].AsString();
        var fatherId = prepared["father"].AsString();
        var cowId = prepared["cow"].AsString();
        var dogId = prepared["dog"].AsString();
        var wildId = prepared["wild"].AsString();
        var guardId = prepared["guard"].AsString();
        var handlerId = prepared["handler"].AsString();
        var areaId = prepared["area"].AsString();
        if (new[] { motherId, fatherId, cowId, dogId, wildId, guardId, handlerId, areaId }.Any(id => id.Length == 0))
        {
            throw new AcceptanceException($"prepare: missing fixture ids: {prepared.ToJson()}");
        }

        report["fixture_guard"] = guardId;
        report["fixture_area"] = areaId;
        report["fixture_mother"] = motherId;
        report["fixture_father"] = fatherId;
        report["fixture_cow"] = cowId;
        report["fixture_dog"] = dogId;
        report["fixture_wild"] = wildId;

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "grant-auto", identity, cancellationToken).ConfigureAwait(false);

        // HerdRow reads the whole census through observations_read_husbandry, the read the husbandry planners consume;
        // null when the animal is not in it.
        async Task<JsonObject?> FindHerdRow(string label, string animalId, bool includeWild)
        {
            var request = new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } };
            if (includeWild)
            {
                request["includeWild"] = true;
            }

            var reply = await h.WireAsync(label, "observations_read_husbandry", request, cancellationToken).ConfigureAwait(false);
            var (_, observed) = Harness.Outcome(reply, "observed");
            foreach (var raw in observed["animals"].AsSlice())
            {
                var row = raw.AsMap();
                if (row is not null && PawnTable.PawnRef(row) == animalId)
                {
                    return row;
                }
            }

            return null;
        }

        async Task<JsonObject> HerdRowScoped(string label, string animalId, bool includeWild) =>
            await FindHerdRow(label, animalId, includeWild).ConfigureAwait(false)
                ?? throw new AcceptanceException($"{label}: animal {animalId} not found in husbandry census");

        Task<JsonObject> HerdRow(string label, string animalId) => HerdRowScoped(label, animalId, false);

        static JsonObject? TrainingEntry(JsonObject row, string def)
        {
            foreach (var raw in row["animal"].AsMap()?["training"].AsSlice() ?? [])
            {
                var entry = raw.AsMap();
                if (entry?["defName"].AsString() == def)
                {
                    return entry;
                }
            }

            return null;
        }

        static JsonObject Animal(JsonObject row) => row["animal"].AsMap() ?? [];

        // --- Preconditions: the fixture's dog genuinely has an available, not-yet-wanted Obedience trainable; the cow is
        // genuinely slaughterable; the mother reads pregnant (policy protects her, native does not refuse). ---
        var dogRow = await HerdRow("dog-before", dogId).ConfigureAwait(false);
        var obedience = TrainingEntry(dogRow, "Obedience")
            ?? throw new AcceptanceException($"dog-before: missing Obedience trainable entry: {dogRow.ToJson()}");
        if (obedience["available"].AsBool() != true)
        {
            throw new AcceptanceException($"dog-before: expected Obedience to be available, got {obedience.ToJson()}");
        }

        if (obedience["learned"].AsBool() == true)
        {
            throw new AcceptanceException($"dog-before: expected Obedience to be unlearned, got {obedience.ToJson()}");
        }

        if (obedience["wanted"].AsBool() == true)
        {
            throw new AcceptanceException($"dog-before: expected Obedience to be unwanted before dispatch, got {obedience.ToJson()}");
        }

        var cowAnimal = Animal(await HerdRow("cow-before", cowId).ConfigureAwait(false));
        if (cowAnimal["slaughterDesignatable"].AsBool() != true)
        {
            throw new AcceptanceException($"cow-before: expected the slaughter designator to accept the fixture cow, got {cowAnimal.ToJson()}");
        }

        foreach (var flag in new[] { "downed", "inMentalState", "pregnant", "colonistBonded" })
        {
            if (cowAnimal[flag].AsBool() == true)
            {
                throw new AcceptanceException($"cow-before: expected {flag} clear on the fixture cow, got {cowAnimal.ToJson()}");
            }
        }

        if (cowAnimal["slaughter"].AsBool() == true)
        {
            throw new AcceptanceException($"cow-before: expected no pre-existing slaughter designation, got {cowAnimal.ToJson()}");
        }

        var motherAnimal = Animal(await HerdRow("mother-before", motherId).ConfigureAwait(false));
        if (motherAnimal["pregnant"].AsBool() != true)
        {
            throw new AcceptanceException($"mother-before: expected the raw pregnant fact on the near-term mother, got {motherAnimal.ToJson()}");
        }

        // Apply sends one HusbandryIntent on Actions/Apply and returns the action's result: applied carries the animal
        // evidence, refused the native reason.
        async Task<JsonObject> Apply(string label, string key, string animalId, string order, JsonObject? extra)
        {
            var intent = new JsonObject { ["animalId"] = animalId, ["order"] = "HUSBANDRY_ORDER_" + order };
            foreach (var (name, value) in extra ?? [])
            {
                intent[name] = value?.DeepClone();
            }

            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = key, ["husbandry"] = intent }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            if (results.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected one result, got {reply.ToJson()}");
            }

            return results[0].AsMap() ?? [];
        }

        // Applied returns the AnimalEffect of an applied result.
        async Task<JsonObject> Applied(string label, string key, string animalId, string order, JsonObject? extra)
        {
            var result = await Apply(label, key, animalId, order, extra).ConfigureAwait(false);
            return result["applied"].AsMap()?["applied"].AsMap()?["observed"].AsMap()?["animal"].AsMap()
                ?? throw new AcceptanceException($"{label}: expected applied animal evidence, got {result.ToJson()}");
        }

        // Refused asserts a refusal with the given code whose reason names the rule that failed.
        async Task Refused(string label, string key, string animalId, string order, JsonObject? extra, string code, string reason)
        {
            var result = await Apply(label, key, animalId, order, extra).ConfigureAwait(false);
            var refusal = result["refused"].AsMap();
            var got = refusal?["reason"].AsString() ?? "";
            if (refusal is null || refusal["code"].AsString() != code || !got.Contains(reason, StringComparison.Ordinal))
            {
                throw new AcceptanceException($"{label}: expected {code} \"{reason}\", got {result.ToJson()}");
            }

            report[label.Replace('-', '_')] = got;
        }

        // Train: the real native SetWantedRecursive write, read back at once: a direct write with no native job, so no
        // game ticks are needed.
        var trainEffect = await Applied("apply-train", "husbandry-train", dogId, "TRAIN", new JsonObject { ["trainableDef"] = "Obedience" }).ConfigureAwait(false);
        if (trainEffect["wanted"].AsBool() != true || trainEffect["trainableDef"].AsString() != "Obedience")
        {
            throw new AcceptanceException($"apply-train: expected Obedience wanted, got {trainEffect.ToJson()}");
        }

        var afterTrainRow = await HerdRow("dog-after-train", dogId).ConfigureAwait(false);
        var trained = TrainingEntry(afterTrainRow, "Obedience")
            ?? throw new AcceptanceException($"dog-after-train: missing Obedience trainable entry: {afterTrainRow.ToJson()}");
        if (trained["wanted"].AsBool() != true)
        {
            throw new AcceptanceException($"dog-after-train: expected Obedience to be wanted, got {trained.ToJson()}");
        }

        // A training request that already holds applies again.
        _ = await Applied("reapply-train", "husbandry-train-again", dogId, "TRAIN", new JsonObject { ["trainableDef"] = "Obedience" }).ConfigureAwait(false);
        report["training_dispatched"] = true;

        // --- Slaughter: the same immediate-write shape, on the fixture's genuinely slaughterable cow; resending applies
        // again. ---
        foreach (var label in new[] { "apply-slaughter", "reapply-slaughter" })
        {
            var effect = await Applied(label, "husbandry-" + label, cowId, "SLAUGHTER", null).ConfigureAwait(false);
            if (effect["slaughterDesignated"].AsBool() != true)
            {
                throw new AcceptanceException($"{label}: expected a slaughter designation, got {effect.ToJson()}");
            }
        }

        var afterSlaughterAnimal = Animal(await HerdRow("cow-after-slaughter", cowId).ConfigureAwait(false));
        if (afterSlaughterAnimal["slaughter"].AsBool() != true)
        {
            throw new AcceptanceException($"cow-after-slaughter: expected the slaughter designation to be observable, got {afterSlaughterAnimal.ToJson()}");
        }

        report["slaughter_dispatched"] = true;

        // --- Tame: the factionless muffalo is absent from the plain herd read, present with includeWild, and reads as
        // tameable; the wild census the routine planner consumes (colony facts upkeep.wildAnimals) agrees. ---
        if (await FindHerdRow("wild-plain-read", wildId, false).ConfigureAwait(false) is not null)
        {
            throw new AcceptanceException("wild-plain-read: a factionless animal must not appear in the player herd read");
        }

        var wildAnimal = Animal(await HerdRowScoped("wild-before", wildId, true).ConfigureAwait(false));
        if (wildAnimal["tameable"].AsBool() != true)
        {
            throw new AcceptanceException($"wild-before: expected the fixture muffalo to be tameable, got {wildAnimal.ToJson()}");
        }

        if (wildAnimal["tame"].AsBool() == true)
        {
            throw new AcceptanceException($"wild-before: expected no pre-existing tame designation, got {wildAnimal.ToJson()}");
        }

        var factsReply = await h.WireAsync(
            "colony-facts-wild",
            "observations_read_colony_facts",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var (_, factsSnapshot) = Harness.Outcome(factsReply, "observed");
        JsonObject upkeep;
        try
        {
            (_, upkeep) = Harness.Outcome(factsSnapshot["upkeep"].AsMap() ?? [], "observed");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"colony-facts-wild: upkeep section unavailable: {e.Message}", e);
        }

        var wildCensusFound = false;
        foreach (var raw in upkeep["wildAnimals"].AsSlice())
        {
            var row = raw.AsMap();
            if (row is null || PawnTable.PawnRef(row) != wildId)
            {
                continue;
            }

            // The tame facts ride the pawn table row.
            await h.JoinPawnAsync("colony-facts-wild", identity, row, cancellationToken).ConfigureAwait(false);
            var state = row["pawn"].AsMap()?["animalState"].AsMap();
            if (state?["tameable"].AsBool() != true)
            {
                throw new AcceptanceException($"colony-facts-wild: expected tameable in the wild census, got {state.ToJson()}");
            }

            wildCensusFound = true;
        }

        if (!wildCensusFound)
        {
            throw new AcceptanceException($"colony-facts-wild: fixture muffalo {wildId} missing from upkeep.wildAnimals");
        }

        foreach (var raw in upkeep["animals"].AsSlice())
        {
            var row = raw.AsMap();
            if (row is not null && PawnTable.PawnRef(row) == wildId)
            {
                throw new AcceptanceException("colony-facts-wild: factionless muffalo must not be in the player animal census");
            }
        }

        report["wild_census_observed"] = true;

        // Refusal: taming a player animal is refused (not found as a wild target).
        await Refused(
            "tame-player-animal",
            "husbandry-tame-player",
            cowId,
            "TAME",
            null,
            "FAILURE_CODE_NOT_FOUND",
            "Husbandry refused: animal " + cowId + " is not a wild animal on the map").ConfigureAwait(false);
        foreach (var label in new[] { "apply-tame", "reapply-tame" })
        {
            var effect = await Applied(label, "husbandry-" + label, wildId, "TAME", null).ConfigureAwait(false);
            if (effect["tameDesignated"].AsBool() != true)
            {
                throw new AcceptanceException($"{label}: expected a tame designation, got {effect.ToJson()}");
            }
        }

        var afterTameAnimal = Animal(await HerdRowScoped("wild-after-tame", wildId, true).ConfigureAwait(false));
        if (afterTameAnimal["tame"].AsBool() != true)
        {
            throw new AcceptanceException($"wild-after-tame: expected the tame designation to be observable, got {afterTameAnimal.ToJson()}");
        }

        if (afterTameAnimal["tameable"].AsBool() == true)
        {
            throw new AcceptanceException($"wild-after-tame: a designated animal must no longer read as a tame candidate, got {afterTameAnimal.ToJson()}");
        }

        report["tame_dispatched"] = true;

        // --- Release: the same immediate-write shape on the fixture's unbonded, unmastered father muffalo. ---
        var fatherAnimal = Animal(await HerdRow("father-before", fatherId).ConfigureAwait(false));
        if (fatherAnimal["safeToRelease"].AsBool() != true)
        {
            throw new AcceptanceException($"father-before: expected the fixture father to be safe to release, got {fatherAnimal.ToJson()}");
        }

        var releaseEffect = await Applied("apply-release", "husbandry-release", fatherId, "RELEASE", null).ConfigureAwait(false);
        if (releaseEffect["releaseDesignated"].AsBool() != true)
        {
            throw new AcceptanceException($"apply-release: expected a release designation, got {releaseEffect.ToJson()}");
        }

        var afterReleaseAnimal = Animal(await HerdRow("father-after-release", fatherId).ConfigureAwait(false));
        if (afterReleaseAnimal["release"].AsBool() != true)
        {
            throw new AcceptanceException($"father-after-release: expected the release designation to be observable, got {afterReleaseAnimal.ToJson()}");
        }

        report["release_dispatched"] = true;

        // --- Animals-tab settings: allowed area, master and following on the obedient guard, read back through
        // AnimalState. Each write's evidence is the animal's actual post-write value, so a clear reads back empty. ---
        var guardAnimal = Animal(await HerdRow("guard-before", guardId).ConfigureAwait(false));
        if (guardAnimal["obedient"].AsBool() != true)
        {
            throw new AcceptanceException($"guard-before: expected the fixture guard to read as obedient, got {guardAnimal.ToJson()}");
        }

        if (guardAnimal["supportsAllowedAreas"].AsBool() != true)
        {
            throw new AcceptanceException($"guard-before: expected the guard to support allowed areas, got {guardAnimal.ToJson()}");
        }

        if (guardAnimal["allowedAreaId"].AsString().Length > 0 || guardAnimal["masterId"].AsString().Length > 0)
        {
            throw new AcceptanceException($"guard-before: expected no pre-existing area or master, got {guardAnimal.ToJson()}");
        }

        // The dog has not learned Obedience: master and following are refused before any write, and the untrained dog
        // reads as not obedient.
        var dogAnimal = Animal(await HerdRow("dog-settings", dogId).ConfigureAwait(false));
        if (dogAnimal["obedient"].AsBool() == true)
        {
            throw new AcceptanceException($"dog-settings: expected the untrained dog to read as not obedient, got {dogAnimal.ToJson()}");
        }

        await Refused(
            "master-untrained",
            "husbandry-master-untrained",
            dogId,
            "MASTER",
            new JsonObject { ["targetId"] = handlerId },
            "FAILURE_CODE_INVALID_REQUEST",
            "Husbandry refused: a master requires learned Obedience").ConfigureAwait(false);
        await Refused(
            "follow-untrained",
            "husbandry-follow-untrained",
            dogId,
            "FOLLOW_DRAFTED",
            new JsonObject { ["follow"] = true },
            "FAILURE_CODE_INVALID_REQUEST",
            "Husbandry refused: following requires learned Obedience").ConfigureAwait(false);

        // An area id the map does not carry is refused as not found.
        await Refused(
            "area-missing",
            "husbandry-area-missing",
            guardId,
            "ALLOWED_AREA",
            new JsonObject { ["targetId"] = "Area_Allowed_999999" },
            "FAILURE_CODE_NOT_FOUND",
            "Husbandry refused: allowed area Area_Allowed_999999 is not on the animal's map").ConfigureAwait(false);
        report["negative_settings_refusals"] = true;

        // Area assign, then readback and clear.
        var areaEffect = await Applied("apply-area", "husbandry-area", guardId, "ALLOWED_AREA", new JsonObject { ["targetId"] = areaId }).ConfigureAwait(false);
        if (areaEffect["allowedAreaId"].AsString() != areaId)
        {
            throw new AcceptanceException($"apply-area: expected allowedAreaId {areaId} in the effect, got {areaEffect.ToJson()}");
        }

        var afterAreaAnimal = Animal(await HerdRow("guard-after-area", guardId).ConfigureAwait(false));
        if (afterAreaAnimal["allowedAreaId"].AsString() != areaId)
        {
            throw new AcceptanceException($"guard-after-area: expected the area to be observable, got {afterAreaAnimal.ToJson()}");
        }

        var clearEffect = await Applied("apply-area-clear", "husbandry-area-clear", guardId, "ALLOWED_AREA", null).ConfigureAwait(false);
        if (!clearEffect.ContainsKey("allowedAreaId") || clearEffect["allowedAreaId"].AsString().Length > 0)
        {
            throw new AcceptanceException($"apply-area-clear: expected an empty allowedAreaId in the effect, got {clearEffect.ToJson()}");
        }

        var afterClearAnimal = Animal(await HerdRow("guard-after-area-clear", guardId).ConfigureAwait(false));
        if (afterClearAnimal["allowedAreaId"].AsString().Length > 0)
        {
            throw new AcceptanceException($"guard-after-area-clear: expected no area after clearing, got {afterClearAnimal.ToJson()}");
        }

        report["area_dispatched"] = true;

        // Master: the handler becomes the guard's master.
        var masterEffect = await Applied("apply-master", "husbandry-master", guardId, "MASTER", new JsonObject { ["targetId"] = handlerId }).ConfigureAwait(false);
        if (masterEffect["masterId"].AsString() != handlerId)
        {
            throw new AcceptanceException($"apply-master: expected masterId {handlerId} in the effect, got {masterEffect.ToJson()}");
        }

        var afterMasterAnimal = Animal(await HerdRow("guard-after-master", guardId).ConfigureAwait(false));
        if (afterMasterAnimal["masterId"].AsString() != handlerId)
        {
            throw new AcceptanceException($"guard-after-master: expected the master to be observable, got {afterMasterAnimal.ToJson()}");
        }

        report["master_dispatched"] = true;

        // Following: each flag is written on its own and the other is untouched.
        var draftedEffect = await Applied("apply-follow-drafted", "husbandry-follow-drafted", guardId, "FOLLOW_DRAFTED", new JsonObject { ["follow"] = true }).ConfigureAwait(false);
        if (draftedEffect["followDrafted"].AsBool() != true)
        {
            throw new AcceptanceException($"apply-follow-drafted: expected followDrafted in the effect, got {draftedEffect.ToJson()}");
        }

        var afterDraftedAnimal = Animal(await HerdRow("guard-after-follow-drafted", guardId).ConfigureAwait(false));
        if (afterDraftedAnimal["followDrafted"].AsBool() != true)
        {
            throw new AcceptanceException($"guard-after-follow-drafted: expected followDrafted to be observable, got {afterDraftedAnimal.ToJson()}");
        }

        if (afterDraftedAnimal["followFieldwork"].AsBool() == true)
        {
            throw new AcceptanceException($"guard-after-follow-drafted: followFieldwork must be untouched, got {afterDraftedAnimal.ToJson()}");
        }

        var fieldworkEffect = await Applied("apply-follow-fieldwork", "husbandry-follow-fieldwork", guardId, "FOLLOW_FIELDWORK", new JsonObject { ["follow"] = true }).ConfigureAwait(false);
        if (fieldworkEffect["followFieldwork"].AsBool() != true)
        {
            throw new AcceptanceException($"apply-follow-fieldwork: expected followFieldwork in the effect, got {fieldworkEffect.ToJson()}");
        }

        var afterFieldworkAnimal = Animal(await HerdRow("guard-after-follow-fieldwork", guardId).ConfigureAwait(false));
        if (afterFieldworkAnimal["followDrafted"].AsBool() != true || afterFieldworkAnimal["followFieldwork"].AsBool() != true)
        {
            throw new AcceptanceException($"guard-after-follow-fieldwork: expected both follow flags set, got {afterFieldworkAnimal.ToJson()}");
        }

        report["following_dispatched"] = true;

        // Standing removals are reversible settings: cancel each, read native state back, and cancel again, which
        // applies as it stands.
        foreach (var (id, order, field) in new[] { (cowId, "CANCEL_SLAUGHTER", "slaughter"), (fatherId, "CANCEL_RELEASE", "release") })
        {
            foreach (var label in new[] { "cancel-" + field, "recancel-" + field })
            {
                var effect = await Applied(label, "husbandry-" + label, id, order, null).ConfigureAwait(false);
                if (effect[field + "Designated"].AsBool() is not false)
                {
                    throw new AcceptanceException($"{label}: cancellation effect {effect.ToJson()}");
                }
            }

            var animal = Animal(await HerdRow("after-cancel-" + field, id).ConfigureAwait(false));
            if (animal[field].AsBool() is not false)
            {
                throw new AcceptanceException($"cancellation native readback: {animal.ToJson()}");
            }

            report["cancel_" + field] = animal;
        }
    }
}
