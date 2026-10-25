using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The native surgery facts and the medical bill (#1161, #1162, #1164-#1166, #1169, #1239). Each row opens on
/// test/medical_management_setup: three Medicine 20 doctors, the third colonist missing a leg with a SimpleProstheticLeg
/// stocked and, under a condition, a sterile lit surgery room plus one condition on the second colonist (and a prisoner for
/// the kidney rows). The rows read the vanilla recipe discovery, success chances and harvest facts, apply medical
/// ProductionBillIntents, and let native doctors complete one install. Which operation the brain queues is policy
/// (SurgerySelectionTests); these prove the native read and write it decides over.
/// </summary>
internal static class MedicalSurgery
{
    internal const string SetupTool = "test/medical_management_setup";

    // The restore's walk, haul and InstallSimpleProstheticLeg by a Medicine 20 doctor, with room to spare.
    private const int RestoreTicks = 20000;

    private static readonly FixtureOp<string, Fx.MedicalSetup> Setup = new(
        SetupTool,
        condition => ServedOps.Args(("disease", false), ("condition", condition)),
        ServedOps.Reply<Fx.MedicalSetup>(SetupTool));

    private sealed record Row(string Name, string Condition, int Ticks, string Scope, Func<TypedSession, Fx.MedicalSetup, CancellationToken, Task> Run);

    private static readonly Row[] Rows =
    [
        new(
            "medical/surgery-facts",
            "",
            0,
            "list_pawns' health detail on a colonist missing a leg with a prosthetic in stock reports the missing part and a restore "
                + "operation on it with a vanilla success chance above zero.",
            FactsAsync),
        new(
            "medical/surgery-intent",
            "missingKidney",
            0,
            "A medical ProductionBillIntent queues InstallPegLeg on the missing leg (SURGERY_STATE_QUEUED, the patient's Bill_Medical); "
                + "the same intent again applies to the same bill; and RemoveBodyPart on a "
                + "non-player prisoner's kidney is refused as a violation without acknowledge_violation.",
            IntentAsync),
        new(
            "medical/surgery-restore",
            "restore",
            RestoreTicks,
            "A medical ProductionBillIntent queues InstallSimpleProstheticLeg on the missing leg, and native doctors install it in the "
                + "surgery room: the health detail no longer reports the part missing.",
            RestoreAsync),
        new(
            "medical/surgery-cataract",
            "cataract",
            0,
            "The health detail reads a Cataract on the eye with its part index, and with a bionic eye in stock an InstallBionicEye "
                + "install operation on that eye.",
            CataractAsync),
        new(
            "medical/surgery-amputation",
            "infectedHand",
            0,
            "The health detail reads a hand WoundInfection with its part index and a RemoveBodyPart operation of kind amputate on "
                + "that hand.",
            AmputationAsync),
        new(
            "medical/surgery-harvest",
            "missingKidney",
            0,
            "The population read carries an unrecruitable prisoner's kidney harvest operation, its faction and a harvest goodwill "
                + "change at or below zero.",
            HarvestAsync),
        new(
            "medical/surgery-harvest-care-limit",
            "missingKidneyIndustrial",
            0,
            "With industrial medicine the only stock, the population read reports the prisoner's HerbalOrWorse medical care and its "
                + "kidney harvest as medicine-care limited with its ingredients not on the map.",
            HarvestCareLimitAsync),
    ];

    internal static IEnumerable<TypedCase> All() => Rows.Select(row => new TypedCase
    {
        Name = row.Name,
        Scope = row.Scope,
        Ticks = row.Ticks,
        Tier = CaseTier.OnDemand,
        Ops = [SetupTool],
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            var setup = await s.FixtureAsync("medical-setup", Setup, row.Condition, ct).ConfigureAwait(false);
            if (!setup.Success || setup.Surgical.Length == 0)
            {
                throw new AcceptanceException($"medical-setup: {setup.Error ?? ServedOps.Json(setup)?.ToJsonString()}");
            }

            await row.Run(s, setup, ct).ConfigureAwait(false);
        },
    });

    private static async Task<RimGovernor.Brain.GameFacts.Observations.PawnHealth> HealthAsync(TypedSession s, string label, string pawn, CancellationToken cancellationToken) =>
        (await TypedPawns.OneAsync(s, label, pawn, cancellationToken).ConfigureAwait(false)).Health
            ?? throw new AcceptanceException($"{label}: {pawn} read no health detail");

    private static bool Missing(RimGovernor.Brain.GameFacts.Observations.PawnHealth health, int part) => health.Surgery?.MissingParts.Any(m => m.Part.Index == part) == true;

    private static RimGovernor.Brain.GameFacts.Observations.SurgeryOperation? Operation(RimGovernor.Brain.GameFacts.Observations.PawnHealth health, Func<RimGovernor.Brain.GameFacts.Observations.SurgeryOperation, bool> match) =>
        health.Surgery?.Operations.FirstOrDefault(match);

    private static string OperationList(RimGovernor.Brain.GameFacts.Observations.PawnHealth health) =>
        string.Join("; ", health.Surgery?.Operations.Select(o => $"{o.Recipe}/{o.Kind}@{o.Part?.Index}") ?? []);

    private static string Condition(Fx.MedicalSetup setup) =>
        setup.ConditionPatient is { Length: > 0 } patient && setup.ConditionPart >= 0
            ? patient
            : throw new AcceptanceException($"medical-setup named no condition patient and part: {ServedOps.Json(setup)}");

    private static string Prisoner(Fx.MedicalSetup setup) =>
        setup.PrisonerId is { Length: > 0 } prisoner ? prisoner : throw new AcceptanceException($"medical-setup held no prisoner: {ServedOps.Json(setup)}");

    private static async Task FactsAsync(TypedSession s, Fx.MedicalSetup setup, CancellationToken cancellationToken)
    {
        var health = await HealthAsync(s, "surgery-facts", setup.Surgical, cancellationToken).ConfigureAwait(false);
        s.RecordWire("surgery", health.Surgery);
        if (!Missing(health, setup.Part))
        {
            throw new AcceptanceException($"the missing leg {setup.Part} is not reported");
        }

        if (Operation(health, o => o.Part?.Index == setup.Part && o.Kind == RimGovernor.Brain.GameFacts.Observations.SurgeryKind.Restore && o.Chance is RimGovernor.Brain.GameFacts.Observations.SurgeryChance.Rated { Success: > 0 }) is null)
        {
            throw new AcceptanceException($"no restore operation with a positive success chance on part {setup.Part}: {OperationList(health)}");
        }
    }

    private static RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent Bill(string patient, string recipe, int part) => new()
    {
        Patient = new RimGovernor.Brain.GameFacts.Common.Ref { Id = patient },
        RecipeDef = recipe,
        PartIndex = new RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.Part(part), AcknowledgeViolation = false,
    };

    private static async Task<string> QueuedAsync(TypedSession s, string key, RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent bill, CancellationToken cancellationToken)
    {
        var effect = (await TypedPawns.AppliedAsync(s, key, bill, cancellationToken).ConfigureAwait(false))?.SurgeryBill;
        return effect is { State: RimGovernor.Brain.GameFacts.Receipts.SurgeryState.Queued, Bill.Id: { Length: > 0 } id } && effect.PawnId == bill.Patient?.Id && (effect.Target is RimGovernor.Brain.GameFacts.Receipts.SurgeryTarget.WholeBodyCase ? bill.PartIndex is RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.WholeBody : effect.Target is RimGovernor.Brain.GameFacts.Receipts.SurgeryTarget.PartCase part && bill.PartIndex is RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.Part selected && part.Index == selected.Index)
            ? id
            : throw new AcceptanceException($"{key}: want a QUEUED surgery bill on {bill.Patient?.Id} part {bill.PartIndex}, got {effect}");
    }

    private static async Task IntentAsync(TypedSession s, Fx.MedicalSetup setup, CancellationToken cancellationToken)
    {
        var prisoner = Prisoner(setup);
        _ = Condition(setup);

        // The fixture edits health outside authority, so authority is granted after it.
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var first = await QueuedAsync(s, "surgery-queue", Bill(setup.Surgical, "InstallPegLeg", setup.Part), cancellationToken).ConfigureAwait(false);
        var again = await QueuedAsync(s, "surgery-requeue", Bill(setup.Surgical, "InstallPegLeg", setup.Part), cancellationToken).ConfigureAwait(false);
        if (again != first)
        {
            throw new AcceptanceException($"surgery-requeue: queued bill {again} beside {first}");
        }

        s.Record("bill", first);
        var queued = (await HealthAsync(s, "surgery-bills", setup.Surgical, cancellationToken).ConfigureAwait(false)).Surgery?.SurgeryBills ?? [];
        if (queued.Count(b => b.Id == first) != 1 || queued.Single(b => b.Id == first).Part?.Index != setup.Part)
        {
            throw new AcceptanceException($"the health detail does not read bill {first} once on part {setup.Part}: {string.Join("; ", queued)}");
        }

        // Both are human bodies, so the patient's kidney index is the prisoner's.
        var result = await TypedPawns.ApplyAsync(s, "surgery-violation", Bill(prisoner, "RemoveBodyPart", setup.ConditionPart), cancellationToken).ConfigureAwait(false);
        var refused = result.Outcome?.Refused;
        s.Record("violation_refusal", refused?.Reason);
        if (refused?.Reason?.Contains("violation", StringComparison.Ordinal) != true)
        {
            throw new AcceptanceException($"unacknowledged organ removal: want a violation refusal, got {result.Outcome}");
        }
    }

    private static async Task RestoreAsync(TypedSession s, Fx.MedicalSetup setup, CancellationToken cancellationToken)
    {
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await QueuedAsync(s, "restore-queue", Bill(setup.Surgical, "InstallSimpleProstheticLeg", setup.Part), cancellationToken).ConfigureAwait(false);
        var reads = 0;
        var elapsed = await s.RunUntilAsync("restore-installed", RestoreTicks, async ct =>
        {
            var health = await HealthAsync(s, "restore-health-" + CaseSupport.Tag(reads++), setup.Surgical, ct).ConfigureAwait(false);
            return (Missing(health, setup.Part) ? "missing" : "restored", !Missing(health, setup.Part));
        }, cancellationToken).ConfigureAwait(false);
        s.Record("restore_ticks", elapsed);
    }

    private static async Task CataractAsync(TypedSession s, Fx.MedicalSetup setup, CancellationToken cancellationToken)
    {
        var patient = Condition(setup);
        var health = await HealthAsync(s, "cataract-health", patient, cancellationToken).ConfigureAwait(false);
        s.RecordWire("surgery", health.Surgery);
        if (!health.Hediffs.Any(h => h.DefName == "Cataract" && h.Part is { DefName: "Eye" } eye && eye.Index == setup.ConditionPart))
        {
            throw new AcceptanceException($"no Cataract read on part {setup.ConditionPart}");
        }

        if (Operation(health, o => o.Recipe == "InstallBionicEye" && o.Kind == RimGovernor.Brain.GameFacts.Observations.SurgeryKind.Install && o.Part?.Index == setup.ConditionPart) is null)
        {
            throw new AcceptanceException($"no InstallBionicEye install operation on the cataract eye {setup.ConditionPart}: {OperationList(health)}");
        }
    }

    private static async Task AmputationAsync(TypedSession s, Fx.MedicalSetup setup, CancellationToken cancellationToken)
    {
        var patient = Condition(setup);
        var health = await HealthAsync(s, "amputation-health", patient, cancellationToken).ConfigureAwait(false);
        s.RecordWire("surgery", health.Surgery);
        if (!health.Hediffs.Any(h => h.DefName == "WoundInfection" && h.Part is { DefName: "Hand" } hand && hand.Index == setup.ConditionPart))
        {
            throw new AcceptanceException($"WoundInfection not read on its part {setup.ConditionPart}");
        }

        if (Operation(health, o => o.Recipe == "RemoveBodyPart" && o.Kind == RimGovernor.Brain.GameFacts.Observations.SurgeryKind.Amputate && o.Part?.Index == setup.ConditionPart) is null)
        {
            throw new AcceptanceException($"no RemoveBodyPart amputate operation on the infected hand {setup.ConditionPart}: {OperationList(health)}");
        }
    }

    private static async Task<(RimGovernor.Brain.GameFacts.Observations.PersonCustody.Prisoner Person, RimGovernor.Brain.GameFacts.Observations.SurgeryOperation Harvest)> KidneyHarvestAsync(TypedSession s, string label, string prisoner, CancellationToken cancellationToken)
    {
        var population = await s.CallAsync(label, NativeOps.ReadPopulation, new RimGovernor.Brain.GameOrders.Observations.PopulationRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var person = population.Persons.FirstOrDefault(p => p.Pawn.Value == prisoner)?.Custody as RimGovernor.Brain.GameFacts.Observations.PersonCustody.Prisoner
            ?? throw new AcceptanceException($"{label}: prisoner {prisoner} is not a colony prisoner in the population read");
        var health = await HealthAsync(s, label + "-health", prisoner, cancellationToken).ConfigureAwait(false);
        var surgery = health.Surgery ?? throw new AcceptanceException($"{label}: prisoner {prisoner} has no surgery facts");
        s.RecordWire("prisoner_surgery", surgery);
        var harvest = surgery.Operations.FirstOrDefault(o => o.Kind == RimGovernor.Brain.GameFacts.Observations.SurgeryKind.Harvest && o.Part?.DefName == "Kidney")
            ?? throw new AcceptanceException($"{label}: prisoner {prisoner} has no kidney harvest operation");
        return (person, harvest);
    }

    private static async Task HarvestAsync(TypedSession s, Fx.MedicalSetup setup, CancellationToken cancellationToken)
    {
        var prisoner = Prisoner(setup);
        var (person, _) = await KidneyHarvestAsync(s, "harvest-population", prisoner, cancellationToken).ConfigureAwait(false);
        s.Record("goodwill", person.HarvestGoodwillChange);
        if (person.Faction is null)
        {
            throw new AcceptanceException($"prisoner {prisoner} reads no faction");
        }

        if (person.HarvestGoodwillChange > 0)
        {
            throw new AcceptanceException($"prisoner {prisoner} harvest goodwill change {person.HarvestGoodwillChange}, want at most zero");
        }
    }

    private static async Task HarvestCareLimitAsync(TypedSession s, Fx.MedicalSetup setup, CancellationToken cancellationToken)
    {
        var prisoner = Prisoner(setup);
        var (_, harvest) = await KidneyHarvestAsync(s, "care-limit-population", prisoner, cancellationToken).ConfigureAwait(false);
        var pawn = await TypedPawns.OneAsync(s, "care-limit-pawn", prisoner, cancellationToken).ConfigureAwait(false);
        if (pawn.Settings.Care is not RimGovernor.Brain.GameFacts.Observations.PawnCare.Configured { MedicalCare: RimGovernor.Brain.GameFacts.Operations.MedicalCare.HerbalOrWorse })
        {
            throw new AcceptanceException($"prisoner {prisoner} medical care {pawn.Settings.Care}, want HerbalOrWorse");
        }

        if (!harvest.MedicineCareLimited || harvest.IngredientsOnMap)
        {
            throw new AcceptanceException($"kidney harvest reads medicine-care limited {harvest.MedicineCareLimited} and ingredients on map {harvest.IngredientsOnMap}, want true and not true");
        }
    }
}
