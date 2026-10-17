using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The surgery planner fixture cases (Go <c>surgerycases.go</c>). Each opens on <c>test/medical_management_setup</c>
/// (three Medicine 20 doctors, the third colonist missing a leg with a SimpleProstheticLeg stocked, a sterile lit
/// surgery room with a medical hospital bed) plus one condition on the second colonist, and serves the medical family,
/// which carries the medical family.
/// </summary>
internal static class SurgeryCases
{
    private const string HerbalOrWorse = "MEDICAL_CARE_HERBAL_OR_WORSE";

    internal static IEnumerable<AcceptanceCase> All()
    {
        yield return new AcceptanceCase
        {
            Name = "medical/surgery-restore",
            Scope = "Restore (#1164): a colonist missing a leg with a prosthetic in stock gets an InstallSimpleProstheticLeg "
                + "medical ProductionBillIntent from medical/surgery, and native doctors install it (the part is no longer missing). "
                + "End to end: the vanilla doctor job completing the bill is no Go snapshot; the service queues it, the case then advances the game.",
            Start = MedicalKit.Setup("restore"),
            Service = true,
            Budget = TimeSpan.FromMinutes(10),
            Crew = new Crew { Size = 3 },
            Run = RestoreAsync,
        };
        yield return new AcceptanceCase
        {
            Name = "medical/surgery-cataract",
            Scope = "Chronic replacement (#1165): the pawn read carries a Cataract on the eye, and with a bionic eye in stock "
                + "medical/surgery admits a medical bill on that eye. A native read contract: the chronic defName and part index come from vanilla hediffs.",
            Start = MedicalKit.Setup("cataract"),
            Service = true,
            Budget = TimeSpan.FromMinutes(5),
            Crew = new Crew { Size = 3 },
            Run = CataractAsync,
        };
        yield return new AcceptanceCase
        {
            Name = "medical/surgery-amputation",
            Scope = "Life-saving amputation (#1166): a hand WoundInfection losing its immunity race reads with its part index "
                + "and a RemoveBodyPart operation of kind amputate; the patient's medical/critical Need queues it and native doctors complete it. Native: the read contract and vanilla infection physics.",
            Start = MedicalKit.Setup("infectedHand"),
            Service = true,
            Budget = TimeSpan.FromMinutes(10),
            Crew = new Crew { Size = 3 },
            Run = AmputationAsync,
        };
        yield return new AcceptanceCase
        {
            Name = "medical/surgery-harvest",
            Scope = "Organ harvest (#1169): with an unrecruitable prisoner, a colonist missing a kidney and none stocked, the "
                + "population read carries the prisoner's harvest facts, medical/surgery harvests a kidney from the prisoner, "
                + "then installs it with InstallNaturalKidney. Native: the population read contract and the vanilla harvest yielding the kidney.",
            Start = MedicalKit.Setup("missingKidney"),
            Service = true,
            Budget = TimeSpan.FromMinutes(10),
            Crew = new Crew { Size = 3 },
            Run = HarvestAsync,
        };
        yield return new AcceptanceCase
        {
            Name = "medical/surgery-harvest-care-limit",
            Scope = "Prisoner care limit (#1239): with industrial medicine the only stock, the population read reports the "
                + "prisoner's HerbalOrWorse medical_care and its kidney harvest as medicine_care_limited with ingredients_on_map "
                + "false. Native: the population read contract; the refusal and herbal want are policy snapshot tests.",
            Start = MedicalKit.Setup("missingKidneyIndustrial"),
            Budget = TimeSpan.FromMinutes(3),
            Crew = new Crew { Size = 3 },
            Run = HarvestCareLimitAsync,
        };
        yield return new AcceptanceCase
        {
            Name = "medical/surgery-part-recovery",
            Scope = "Artificial part recovery (#1232): with an unrecruitable low-worth prisoner wearing a BionicArm and a colonist "
                + "missing an arm with none stocked, medical/surgery removes the arm from the prisoner, then installs it with "
                + "InstallBionicArm. Native: the vanilla removal yielding the BionicArm item.",
            Start = MedicalKit.Setup("missingArm"),
            Service = true,
            Budget = TimeSpan.FromMinutes(10),
            Crew = new Crew { Size = 3 },
            Run = PartRecoveryAsync,
        };
    }

    private static async Task HarvestCareLimitAsync(ISession s, CancellationToken cancellationToken)
    {
        var prisoner = (s.Prepared ?? [])["prisonerId"].AsString();
        var observed = await PopulationAsync(s, "surgery-care-limit-population", cancellationToken).ConfigureAwait(false);
        foreach (var person in observed["persons"].Rows())
        {
            if (prisoner.Length == 0 || person["pawn"].RefId() != prisoner)
            {
                continue;
            }

            s.Report["medical_care"] = person["medicalCare"].Copy();
            var care = person["medicalCare"].AsString();
            if (care != HerbalOrWorse)
            {
                throw new AcceptanceException($"prisoner {prisoner} medical_care \"{care}\", want HerbalOrWorse");
            }

            var surgery = person["surgery"].AsMap() ?? [];
            foreach (var op in surgery["operations"].Rows())
            {
                if (op["kind"].AsString() != "SURGERY_KIND_HARVEST" || op["partDefName"].AsString() != "Kidney")
                {
                    continue;
                }

                s.Report["harvest_op"] = op.DeepClone();
                if (op["medicineCareLimited"].AsBool() != true)
                {
                    throw new AcceptanceException($"kidney harvest not medicine_care_limited: {op.ToJson()}");
                }

                if (op["ingredientsOnMap"].AsBool() == true)
                {
                    throw new AcceptanceException($"kidney harvest reads ingredients_on_map true under the herbal limit: {op.ToJson()}");
                }

                return;
            }

            throw new AcceptanceException($"prisoner {prisoner} has no kidney harvest operation: {surgery["operations"].ToJson()}");
        }

        throw new AcceptanceException($"prisoner \"{prisoner}\" not in the population read");
    }

    private static async Task<JsonObject> PopulationAsync(ISession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.Harness.WireAsync(
            label,
            "observations_read_population",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        return Harness.Outcome(reply, "observed").Value;
    }

    private static async Task RestoreAsync(ISession s, CancellationToken cancellationToken)
    {
        var (patient, part) = MedicalKit.SurgicalFixture(s);
        var run = await SurgeryRun.StartAsync(s, "surgery-restore", cancellationToken).ConfigureAwait(false);
        try
        {
            await run.UntilAsync(
                TimeSpan.FromMinutes(4),
                (surgeries, _) => Task.FromResult((Wait.Signature(surgeries.Count), SurgeryRun.Find(surgeries, patient, part, "InstallSimpleProstheticLeg") is not null)),
                cancellationToken).ConfigureAwait(false);
            s.Report["restore_intent"] = true;
            await run.ServedAsync("surgery-restore", patient, health => !MedicalKit.PartMissing(health, part), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await run.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CataractAsync(ISession s, CancellationToken cancellationToken)
    {
        var (patient, part) = MedicalKit.ConditionFixture(s);
        var health = await MedicalKit.PawnHealthAsync(s, "surgery-cataract-health", patient, cancellationToken).ConfigureAwait(false);
        if (!MedicalKit.HediffAt(health, "Cataract", part))
        {
            throw new AcceptanceException($"no Cataract hediff read on part {part}: {health["hediffs"].ToJson()}");
        }

        if (!MedicalKit.OperationAt(health, "InstallBionicEye", "SURGERY_KIND_INSTALL", part))
        {
            throw new AcceptanceException($"no InstallBionicEye install operation on the cataract eye {part}: {MedicalKit.Surgery(health)["operations"].ToJson()}");
        }

        var run = await SurgeryRun.StartAsync(s, "surgery-cataract", cancellationToken).ConfigureAwait(false);
        try
        {
            await run.UntilAsync(
                TimeSpan.FromMinutes(4),
                (surgeries, _) =>
                {
                    if (SurgeryRun.Find(surgeries, patient, part, "") is not { } surgery)
                    {
                        return Task.FromResult((Wait.Signature(surgeries.Count), false));
                    }

                    s.Report["cataract_intent"] = new JsonObject { ["pawn"] = patient, ["part"] = part, ["recipe"] = surgery.Recipe };
                    return Task.FromResult(("", true));
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await run.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AmputationAsync(ISession s, CancellationToken cancellationToken)
    {
        var (patient, part) = MedicalKit.ConditionFixture(s);
        var health = await MedicalKit.PawnHealthAsync(s, "surgery-amputation-health", patient, cancellationToken).ConfigureAwait(false);
        if (!MedicalKit.HediffAt(health, "WoundInfection", part))
        {
            throw new AcceptanceException($"WoundInfection not read on its part {part}: {health["hediffs"].ToJson()}");
        }

        if (!MedicalKit.OperationAt(health, "RemoveBodyPart", "SURGERY_KIND_AMPUTATE", part))
        {
            throw new AcceptanceException($"no RemoveBodyPart amputate operation on the infected hand {part}: {MedicalKit.Surgery(health)["operations"].ToJson()}");
        }

        var run = await SurgeryRun.StartAsync(s, "surgery-amputation", cancellationToken).ConfigureAwait(false);
        try
        {
            await run.UntilAsync(
                TimeSpan.FromMinutes(4),
                (surgeries, _) => Task.FromResult((Wait.Signature(surgeries.Count), SurgeryRun.Find(surgeries, patient, part, "RemoveBodyPart") is not null)),
                cancellationToken).ConfigureAwait(false);
            s.Report["amputation_intent"] = true;
            await run.ServedAsync(
                "surgery-amputation",
                patient,
                after => MedicalKit.PartMissing(after, part) && !MedicalKit.HediffAt(after, "WoundInfection", part),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await run.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task HarvestAsync(ISession s, CancellationToken cancellationToken)
    {
        var (patient, part) = MedicalKit.ConditionFixture(s);
        var prisoner = PrisonerId(s);
        await HarvestFactsAsync(s, prisoner, cancellationToken).ConfigureAwait(false);
        await TransferPartAsync(s, "surgery-harvest", prisoner, patient, part, "InstallNaturalKidney", cancellationToken).ConfigureAwait(false);
    }

    private static async Task PartRecoveryAsync(ISession s, CancellationToken cancellationToken)
    {
        var (patient, part) = MedicalKit.ConditionFixture(s);
        var prisoner = PrisonerId(s);
        await TransferPartAsync(s, "surgery-part-recovery", prisoner, patient, part, "InstallBionicArm", cancellationToken).ConfigureAwait(false);
    }

    private static string PrisonerId(ISession s)
    {
        var prepared = s.Prepared ?? [];
        var prisoner = prepared["prisonerId"].AsString();
        if (prisoner.Length == 0)
        {
            throw new AcceptanceException($"medical_management_setup reply lacks prisonerId: {prepared.ToJson()}");
        }

        return prisoner;
    }

    // Waits for the RemoveBodyPart intent on the prisoner, lets native doctors complete it, then waits for the same
    // service to install the yielded part on the patient: the queued cut is clock work, so the service runs it and then
    // sees the part in stock.
    private static async Task TransferPartAsync(ISession s, string label, string prisoner, string patient, int part, string recipe, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var run = await SurgeryRun.StartAsync(s, label, cancellationToken).ConfigureAwait(false);
        try
        {
            SurgeryIntent? cut = null;
            await run.UntilAsync(
                TimeSpan.FromMinutes(4),
                (surgeries, _) =>
                {
                    cut = SurgeryRun.Find(surgeries, prisoner, -1, "RemoveBodyPart");
                    return Task.FromResult((Wait.Signature(surgeries.Count), cut is not null));
                },
                cancellationToken).ConfigureAwait(false);
            report["removal_intent"] = new JsonObject { ["prisoner"] = prisoner, ["part"] = cut?.Part };
            await run.UntilAsync(
                TimeSpan.FromMinutes(6),
                (surgeries, _) =>
                {
                    var found = SurgeryRun.Find(surgeries, patient, part, recipe) is not null;
                    if (found)
                    {
                        report["install_intent"] = new JsonObject { ["pawn"] = patient, ["part"] = part };
                    }

                    return Task.FromResult((Wait.Signature(surgeries.Count), found));
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await run.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Checks the population read's fields on the prisoner: a harvest operation on a kidney, its faction id and its
    // harvest goodwill change.
    private static async Task HarvestFactsAsync(ISession s, string prisoner, CancellationToken cancellationToken)
    {
        var observed = await PopulationAsync(s, "surgery-harvest-population", cancellationToken).ConfigureAwait(false);
        foreach (var person in observed["persons"].Rows())
        {
            if (person["pawn"].RefId() != prisoner)
            {
                continue;
            }

            var surgery = person["surgery"].AsMap() ?? [];
            var goodwill = person["harvestGoodwillChange"].Strict();
            s.Report["harvest_facts"] = new JsonObject { ["faction"] = person["factionId"].Copy(), ["goodwill"] = person["harvestGoodwillChange"].Copy() };
            var harvest = false;
            foreach (var op in surgery["operations"].Rows())
            {
                harvest = harvest || (op["kind"].AsString() == "SURGERY_KIND_HARVEST" && op["partDefName"].AsString() == "Kidney");
            }

            if (!harvest)
            {
                throw new AcceptanceException($"prisoner {prisoner} has no kidney harvest operation: {surgery["operations"].ToJson()}");
            }

            if (person["faction"].RefId().Length == 0)
            {
                throw new AcceptanceException($"prisoner {prisoner} has no faction_id");
            }

            if (goodwill is null or > 0)
            {
                throw new AcceptanceException($"prisoner {prisoner} harvest_goodwill_change absent or positive: {person["harvestGoodwillChange"].ToText()}");
            }

            return;
        }

        throw new AcceptanceException($"prisoner {prisoner} not in the population read");
    }
}
