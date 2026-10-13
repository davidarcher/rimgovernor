using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The pawn-health reads the medical cases share (Go: the unexported helpers of <c>cases/medical</c>:
/// <c>pawnHealth</c>, <c>partMissing</c>, <c>hediffAt</c>, <c>operationAt</c>, <c>conditionFixture</c>).
/// </summary>
internal static class MedicalKit
{
    /// <summary>The <c>observations_list_pawns</c> filter for colonists only.</summary>
    internal static JsonObject ColonistFilter() => new() { ["colonist"] = true, ["humanlike"] = true, ["animal"] = false };

    /// <summary>The filter for every humanlike pawn: prisoners and guests as well as colonists.</summary>
    internal static JsonObject HumanFilter() => new() { ["humanlike"] = true, ["animal"] = false };

    /// <summary>The <c>details</c> block of a pawn read: only the named sections.</summary>
    internal static JsonObject Details(bool health, bool biography = false, bool tend = false)
    {
        var details = new JsonObject
        {
            ["health"] = health,
            ["needs"] = false,
            ["equipment"] = false,
            ["biography"] = biography,
            ["settings"] = false,
            ["social"] = false,
            ["animals"] = false,
        };
        if (tend)
        {
            details["tend"] = true;
        }

        return details;
    }

    /// <summary><c>observations_list_pawns</c> under <paramref name="identity"/>; the <c>observed</c> body.</summary>
    internal static async Task<JsonObject> ListPawnsAsync(Harness h, string label, JsonObject identity, JsonObject filter, JsonObject details, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["filter"] = filter,
                ["details"] = details,
            },
            cancellationToken).ConfigureAwait(false);
        return Harness.Outcome(reply, "observed").Value;
    }

    /// <summary>One pawn's health detail from list_pawns (Go <c>pawnHealth</c>).</summary>
    internal static async Task<JsonObject> PawnHealthAsync(ISession s, string label, string pawn, CancellationToken cancellationToken)
    {
        var observed = await ListPawnsAsync(s.Harness, label, s.Identity, HumanFilter(), Details(health: true), cancellationToken).ConfigureAwait(false);
        foreach (var row in observed["pawns"].Rows())
        {
            if (row["pawn"].RefId() == pawn)
            {
                return row["health"].AsMap() ?? [];
            }
        }

        throw new AcceptanceException($"{label}: pawn {pawn} not in the pawn read");
    }

    /// <summary>Whether the health detail reports body part <paramref name="part"/> missing.</summary>
    internal static bool PartMissing(JsonObject health, int part) =>
        health["missingParts"].Rows().Any(m => m["partIndex"].Strict() is { } index && (int)index == part);

    /// <summary>Whether the health detail carries hediff <paramref name="def"/> on <paramref name="part"/>.</summary>
    internal static bool HediffAt(JsonObject health, string def, int part) =>
        health["hediffs"].Rows().Any(h => h["definition"].AsMap()?["defName"].AsString() == def && h["partIndex"].Strict() is { } index && (int)index == part);

    /// <summary>Whether the health detail offers operation <paramref name="recipe"/> of <paramref name="kind"/> on <paramref name="part"/>.</summary>
    internal static bool OperationAt(JsonObject health, string recipe, string kind, int part) =>
        health["operations"].Rows().Any(op =>
            op["recipe"].AsMap()?["defName"].AsString() == recipe && op["kind"].AsString() == kind && op["partIndex"].Strict() is { } index && (int)index == part);

    /// <summary>The condition patient and part the setup op replied (Go <c>conditionFixture</c>).</summary>
    internal static (string Patient, int Part) ConditionFixture(ISession s)
    {
        var prepared = s.Prepared ?? [];
        var patient = prepared["conditionPatient"].AsString();
        var index = prepared["conditionPart"].Strict();
        if (patient.Length == 0 || index is null || index < 0)
        {
            throw new AcceptanceException($"medical_management_setup reply lacks conditionPatient/conditionPart: {prepared.ToJson()}");
        }

        return (patient, (int)index.Value);
    }

    /// <summary>The surgical patient and missing-part index the setup op replied (<c>surgical</c>/<c>part</c>).</summary>
    internal static (string Patient, int Part) SurgicalFixture(ISession s)
    {
        var prepared = s.Prepared ?? [];
        var patient = prepared["surgical"].AsString();
        var part = prepared["part"].Strict();
        if (patient.Length == 0 || part is null)
        {
            throw new AcceptanceException($"medical_management_setup reply lacks surgical/part: {prepared.ToJson()}");
        }

        return (patient, (int)part.Value);
    }

    /// <summary>The fixture start every surgery planner case opens on: the setup op on the lab with one condition.</summary>
    internal static FixtureStart Setup(string condition) => new()
    {
        Op = "test/medical_management_setup",
        On = new LabStart(),
        Args = new JsonObject { ["disease"] = false, ["condition"] = condition },
    };
}
