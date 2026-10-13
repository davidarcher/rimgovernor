namespace RimGovernor.Brain.Store;

/// <summary>One concern's tables: the DDL that creates them and the probes that prove an existing file has them.</summary>
/// <param name="Ddl">Semicolon-separated <c>CREATE</c> statements.</param>
/// <param name="Checks">Queries that fail when a table or column is missing (<c>SELECT cols FROM t LIMIT 0</c>).</param>
internal sealed record SchemaPart(string Ddl, IReadOnlyList<string> Checks);

/// <summary>
/// The journal's schema (version <see cref="Store.SchemaVersion"/>). Domain payloads are JSON text written by the
/// hand-written codecs, so a row keeps only the columns something queries; the per-kind column checks Go kept in SQL
/// live in the domain constructors a payload is rebuilt through. The journal is disposable on upgrade: a file of
/// another version is moved aside and replaced, so there are no migrations.
/// </summary>
internal static partial class StoreSchema
{
    private const string CoreDdl = """
        CREATE TABLE metadata(singleton INTEGER PRIMARY KEY CHECK(singleton=1), controller_session_id TEXT NOT NULL) STRICT;
        CREATE TABLE plans(id TEXT PRIMARY KEY, revision TEXT NOT NULL, method_id TEXT, superseded_by TEXT REFERENCES plans(id), retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN (0,1))) STRICT;
        CREATE INDEX active_plans ON plans(id) WHERE retired=0;
        CREATE TABLE actions(id TEXT PRIMARY KEY, plan_id TEXT NOT NULL REFERENCES plans(id), ordinal INTEGER NOT NULL, kind TEXT NOT NULL, fight TEXT, body TEXT NOT NULL, UNIQUE(plan_id,ordinal)) STRICT;
        CREATE TABLE transitions(sequence INTEGER PRIMARY KEY, action_id TEXT NOT NULL REFERENCES actions(id), payload TEXT NOT NULL) STRICT;
        CREATE INDEX action_transitions ON transitions(action_id,sequence);
        CREATE TABLE action_dependencies(plan_id TEXT NOT NULL REFERENCES plans(id), action_id TEXT NOT NULL REFERENCES actions(id), requires_id TEXT NOT NULL REFERENCES actions(id), coupled INTEGER NOT NULL DEFAULT 0 CHECK(coupled IN (0,1)), PRIMARY KEY(plan_id,action_id,requires_id)) STRICT;
        CREATE TABLE admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload TEXT NOT NULL) STRICT;
        """;

    private static readonly string[] _coreChecks =
    [
        "SELECT singleton,controller_session_id FROM metadata LIMIT 0",
        "SELECT p.id,p.revision,p.method_id,p.superseded_by,p.retired,a.id,a.ordinal,a.kind,a.fight,a.body,t.sequence,t.payload FROM plans p LEFT JOIN actions a ON a.plan_id=p.id LEFT JOIN transitions t ON t.action_id=a.id LIMIT 0",
        "SELECT plan_id,action_id,requires_id,coupled FROM action_dependencies LIMIT 0",
        "SELECT action_id,payload FROM admissions LIMIT 0",
    ];

    private static SchemaPart[] Parts() => [new SchemaPart(CoreDdl, _coreChecks), .. MorePartsOf()];

    /// <summary>Creates every table of a new file.</summary>
    public static void Create(StoreTransaction tx)
    {
        foreach (var part in Parts())
        {
            _ = tx.Execute(part.Ddl);
        }
    }

    /// <summary>Proves an existing file of the right version has every table and column the store reads.</summary>
    public static void Check(StoreTransaction tx)
    {
        foreach (var part in Parts())
        {
            foreach (var check in part.Checks)
            {
                _ = tx.Execute(check);
            }
        }
    }
}
