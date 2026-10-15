using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The Loud <c>combat/*</c> acceptance cases (Go <c>cases/combat</c>, the former combataccept): a colonist drafted by a
/// DraftIntent attacks through one combat_orders intent, native picking melee or ranged from its weapon; the same key
/// replays the receipt; the scenario clock's WATCH_MODE_COMBAT policy advances the clock a step at a time until the target is down, and
/// the undraft intent releases the attacker. No damage or completion injection. <see cref="Register"/> replaces the Go
/// package's <c>init()</c>.
/// </summary>
public static class CombatCases
{
    private const int CombatAdvances = 20;
    private const int TicksPerAdvance = 240;
    private const int CombatTickBudget = CombatAdvances * TicksPerAdvance;

    // Each advance holds the clock after the tick the harness polled past its target; the game runs a few ticks in between.
    private const int OvershootSlack = 120;

    /// <summary>
    /// Why every combat case keeps the storyteller: the fixture spawns hostile animals and the WATCH_MODE_COMBAT clock
    /// must see them as the game reports hostility, which the quiet op's storyteller swap would mask.
    /// </summary>
    private const string LoudReason = "the attack target is a fixture-spawned hostile animal and the combat watch policy stops on the game's own hostility and health reporting";

    /// <summary>Registers every <c>combat/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var (name, ranged, explosive) in new[]
        {
            ("combat/melee", false, false),
            ("combat/ranged", true, false),
            ("combat/explosive", true, true),
        })
        {
            registry.Register(new AcceptanceCase
            {
                Name = name,
                Scope = "Draft intent, one combat_orders attack (melee or ranged by weapon), key replay, "
                    + "target downed or dead within bounded shared clock waits, undraft intent. No damage or completion injection.",
                Start = new LabStart(),
                Quiet = QuietMode.Loud,
                Reason = LoudReason,
                Budget = TimeSpan.FromMinutes(12),
                Crew = new Crew { Size = 3 },
                Run = (s, cancellationToken) => RunAsync(s, ranged, explosive, cancellationToken),
            });
        }
    }

    private static async Task RunAsync(ISession s, bool ranged, bool explosive, CancellationToken cancellationToken)
    {
        var report = s.Report;
        report["ranged"] = ranged;
        report["explosive"] = explosive;
        report["combat_tick_budget"] = CombatTickBudget;
        var jobDef = ranged ? "AttackStatic" : "AttackMelee";
        var h = s.Harness;
        var names = s.Names;
        var identity = s.Identity;
        var identityReply = await h.WireAsync("identity-paused", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var initial = Harness.Outcome(identityReply, "loaded").Value;
        if (initial["paused"].AsBool() != true)
        {
            throw new AcceptanceException("fresh debug game did not start paused");
        }

        var initialContext = initial["context"].AsMap();

        Task<JsonObject> Query(string label, JsonObject filters) => h.WireAsync(
            label,
            "observations_list_pawns",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["filter"] = filters },
            cancellationToken);

        async Task<JsonObject> Read(string label, string pawnId)
        {
            var reply = await Query(label, new JsonObject { ["ids"] = new JsonArray(pawnId) }).ConfigureAwait(false);
            return Draft.PawnRow(reply, identity, pawnId);
        }

        var healthyReply = await Query("healthy-colonists", new JsonObject { ["colonist"] = true }).ConfigureAwait(false);
        var healthyRows = ObservedRows(healthyReply, identity);
        var people = HealthyCandidates(healthyRows, ranged);
        if (people.Count == 0)
        {
            throw new AcceptanceException("no healthy observed violence-capable colonist");
        }

        var actorId = people[0]["pawn"].AsMap()?["id"].AsString() ?? "";

        if (ranged)
        {
            var gearOp = "ranged-equipment";
            var wantGear = "Gun_AssaultRifle";
            if (explosive)
            {
                gearOp = "explosive-equipment";
                wantGear = "Weapon_GrenadeFrag";
            }

            var gear = await h.CallAsync(gearOp, "test/b04f_setup", new JsonObject { ["op"] = gearOp, ["pawn"] = actorId }, cancellationToken).ConfigureAwait(false);
            if (gear["success"].AsBool() != true)
            {
                throw new AcceptanceException($"{gearOp} fixture refused");
            }

            if (gear["completedWorkInjected"].AsBool() == true)
            {
                throw new AcceptanceException($"{gearOp} fixture injected completed work");
            }

            var weapons = gear["weapons"].AsSlice();
            if (weapons.Count != 1)
            {
                throw new AcceptanceException($"{gearOp} fixture did not equip exactly one weapon: {gear["weapons"].ToJson()}");
            }

            var weaponId = weapons[0].AsString();
            var equipped = await Read("equipped-attacker", actorId).ConfigureAwait(false);
            var equipment = equipped["equipment"].AsMap();
            if (equipment?["primaryId"].AsString() != weaponId)
            {
                throw new AcceptanceException("equipped attacker's primaryId does not match the fixture weapon");
            }

            var found = false;
            foreach (var raw in equipment["equipped"].AsSlice())
            {
                var thing = raw.AsMap()?["thing"].AsMap();
                if (thing?["id"].AsString() == weaponId && thing["defName"].AsString() == wantGear)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new AcceptanceException($"equipped attacker's equipment does not include {weaponId} ({wantGear})");
            }
        }

        var setupOp = explosive ? "explosive-opponents" : ranged ? "ranged-opponents" : "opponents";
        var setup = await h.CallAsync("opponents", "test/b04f_setup", new JsonObject { ["op"] = setupOp, ["pawn"] = actorId }, cancellationToken).ConfigureAwait(false);
        if (setup["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{setupOp} fixture refused");
        }

        if (setup["completedWorkInjected"].AsBool() == true)
        {
            throw new AcceptanceException($"{setupOp} fixture injected completed work");
        }

        var targets = StringSlice(setup["opponents"]);
        if (targets.Count != 2 || targets.Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new AcceptanceException($"expected exactly 2 distinct opponents, got {setup["opponents"].ToJson()}");
        }

        var opponentsReply = await Query("observed-opponents", new JsonObject { ["ids"] = AnySlice(targets) }).ConfigureAwait(false);
        var targetsBefore = ObservedRows(opponentsReply, identity);
        if (!SameIdSet(targetsBefore, targets))
        {
            throw new AcceptanceException("observed opponents do not match the setup fixture's targets");
        }

        var wantDefName = ranged ? "Tortoise" : "Hare";
        foreach (var r in targetsBefore)
        {
            if (r["pawn"].AsMap()?["defName"].AsString() != wantDefName
                || r["animal"].AsBool() != true
                || r["hostile"].AsBool() != true
                || r["dead"].AsBool() == true
                || r["downed"].AsBool() == true)
            {
                throw new AcceptanceException($"opponent does not match expected fixture shape: {r.ToJson()}");
            }
        }

        var grant = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        // Drafts are plan-owned intents: draft, then one combat_orders attack; native picks melee or ranged from the
        // attacker's weapon.
        var draftJob = await ApplyIntents.ApplyDraftAsync(h, "draft", identity, "combat-draft", actorId, true, cancellationToken).ConfigureAwait(false);
        if (draftJob["issued"].AsBool() != true)
        {
            throw new AcceptanceException($"draft: an undrafted attacker was not drafted: {draftJob.ToJson()}");
        }

        var actor = await Read("drafted-attacker", actorId).ConfigureAwait(false);
        if (actor["drafted"].AsBool() != true)
        {
            throw new AcceptanceException("drafted-attacker: census shows the attacker undrafted");
        }

        var orders = new JsonArray(new JsonObject
        {
            ["pawn"] = new JsonObject { ["entityId"] = actorId },
            ["attack"] = new JsonObject { ["entityId"] = targets[0] },
        });
        var results = await ApplyIntents.ApplyCombatOrdersAsync(h, "attack", identity, "combat-attack", orders, cancellationToken).ConfigureAwait(false);
        try
        {
            CheckAttackResult(results, actorId, jobDef);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"attack: {e.Message}", e);
        }

        var attacking = await Read("attack-job", actorId).ConfigureAwait(false);
        var attackingJob = attacking["job"].AsMap();
        if (attackingJob?["defName"].AsString() != jobDef)
        {
            throw new AcceptanceException($"attack-job: row job {attackingJob?["defName"].ToJson() ?? "null"} is not {jobDef}");
        }

        // The same key replays the committed receipt without a second order.
        var replay = await ApplyIntents.ApplyCombatOrdersAsync(h, "attack-replay", identity, "combat-attack", orders, cancellationToken).ConfigureAwait(false);
        if (!Dyn.DeepEqual(new JsonArray([.. replay.Select(r => (JsonNode?)r.DeepClone())]), new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())])))
        {
            throw new AcceptanceException($"attack-replay returned different results: {new JsonArray([.. replay.Select(r => (JsonNode?)r.DeepClone())]).ToJson()}");
        }

        var replayRow = await Read("replay-unchanged", actorId).ConfigureAwait(false);
        if (!Dyn.DeepEqual(replayRow["job"], attacking["job"]))
        {
            throw new AcceptanceException("replay-unchanged: job changed unexpectedly");
        }

        var supervisor = new ScenarioClock(h.WireFunc(), identity, report)
        {
            Grant = grant,
            CombatTargets = targets,
        };
        var rt = new ScenarioRuntime(h.CallAsync, supervisor, report, names) { CombatTargets = targets };

        var advance = 0;
        var ended = false;
        for (; advance < CombatAdvances && !ended; advance++)
        {
            await supervisor.RenewAuthorityAsync(cancellationToken).ConfigureAwait(false);
            _ = await AdvanceGame.RunAsync(
                rt,
                TicksPerAdvance,
                [AdvanceOptions.WithTimeout(TimeSpan.FromSeconds(180)), AdvanceOptions.WithCombatTargets([.. targets])],
                cancellationToken).ConfigureAwait(false);
            var targetStateReply = await Query(
                string.Create(CultureInfo.InvariantCulture, $"target-state-{advance}"),
                new JsonObject { ["ids"] = new JsonArray(targets[0]), ["includeDead"] = true }).ConfigureAwait(false);
            var victims = ObservedRows(targetStateReply, identity);
            if (victims.Count != 1)
            {
                throw new AcceptanceException($"exact native target state unavailable: {new JsonArray([.. victims.Select(v => (JsonNode?)v.DeepClone())]).ToJson()}");
            }

            report["last_target"] = victims[0].DeepClone();
            ended = victims[0]["dead"].AsBool() == true || victims[0]["downed"].AsBool() == true;
        }

        report["combat_advances_used"] = advance;
        if (!ended)
        {
            throw new AcceptanceException($"combat budget exhausted after {CombatTickBudget} ticks with the target standing");
        }

        // No plan needs the attacker now: the undraft intent releases it.
        var undraftJob = await ApplyIntents.ApplyDraftAsync(h, "undraft", identity, "combat-undraft", actorId, false, cancellationToken).ConfigureAwait(false);
        if (undraftJob["issued"].AsBool() != true)
        {
            throw new AcceptanceException($"undraft: a drafted attacker was not undrafted: {undraftJob.ToJson()}");
        }

        var after = await Read("undrafted-attacker", actorId).ConfigureAwait(false);
        if (after["drafted"].AsBool() == true)
        {
            throw new AcceptanceException("undrafted-attacker: census shows the attacker drafted");
        }

        var finalReply = await h.WireAsync("identity-after", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var final = Harness.Outcome(finalReply, "loaded").Value;
        if (final["paused"].AsBool() != true)
        {
            throw new AcceptanceException("game unexpectedly resumed");
        }

        var finalContext = final["context"].AsMap();
        if (!Dyn.DeepEqual(finalContext?["identity"], identity))
        {
            throw new AcceptanceException("identity changed during the run");
        }

        var ticks = finalContext!["tick"].AsNumber() - (initialContext?["tick"].AsNumber() ?? 0);
        if (ticks <= 0 || ticks > CombatTickBudget + (CombatAdvances * OvershootSlack))
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"unexpected tick delta: {ticks}"));
        }

        report["pawn_id"] = actorId;
        report["target_ids"] = AnySlice(targets);
        report["ticks"] = ticks;
    }

    /// <summary>Asserts the one attack order applied as <paramref name="jobDef"/>.</summary>
    private static void CheckAttackResult(IReadOnlyList<JsonObject> results, string actorId, string jobDef)
    {
        if (results.Count != 1)
        {
            throw new AcceptanceException($"expected one result: {new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]).ToJson()}");
        }

        var r = results[0];
        if (r["applied"].AsBool() != true || r["pawnId"].AsString() != actorId || r["jobDef"].AsString() != jobDef)
        {
            throw new AcceptanceException($"result applied/pawn/jobDef mismatch: {r.ToJson()}");
        }
    }

    /// <summary>Asserts an observations_list_pawns reply is a single complete, exact-match page with unique pawn ids and returns its rows.</summary>
    private static List<JsonObject> ObservedRows(JsonObject reply, JsonObject identity)
    {
        var observed = Harness.Outcome(reply, "observed").Value;
        var observedContext = observed["context"].AsMap();
        if (!Dyn.DeepEqual(observedContext?["identity"], identity))
        {
            throw new AcceptanceException("observed rows: context identity mismatch");
        }

        var rows = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in observed["pawns"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var id = row["pawn"].AsMap()?["id"].AsString() ?? "";
            if (!seen.Add(id))
            {
                throw new AcceptanceException($"observed rows: duplicate pawn id \"{id}\"");
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Filters rows to violence-capable, undrafted, healthy colonists (and shooting-capable when ranged).</summary>
    private static List<JsonObject> HealthyCandidates(List<JsonObject> rows, bool ranged)
    {
        if (rows.Count == 0)
        {
            throw new AcceptanceException("no rows provided");
        }

        foreach (var r in rows)
        {
            var fraction = r["health"].AsMap()?["summaryFraction"].AsNumber() ?? 0;
            if (r["dead"].AsBool() == true || r["downed"].AsBool() == true || fraction <= .5005)
            {
                throw new AcceptanceException($"healthy candidates: row failed health precondition: {r.ToJson()}");
            }
        }

        var result = new List<JsonObject>();
        foreach (var r in rows)
        {
            if (r["drafted"].AsBool() == true)
            {
                continue;
            }

            var biography = r["biography"].AsMap();
            var tags = StringSlice(biography?["disabledWorkTags"]);
            if (tags.Contains("Violent", StringComparer.Ordinal))
            {
                continue;
            }

            if (ranged && tags.Contains("Shooting", StringComparer.Ordinal))
            {
                continue;
            }

            if (biography?["issues"].AsSlice().Any(raw => raw.AsMap()?["field"].AsString() == "disabled_work_tags") == true)
            {
                continue;
            }

            result.Add(r);
        }

        return result;
    }

    private static bool SameIdSet(List<JsonObject> rows, List<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            _ = seen.Add(r["pawn"].AsMap()?["id"].AsString() ?? "");
        }

        return seen.Count == ids.Count && ids.All(seen.Contains);
    }

    private static List<string> StringSlice(JsonNode? value) => [.. value.AsSlice().Select(item => item.AsString())];

    private static JsonArray AnySlice(List<string> values) => [.. values.Select(v => (JsonNode?)JsonValue.Create(v))];
}
