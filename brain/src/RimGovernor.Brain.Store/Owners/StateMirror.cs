using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The mirror of the store in the save's governor state component (Go <c>governor_state.go</c>). The store stays
/// authoritative; each value is ASCII JSON (non-ASCII escaped as <c>\uXXXX</c>) with a top-level
/// <c>schemaVersion</c>, and the shapes are those of the Go journal so saves written by either side load:
/// <list type="bullet">
/// <item>A version 6 save's <c>standard/&lt;id&gt;</c> blobs are ignored on load (#3265): Standards are gone, and
/// their methods were re-planned on every load anyway (<see cref="RebuildOwners"/>).</item>
/// <item><c>family/layout_plan</c>, <c>family/defense_layout</c>, <c>family/production_ladder</c>,
/// <c>family/soldier_squad</c>, <c>family/combat_restoration</c>, <c>family/admin/creep_joiners</c>,
/// <c>family/economy/trade_missions</c>, <c>family/ideology/reform</c>: <c>{schemaVersion, scope?, record}</c>.
/// An older save's pending reform lives in its ImproveIdeoligion <c>standard/*</c> blob's <c>Record</c> instead; it
/// is read once on load into <c>family/ideology/reform</c> when that blob is absent (#3260).</item>
/// </list>
/// Version 7 (#3265) only stops writing <c>standard/*</c> blobs; every family blob's shape is unchanged, so a version 6
/// save loads.
/// <list type="bullet">
/// </list>
/// The layout plan is a world row, so its blob carries the scope (colony, map, tick) of the newest row written.
/// </summary>
public sealed partial class Store
{
    /// <summary>Versions every governor state blob; a breaking change bumps it.</summary>
    public const int GovernorStateSchemaVersion = 7;

    /// <summary>The oldest blob version load still reads: version 6 differs only by its ignored <c>standard/*</c> blobs.</summary>
    private const int MinGovernorStateSchemaVersion = 6;

    private static readonly string[] _familyTables = ["colony_layout_plans", "defense_layout", "production_ladder", "soldier_squad", "combat_restoration", "admin_creep_joiners", "economy_trade_missions", "colony_extent_events", "ideology_reform"];

    /// <summary>The retired Concern whose Standard carried the pending reform before the ideology family (#3260).</summary>
    private static readonly ConcernId LegacyReformKind = new("ImproveIdeoligion");

    private static string Blob(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", GovernorStateSchemaVersion);
            body(w);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string FamilyBlob(string recordJson, (string Colony, int Map, long Tick)? scope) =>
        Blob(w =>
        {
            if (scope is { } s)
            {
                w.WriteStartObject("scope");
                w.WriteString("colony", s.Colony);
                w.WriteNumber("map", s.Map);
                w.WriteNumber("tick", s.Tick);
                w.WriteEndObject();
            }
            w.WritePropertyName("record");
            w.WriteRawValue(recordJson);
        });

    /// <summary>Renders the store as governor state blobs by key.</summary>
    public IReadOnlyDictionary<string, string> GovernorStateBlobs(CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var output = new Dictionary<string, string>(StringComparer.Ordinal);
                var (plan, found) = tx.QueryFirst(
                    "SELECT colony,map_id,tick,plan FROM colony_layout_plans ORDER BY rowid DESC LIMIT 1",
                    [],
                    r => (Colony: r.GetString(0), Map: r.GetInt32(1), Tick: r.GetInt64(2), Plan: r.GetString(3))
                );
                if (found)
                {
                    output[GovernorLayoutPlanKey] = FamilyBlob(plan.Plan, (plan.Colony, plan.Map, plan.Tick));
                }
                if (LoadDefenseLayoutIn(tx) is { } defense)
                {
                    output[GovernorDefenseLayoutKey] = FamilyBlob(StoreJson.Serialize(defense.Stored()), null);
                }
                if (LoadProductionLadderIn(tx) is { } ladder)
                {
                    output[GovernorProductionLadderKey] = FamilyBlob(StoreJson.Serialize(ladder), null);
                }
                if (LoadSoldierSquadIn(tx) is { } squad)
                {
                    output[GovernorSoldierSquadKey] = FamilyBlob(StoreJson.Serialize(squad), null);
                }
                if (LoadCombatRestorationIn(tx) is { } restoration)
                {
                    output[GovernorCombatRestorationKey] = FamilyBlob(StoreJson.Serialize(restoration), null);
                }
                if (LoadIdeologyReformIn(tx) is { } reform)
                {
                    output[GovernorIdeologyReformKey] = FamilyBlob(StoreJson.Serialize(reform), null);
                }
                if (LoadCreepJoinersIn(tx) is { } creepJoiners)
                {
                    output[GovernorCreepJoinersKey] = FamilyBlob(StoreJson.Serialize(creepJoiners), null);
                }
                if (LoadTradeMissionsIn(tx) is { } missions)
                {
                    output[GovernorTradeMissionsKey] = FamilyBlob(StoreJson.Serialize(missions), null);
                }
                return (IReadOnlyDictionary<string, string>)output;
            },
            cancellationToken
        );

    public Task<IReadOnlyDictionary<string, string>> GovernorStateBlobsAsync(CancellationToken cancellationToken) => Task.Run(() => GovernorStateBlobs(cancellationToken), cancellationToken);

    private sealed record FamilyBlobIn(JsonDocument Document, (string Colony, int Map, long Tick)? Scope)
    {
        public JsonElement Record => Document.RootElement.GetProperty("record");
    }

    private static JsonDocument ParseBlob(string key, string raw)
    {
        try
        {
            var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.I32("schemaVersion") is < MinGovernorStateSchemaVersion or > GovernorStateSchemaVersion)
            {
                document.Dispose();
                throw new StoreCorruptException($"{key}: schema version");
            }
            return document;
        }
        catch (JsonException e)
        {
            throw new StoreCorruptException($"{key}: {e.Message}");
        }
    }

    /// <summary>The saved layout plan, or null when it does not decode or is not <c>Valid()</c>.</summary>
    internal static Policy.LayoutPlan? SavedLayoutPlan(JsonElement record)
    {
        try
        {
            var plan = StoreJson.Deserialize<Policy.LayoutPlan>(record.GetRawText());
            return plan.Valid() ? plan : null;
        }
        catch (StoreCorruptException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces the family record tables with the save's <c>family/*</c> blobs on a world change: the tables are
    /// session caches of the save. A missing blob leaves its table empty.
    /// </summary>
    public void RebuildFamilies(IReadOnlyDictionary<string, string> saved, CancellationToken cancellationToken)
    {
        var blobs = new Dictionary<string, FamilyBlobIn>(StringComparer.Ordinal);
        try
        {
            foreach (var key in new[] { GovernorLayoutPlanKey, GovernorDefenseLayoutKey, GovernorProductionLadderKey, GovernorSoldierSquadKey, GovernorCombatRestorationKey, GovernorCreepJoinersKey, GovernorTradeMissionsKey, GovernorIdeologyReformKey })
            {
                if (!saved.TryGetValue(key, out var raw))
                {
                    continue;
                }
                var document = ParseBlob(key, raw);
                (string, int, long)? scope = null;
                if (document.RootElement.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.Object)
                {
                    scope = (s.Str("colony"), s.I32("map"), s.I64("tick"));
                }
                if (key == GovernorLayoutPlanKey && scope is null)
                {
                    document.Dispose();
                    throw new StoreCorruptException($"{key}: missing scope");
                }
                blobs[key] = new FamilyBlobIn(document, scope);
            }
            var legacyReform = blobs.ContainsKey(GovernorIdeologyReformKey) ? null : LegacyReform(saved);
            Run(
                tx =>
                {
                    foreach (var table in _familyTables)
                    {
                        _ = tx.Execute("DELETE FROM " + table);
                    }
                    // A saved plan that no longer decodes (a role or field since deleted) or is not Valid() restores as
                    // no plan, as the table's own read does, so the next survey derives a fresh one.
                    if (blobs.TryGetValue(GovernorLayoutPlanKey, out var layout) && SavedLayoutPlan(layout.Record) is { } plan)
                    {
                        var scope = layout.Scope!.Value;
                        _ = tx.Execute(
                            "INSERT INTO colony_layout_plans(colony,map_id,tick,plan) VALUES($colony,$map,$tick,$plan)",
                            ("$colony", scope.Colony),
                            ("$map", scope.Map),
                            ("$tick", scope.Tick),
                            ("$plan", StoreJson.Serialize(plan))
                        );
                    }
                    if (blobs.TryGetValue(GovernorDefenseLayoutKey, out var defense))
                    {
                        var r = Codecs.Default.Read<DefenseLayoutRecord>(defense.Record);
                        r.Validate();
                        PutSingleton(tx, "defense_layout", r.Stored(), MaxDefenseLayoutBytes);
                    }
                    if (blobs.TryGetValue(GovernorProductionLadderKey, out var ladder))
                    {
                        var r = Codecs.Default.Read<ProductionLadderRecord>(ladder.Record);
                        r = r with { Research = [.. r.Research.Order(StringComparer.Ordinal)] };
                        // A ladder its retired owner recorded (the resource and equipment Concerns their Needs replaced in #3303 and #3259) is dropped:
                        // the workshop re-records its rung under the owner that now runs it.
                        if (ProductionLadderRecord.LadderOwner(r.Concern))
                        {
                            r.Validate();
                            PutSingleton(tx, "production_ladder", r, MaxProductionLadderBytes);
                        }
                    }
                    if (blobs.TryGetValue(GovernorSoldierSquadKey, out var squad))
                    {
                        var r = Codecs.Default.Read<SoldierSquadRecord>(squad.Record);
                        r.Validate();
                        PutSingleton(tx, "soldier_squad", r, MaxSoldierSquadBytes);
                    }
                    if (blobs.TryGetValue(GovernorCombatRestorationKey, out var restoration))
                    {
                        var r = Codecs.Default.Read<CombatRestoration>(restoration.Record);
                        r.Validate();
                        PutSingleton(tx, "combat_restoration", r, MaxCombatRestorationBytes);
                    }
                    if (blobs.TryGetValue(GovernorIdeologyReformKey, out var reform))
                    {
                        var r = Codecs.Default.Read<IdeologyReformRecord>(reform.Record);
                        r.Validate();
                        PutSingleton(tx, "ideology_reform", r, MaxIdeologyReformBytes);
                    }
                    else if (legacyReform is not null)
                    {
                        PutSingleton(tx, "ideology_reform", legacyReform, MaxIdeologyReformBytes);
                    }
                    if (blobs.TryGetValue(GovernorCreepJoinersKey, out var creepJoiners))
                    {
                        PutCreepJoinersIn(tx, Codecs.Default.Read<CreepJoinersRecord>(creepJoiners.Record));
                    }
                    if (blobs.TryGetValue(GovernorTradeMissionsKey, out var missions))
                    {
                        PutTradeMissionsIn(tx, Codecs.Default.Read<TradeMissionsRecord>(missions.Record));
                    }
                },
                cancellationToken
            );
        }
        finally
        {
            foreach (var blob in blobs.Values)
            {
                blob.Document.Dispose();
            }
        }
    }

    /// <summary>
    /// The pending reform an older save kept in its ImproveIdeoligion Standard's <c>Record</c>
    /// (<c>{"Ideo","Expected","Target","Count"}</c>), scoped to that Standard's world; null when the save has none or
    /// it does not decode, and the ideology family re-derives.
    /// </summary>
    private static IdeologyReformRecord? LegacyReform(IReadOnlyDictionary<string, string> saved)
    {
        foreach (var (key, raw) in saved.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!key.StartsWith(LegacyStandardKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            using var document = ParseBlob(key, raw);
            // The retired Standard's own JSON (Go field names): only Kind, Record and Snapshot are read.
            if (!document.RootElement.TryGetProperty("standard", out var standard) || standard.ValueKind != JsonValueKind.Object
                || standard.StrOr("Kind") != LegacyReformKind.Value || standard.StrOr("Record") is not { Length: > 0 } text)
            {
                continue;
            }
            try
            {
                var snapshot = standard.Get<GenerationSnapshot>("Snapshot", Codecs.Default);
                using var record = JsonDocument.Parse(text);
                var root = record.RootElement;
                IdeoligionDesign Design(string name) => StoreJson.Deserialize<IdeoligionDesign>(root.GetProperty(name).GetRawText());
                var reform = new IdeoligionReform(root.GetProperty("Ideo").GetString() ?? "", Design("Expected"), Design("Target"), root.GetProperty("Count").GetInt32());
                var r = new IdeologyReformRecord { World = new World(snapshot.Colony, snapshot.Load, snapshot.Map), Reform = reform };
                r.Validate();
                return r;
            }
            catch (Exception e) when (e is JsonException or DomainException or KeyNotFoundException or InvalidOperationException or StoreCorruptException)
            {
                return null;
            }
        }
        return null;
    }

    public Task RebuildFamiliesAsync(IReadOnlyDictionary<string, string> saved, CancellationToken cancellationToken) => Task.Run(() => RebuildFamilies(saved, cancellationToken), cancellationToken);

    /// <summary>
    /// Resets the store's owners on load: a Need's open plans (#3250) are retired and its rows kept as history, since
    /// Needs are re-derived after the load and their rows are keyed by the load token, so nothing continues across it.
    /// Every open combat fight closes with them; the saved restoration is kept. An older save's <c>standard/*</c>
    /// blobs are ignored (#3265).
    /// </summary>
    /// <param name="orphans">Sees the plans the rebuild is about to retire, before they are retired; null skips it.</param>
    public void RebuildOwners(Action<IReadOnlyList<PlanState>, CancellationToken>? orphans, CancellationToken cancellationToken)
    {
        if (orphans is not null)
        {
            var plans = Run(
                tx => (IReadOnlyList<PlanState>)tx.Query("SELECT plan_id FROM plan_owner WHERE plan_id IN (SELECT id FROM plans WHERE retired=0) ORDER BY plan_id", [], r => new PlanId(r.GetString(0))).Select(id => LoadIn(tx, id)).ToList(),
                cancellationToken
            );
            if (plans.Count > 0)
            {
                orphans(plans, cancellationToken);
            }
        }
        Run(
            tx =>
            {
                _ = tx.Execute("UPDATE plans SET retired=1 WHERE retired=0 AND id IN (SELECT plan_id FROM plan_owner)");
                CloseOpenCombatFightsIn(tx);
            },
            cancellationToken
        );
        Floors.Reset();
    }

    public Task RebuildOwnersAsync(Action<IReadOnlyList<PlanState>, CancellationToken>? orphans, CancellationToken cancellationToken) =>
        Task.Run(() => RebuildOwners(orphans, cancellationToken), cancellationToken);

    /// <summary>
    /// Writes a snapshot of the whole store beside <paramref name="path"/> and renames it over <paramref name="path"/>,
    /// so a reader sees the previous snapshot or the new one and never a partial file. The copy is derived and
    /// read-only: nothing in the controller reads it back.
    /// </summary>
    public void SnapshotReplace(string path, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(temp + suffix);
        }
        try
        {
            Snapshot(temp, cancellationToken);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        File.Move(temp, path, overwrite: true);
    }

    public Task SnapshotReplaceAsync(string path, CancellationToken cancellationToken) => Task.Run(() => SnapshotReplace(path, cancellationToken), cancellationToken);
}

internal static class StateMirrorSchema
{
    internal static readonly SchemaPart OwnersPart = new(
        """
        CREATE TABLE plan_owner(plan_id TEXT PRIMARY KEY REFERENCES plans(id), kind TEXT NOT NULL CHECK(kind='need'), UNIQUE(plan_id,kind)) STRICT;
        CREATE TABLE need_methods(colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, need_id TEXT NOT NULL, method_id TEXT NOT NULL, attempt INTEGER NOT NULL CHECK(attempt>0), plan_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL DEFAULT 'need' CHECK(kind='need'), reason TEXT, PRIMARY KEY(colony,load_token,map_id,need_id,method_id,attempt), FOREIGN KEY(plan_id,kind) REFERENCES plan_owner(plan_id,kind)) STRICT;
        """,
        [
            "SELECT plan_id,kind FROM plan_owner LIMIT 0",
            "SELECT colony,load_token,map_id,need_id,method_id,attempt,plan_id,kind,reason FROM need_methods LIMIT 0",
        ]
    );
}
