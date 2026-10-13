using System.Globalization;
using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The mirror of the store in the save's governor state component (Go <c>governor_state.go</c>). The store stays
/// authoritative; each value is ASCII JSON (non-ASCII escaped as <c>\uXXXX</c>) with a top-level
/// <c>schemaVersion</c>, and the shapes are those of the Go journal so saves written by either side load:
/// <list type="bullet">
/// <item><c>standard/&lt;id&gt;</c>: <c>{schemaVersion, standard, revision}</c>, one per unretired standard.</item>
/// <item><c>family/layout_plan</c>, <c>family/defense_layout</c>, <c>family/production_ladder</c>,
/// <c>family/soldier_squad</c>, <c>family/combat_restoration</c>: <c>{schemaVersion, scope?, record}</c>.</item>
/// </list>
/// The layout plan is a world row, so its blob carries the scope (colony, map, tick) of the newest row written.
/// </summary>
public sealed partial class Store
{
    /// <summary>Versions every governor state blob; a breaking change bumps it.</summary>
    public const int GovernorStateSchemaVersion = 6;

    private static readonly string[] _familyTables = ["colony_layout_plans", "defense_layout", "production_ladder", "soldier_squad", "combat_restoration", "colony_extent_events"];

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
                var ids = tx.Query("SELECT id FROM standards WHERE retired=0 ORDER BY id", [], r => new StandardId(r.GetString(0)));
                foreach (var id in ids)
                {
                    var g = LoadStandard(tx, id);
                    output[GovernorStandardKeyPrefix + id.Value] = Blob(w =>
                    {
                        w.Put("standard", g.Standard, Codecs.Default);
                        w.WriteNumber("revision", g.Revision);
                    });
                }
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
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.I32("schemaVersion") != GovernorStateSchemaVersion)
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

    /// <summary>
    /// Replaces the family record tables with the save's <c>family/*</c> blobs on a world change: the tables are
    /// session caches of the save. A missing blob leaves its table empty.
    /// </summary>
    public void RebuildFamilies(IReadOnlyDictionary<string, string> saved, CancellationToken cancellationToken)
    {
        var blobs = new Dictionary<string, FamilyBlobIn>(StringComparer.Ordinal);
        try
        {
            foreach (var key in new[] { GovernorLayoutPlanKey, GovernorDefenseLayoutKey, GovernorProductionLadderKey, GovernorSoldierSquadKey, GovernorCombatRestorationKey })
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
            Run(
                tx =>
                {
                    foreach (var table in _familyTables)
                    {
                        _ = tx.Execute("DELETE FROM " + table);
                    }
                    if (blobs.TryGetValue(GovernorLayoutPlanKey, out var layout))
                    {
                        var plan = StoreJson.Deserialize<Policy.LayoutPlan>(layout.Record.GetRawText());
                        DomainException.Require(plan.Valid(), "invalid layout plan");
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
                        r.Validate();
                        PutSingleton(tx, "production_ladder", r, MaxProductionLadderBytes);
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

    public Task RebuildFamiliesAsync(IReadOnlyDictionary<string, string> saved, CancellationToken cancellationToken) => Task.Run(() => RebuildFamilies(saved, cancellationToken), cancellationToken);

    /// <summary>
    /// Replaces the store's standards with the save's <c>standard/*</c> blobs: the save wins. Standards absent from
    /// the save are deleted; every standard method row is deleted and its plan retired after one orphans pass sees
    /// them all, so methods start empty and are re-planned. A save without standard blobs leaves no standards. An
    /// incident's method rows are not touched.
    /// </summary>
    /// <param name="orphans">Sees the plans the rebuild is about to retire, before they are retired; null skips it.</param>
    public void RebuildStandards(IReadOnlyDictionary<string, string> saved, Action<IReadOnlyList<PlanState>, CancellationToken>? orphans, CancellationToken cancellationToken)
    {
        var goals = new Dictionary<StandardId, (Standard Standard, ulong Revision)>();
        foreach (var (key, raw) in saved)
        {
            if (!key.StartsWith(GovernorStandardKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            using var document = ParseBlob(key, raw);
            var standard = document.RootElement.Get<Standard>("standard", Codecs.Default);
            try
            {
                standard.Validate();
            }
            catch (DomainException e)
            {
                throw new StoreCorruptException($"{key}: {e.Message}");
            }
            if (standard.Id.Value != key[GovernorStandardKeyPrefix.Length..])
            {
                throw new StoreCorruptException($"{key}: standard identity mismatch");
            }
            goals[standard.Id] = (standard, document.RootElement.U64("revision"));
        }
        if (orphans is not null)
        {
            var plans = Run(
                tx => (IReadOnlyList<PlanState>)tx.Query("SELECT plan_id FROM plan_owner WHERE kind='standard' ORDER BY plan_id", [], r => new PlanId(r.GetString(0))).Select(id => LoadIn(tx, id)).ToList(),
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
                _ = tx.Execute("UPDATE plans SET retired=1 WHERE retired=0 AND id IN (SELECT plan_id FROM plan_owner WHERE kind='standard')");
                _ = tx.Execute("DELETE FROM standard_methods");
                _ = tx.Execute("DELETE FROM plan_owner WHERE kind='standard'");
                foreach (var id in tx.Query("SELECT id FROM standards ORDER BY id", [], r => new StandardId(r.GetString(0))))
                {
                    if (!goals.ContainsKey(id))
                    {
                        _ = tx.Execute("DELETE FROM standards WHERE id=$id", ("$id", id));
                    }
                }
                foreach (var (id, goal) in goals)
                {
                    _ = tx.Execute(
                        "INSERT INTO standards(id,revision,payload,retired) VALUES($id,$revision,$payload,0) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload,retired=0",
                        ("$id", id),
                        ("$revision", goal.Revision.ToString(CultureInfo.InvariantCulture)),
                        ("$payload", StoreJson.Serialize(goal.Standard))
                    );
                }
            },
            cancellationToken
        );
        Floors.Reset();
    }

    public Task RebuildStandardsAsync(IReadOnlyDictionary<string, string> saved, Action<IReadOnlyList<PlanState>, CancellationToken>? orphans, CancellationToken cancellationToken) =>
        Task.Run(() => RebuildStandards(saved, orphans, cancellationToken), cancellationToken);

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

internal static partial class StoreSchema
{
    internal static readonly SchemaPart StandardsPart = new(
        """
        CREATE TABLE standards(id TEXT PRIMARY KEY, revision TEXT NOT NULL, payload TEXT NOT NULL, retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN (0,1))) STRICT;
        CREATE INDEX active_standards ON standards(id) WHERE retired=0;
        CREATE TABLE incidents(id TEXT PRIMARY KEY, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, kind TEXT NOT NULL, subject TEXT NOT NULL, started_tick INTEGER NOT NULL, ended_tick INTEGER, payload TEXT NOT NULL) STRICT;
        CREATE UNIQUE INDEX open_incidents ON incidents(colony,load_token,map_id,kind,subject) WHERE ended_tick IS NULL;
        CREATE TABLE plan_owner(plan_id TEXT PRIMARY KEY REFERENCES plans(id), kind TEXT NOT NULL CHECK(kind IN ('standard','incident')), UNIQUE(plan_id,kind)) STRICT;
        CREATE TABLE standard_methods(standard_id TEXT NOT NULL REFERENCES standards(id), episode TEXT NOT NULL, method_id TEXT NOT NULL, plan_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL DEFAULT 'standard' CHECK(kind='standard'), priority INTEGER NOT NULL, reason TEXT, PRIMARY KEY(standard_id,episode,method_id), FOREIGN KEY(plan_id,kind) REFERENCES plan_owner(plan_id,kind)) STRICT;
        CREATE TABLE incident_methods(incident_id TEXT NOT NULL REFERENCES incidents(id), method_id TEXT NOT NULL, plan_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL DEFAULT 'incident' CHECK(kind='incident'), priority INTEGER NOT NULL, reason TEXT, PRIMARY KEY(incident_id,method_id), FOREIGN KEY(plan_id,kind) REFERENCES plan_owner(plan_id,kind)) STRICT;
        CREATE VIEW plan_methods AS SELECT plan_id,kind,standard_id AS owner_id,episode,method_id,priority,reason FROM standard_methods UNION ALL SELECT plan_id,kind,incident_id,NULL,method_id,priority,reason FROM incident_methods;
        """,
        [
            "SELECT id,revision,payload,retired FROM standards LIMIT 0",
            "SELECT id,colony,load_token,map_id,kind,subject,started_tick,ended_tick,payload FROM incidents LIMIT 0",
            "SELECT plan_id,kind FROM plan_owner LIMIT 0",
            "SELECT standard_id,episode,method_id,plan_id,kind,priority,reason FROM standard_methods LIMIT 0",
            "SELECT incident_id,method_id,plan_id,kind,priority,reason FROM incident_methods LIMIT 0",
            "SELECT plan_id,kind,owner_id,episode,method_id,priority,reason FROM plan_methods LIMIT 0",
        ]
    );
}
