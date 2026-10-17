using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The food, drug and reading policy writes (#1306, #1537, #1538, #1541, #1543): each row writes a named policy's
/// contents through its policy intent, assigns it with the PawnSettingsIntent arm, and reads the policy's contents and
/// holders back from the colony read's policy facts. The diets, drug schedules and book choices themselves are policy;
/// these prove the native write, assignment and read.
/// </summary>
internal static class PawnPolicies
{
    private sealed record Row(string Name, string Scope, Fx.StageSpec Stage, Func<TypedSession, CancellationToken, Task> Run);

    private static readonly Row[] Rows =
    [
        new(
            "pawn/food-policy",
            "A FoodPolicyIntent writes a meals-only policy and a kibble-only policy, PawnSettingsIntent.food_policy assigns the first to a "
                + "colonist and the second to a tame dog's food tracker, and the policy facts read each policy allowing exactly its "
                + "definitions and held by its pawn.",
            new Fx.StageSpec
            {
                Paused = true,
                Site = new Fx.StageSite { Width = 3, Height = 3, Near = Fx.StageAnchor.MapCenter, OffsetZ = 4 },
                Pawns =
                [
                    new Fx.StagePawn { Name = "colonist", Colonist = 0 },
                    new Fx.StagePawn { Name = "dog", Kind = "Husky", Faction = Fx.StageFaction.Player, Age = 4, Cell = new Fx.StageCell(1, 1) },
                ],
            },
            FoodAsync),
        new(
            "pawn/drug-policy",
            "A DrugPolicyIntent writes Beer for joy, Penoxycyline on a schedule and Luciferium for addiction, PawnSettingsIntent.drug_policy "
                + "assigns it to a luciferium-addicted colonist, the policy facts read those entries held by the pawn, and the needs "
                + "forecast reports the scheduled drug as an unsupported voluntary state.",
            new Fx.StageSpec { Paused = true, Pawns = [new Fx.StagePawn { Name = "colonist", Colonist = 0, Hediffs = ["LuciferiumAddiction"] }] },
            DrugAsync),
        new(
            "pawn/reading-policy",
            "A ReadingPolicyIntent writes a textbooks-only policy, PawnSettingsIntent.reading_policy assigns it, and the policy facts read it "
                + "allowing exactly Textbook and held by the colonist.",
            new Fx.StageSpec { Paused = true, Pawns = [new Fx.StagePawn { Name = "colonist", Colonist = 0 }] },
            ReadingAsync),
    ];

    internal static IEnumerable<TypedCase> All() => Rows.Select(row => new TypedCase
    {
        Name = row.Name,
        Scope = row.Scope,
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = row.Stage,
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            await TypedAuthority.GrantAutoAsync(s, ct).ConfigureAwait(false);
            await row.Run(s, ct).ConfigureAwait(false);
        },
    });

    private static string Pawn(TypedSession s, string name) => s.Staged?.Pawns[name].Id ?? throw new AcceptanceException($"the stage made no {name}");

    private static async Task<Observations.ColonyFactsSnapshot> FactsAsync(TypedSession s, string label, bool planning, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = planning }, cancellationToken).ConfigureAwait(false);
        return TypedReads.Observed(label, NativeOps.ReadColonyFacts, reply, reply.Outcome?.Observed);
    }

    private static async Task<Observations.PolicyFacts> PoliciesAsync(TypedSession s, string label, CancellationToken cancellationToken) =>
        (await FactsAsync(s, label, false, cancellationToken).ConfigureAwait(false)).Policies?.Outcome?.Observed
            ?? throw new AcceptanceException($"{label}: policy facts unavailable");

    /// <summary>Writes a policy's contents, then assigns it to each pawn.</summary>
    private static async Task WriteAsync(TypedSession s, string key, Operations.ActionIntent policy, IEnumerable<(string Pawn, Operations.PawnSettingsIntentSetting Setting)> assignments, CancellationToken cancellationToken)
    {
        _ = await TypedPawns.AppliedAsync(s, key + "-write", policy, cancellationToken).ConfigureAwait(false);
        foreach (var (pawn, setting) in assignments)
        {
            _ = await TypedPawns.AppliedAsync(s, $"{key}-assign-{pawn}", new Operations.PawnSettingsIntent { PawnId = pawn, Setting = setting }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The one policy labelled <paramref name="label"/>, which must be held by <paramref name="pawn"/>.</summary>
    private static Observations.PolicyEntry Held(IEnumerable<Observations.PolicyEntry> policies, string label, string pawn)
    {
        var policy = policies.SingleOrDefault(p => p.Label == label) ?? throw new AcceptanceException($"no single policy labelled \"{label}\"");
        return policy.PawnIds.Contains(pawn) ? policy : throw new AcceptanceException($"policy \"{label}\" is held by [{string.Join(' ', policy.PawnIds)}], not {pawn}");
    }

    private static void Allows(Observations.PolicyEntry policy, params string[] defs)
    {
        if (!policy.AllowedDefs.Order(StringComparer.Ordinal).SequenceEqual(defs.Order(StringComparer.Ordinal)))
        {
            throw new AcceptanceException($"policy \"{policy.Label}\" allows [{string.Join(' ', policy.AllowedDefs)}], want [{string.Join(' ', defs)}]");
        }
    }

    private static async Task FoodAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var colonist = Pawn(s, "colonist");
        var dog = Pawn(s, "dog");
        string[] meals = ["MealSimple", "MealFine"];
        await WriteAsync(s, "meals", new Operations.FoodPolicyIntent { Name = "Meals only", AllowedDefs = [.. meals] },
            [(colonist, new Operations.PawnSettingsIntentSetting.FoodPolicyCase("Meals only"))], cancellationToken).ConfigureAwait(false);
        await WriteAsync(s, "kibble", new Operations.FoodPolicyIntent { Name = "Kibble only", AllowedDefs = ["Kibble"] },
            [(dog, new Operations.PawnSettingsIntentSetting.FoodPolicyCase("Kibble only"))], cancellationToken).ConfigureAwait(false);
        var facts = await PoliciesAsync(s, "food-policies", cancellationToken).ConfigureAwait(false);
        s.RecordWire("food", facts.Food);
        Allows(Held(facts.Food, "Meals only", colonist), meals);
        Allows(Held(facts.Food, "Kibble only", dog), "Kibble");
    }

    private static async Task DrugAsync(TypedSession s, CancellationToken cancellationToken)
    {
        const string Label = "Explicit drugs";
        var colonist = Pawn(s, "colonist");
        Operations.DrugPolicyEntry[] entries =
        [
            new() { DrugDef = "Beer", AllowedForJoy = true, DaysFrequency = 1, OnlyIfMoodBelow = 1, OnlyIfJoyBelow = 1 },
            new() { DrugDef = "Penoxycyline", AllowScheduled = true, DaysFrequency = 5, OnlyIfMoodBelow = 1, OnlyIfJoyBelow = 1 },
            new() { DrugDef = "Luciferium", AllowScheduled = true, AllowedForAddiction = true, DaysFrequency = 4, OnlyIfMoodBelow = 1, OnlyIfJoyBelow = 1 },
        ];
        await WriteAsync(s, "drugs", new Operations.DrugPolicyIntent { Name = Label, Entries = [.. entries] },
            [(colonist, new Operations.PawnSettingsIntentSetting.DrugPolicyCase(Label))], cancellationToken).ConfigureAwait(false);
        var facts = await PoliciesAsync(s, "drug-policies", cancellationToken).ConfigureAwait(false);
        var policy = Held(facts.Drug, Label, colonist);
        s.RecordWire("drug", policy);
        Operations.DrugPolicyEntry? Entry(string def) => policy.DrugEntries.FirstOrDefault(e => e.DrugDef == def);
        if (Entry("Beer") is not { AllowedForJoy: true, AllowScheduled: not true, AllowedForAddiction: not true }
            || Entry("Penoxycyline") is not { AllowScheduled: true, AllowedForJoy: not true }
            || Entry("Luciferium") is not { AllowScheduled: true, AllowedForAddiction: true })
        {
            throw new AcceptanceException($"policy \"{Label}\" reads entries [{string.Join("; ", policy.DrugEntries)}]");
        }

        var forecast = (await FactsAsync(s, "drug-forecast", true, cancellationToken).ConfigureAwait(false)).Forecast?.Outcome?.Needs
            ?? throw new AcceptanceException("drug-forecast: needs forecast unavailable");
        var row = forecast.Pawns.SingleOrDefault(p => p.Pawn == colonist) ?? throw new AcceptanceException($"drug-forecast: no needs row for {colonist}");
        s.RecordWire("needs_row_blockers", row.VoluntaryControlBlockers);
        var scheduled = new[] { "Penoxycyline", "Luciferium" };
        if (row.State != "unsupported_voluntary_state"
            || !row.VoluntaryControlBlockers.Any(b => scheduled.Any(d => b == "scheduled_drug_due:" + d || b == "scheduled_drug_onset_unassessed:" + d)))
        {
            throw new AcceptanceException($"a scheduled drug is not an unsupported voluntary state: {row.State} [{string.Join(' ', row.VoluntaryControlBlockers)}]");
        }
    }

    private static async Task ReadingAsync(TypedSession s, CancellationToken cancellationToken)
    {
        const string Label = "Textbooks only";
        var colonist = Pawn(s, "colonist");
        await WriteAsync(s, "reading", new Operations.ReadingPolicyIntent { Name = Label, AllowedDefs = ["Textbook"] },
            [(colonist, new Operations.PawnSettingsIntentSetting.ReadingPolicyCase(Label))], cancellationToken).ConfigureAwait(false);
        var facts = await PoliciesAsync(s, "reading-policies", cancellationToken).ConfigureAwait(false);
        s.RecordWire("reading", facts.Reading);
        Allows(Held(facts.Reading, Label, colonist), "Textbook");
    }
}
