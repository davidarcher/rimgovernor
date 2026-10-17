using RimGovernor.Brain.NativeFacade;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>husbandry/dispatch</c>: every direct-write animal order (train, slaughter, tame, release, allowed area, master,
/// following and the designation cancels) is one HusbandryIntent on <c>operations_apply</c>, an immediate settings write
/// with no native job, so the case reads each back at once through <c>observations_read_husbandry</c>. The bespoke
/// <c>test/husbandry_setup</c> prepares the herd: a one-step-short trainable, a pregnant mother, a slaughterable cow, a
/// releasable father, a factionless tameable muffalo, an obedient guard and an allowed area are not stage primitives.
/// </summary>
internal static class HusbandryDispatch
{
    private static readonly FixtureOp<NoArgs, Fx.HusbandrySetup> Setup = new("test/husbandry_setup", _ => [], ServedOps.Reply<Fx.HusbandrySetup>("test/husbandry_setup"));

    internal static TypedCase Case() => new()
    {
        Name = "husbandry/dispatch",
        Scope = "HusbandryIntents on operations_apply over the test/husbandry_setup herd: train, slaughter, tame, release, allowed "
            + "area (set and clear), master, follow-drafted, follow-fieldwork and the slaughter/release cancels apply and read back "
            + "at once through observations_read_husbandry (no native job), resends apply again, and taming a player animal, "
            + "an untrained master or follow and a missing area refuse by code; the wild muffalo is in the includeWild read and "
            + "the colony facts' wild census only.",
        Ticks = 0,
        Tier = CaseTier.Smoke,
        Ops = [Setup.Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        // The fixture spawns and despawns pawns outside any authority scope, so it runs before Auto is granted.
        var herd = await s.FixtureAsync("prepare", Setup, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        if (!herd.Success || new[] { herd.Mother, herd.Father, herd.Cow, herd.Dog, herd.Wild, herd.Guard, herd.Handler, herd.Area }.Any(id => id.Length == 0))
        {
            throw new AcceptanceException($"prepare: {Setup.Tool} refused or left an id out: {herd}");
        }

        s.Record("fixture", ServedOps.Json(herd));
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task<Observations.AnimalState?> FindAsync(string label, string id, bool includeWild)
        {
            var reply = await s.CallAsync(label, NativeOps.ReadHusbandry, new Observations.HusbandryRequest { Scope = s.Scope, IncludeWild = includeWild ? true : null }, cancellationToken).ConfigureAwait(false);
            return TypedReads.Observed(label, NativeOps.ReadHusbandry, reply, reply.Outcome?.Observed).Animals.FirstOrDefault(a => a.Pawn?.Id == id)?.Animal;
        }

        async Task<Observations.AnimalState> AnimalAsync(string label, string id, bool includeWild = false) =>
            await FindAsync(label, id, includeWild).ConfigureAwait(false) ?? throw new AcceptanceException($"{label}: animal {id} not in the husbandry census");

        async Task<Operations.ActionResultOutcome?> ApplyAsync(string key, Operations.HusbandryIntent intent)
        {
            var reply = await s.CallAsync(key, NativeOps.Apply, new Operations.ApplyRequest
            {
                Identity = s.Identity,
                Actions = [new Operations.Action { Key = key, Intent = intent }],
            }, cancellationToken).ConfigureAwait(false);
            return reply.Results.ToList() is [var result] ? result.Outcome : throw new AcceptanceException($"{key}: expected one result, got {reply.Results.Count}");
        }

        async Task<Receipts.AnimalEffect> AppliedAsync(string key, Operations.HusbandryIntent intent)
        {
            var outcome = await ApplyAsync(key, intent).ConfigureAwait(false);
            return outcome?.Applied?.Outcome?.Applied?.Observed?.Effect?.Animal
                ?? throw new AcceptanceException($"{key}: expected an applied animal effect, got {outcome}");
        }

        async Task RefusedAsync(string key, Operations.HusbandryIntent intent, Common.FailureCode code)
        {
            var outcome = await ApplyAsync(key, intent).ConfigureAwait(false);
            if (outcome?.Refused is not { } refusal || refusal.Code != code)
            {
                throw new AcceptanceException($"{key}: expected a {code} refusal, got {outcome}");
            }

            s.Record(key, refusal.Reason);
        }

        void Want(bool held, string what, object? got)
        {
            if (!held)
            {
                throw new AcceptanceException($"{what}: got {got}");
            }
        }

        Operations.HusbandryIntent Order(string id, Operations.HusbandryOrder order) => new() { AnimalId = id, Order = order };

        // Preconditions the fixture promises.
        var dog = await AnimalAsync("dog-before", herd.Dog).ConfigureAwait(false);
        var obedience = dog.Training.FirstOrDefault(t => t.DefName == "Obedience");
        Want(obedience is { Available: true, Learned: false, Wanted: false } && dog.Obedient != true, "dog-before: want Obedience available, unlearned, unwanted and the dog not obedient", dog);
        var cow = await AnimalAsync("cow-before", herd.Cow).ConfigureAwait(false);
        Want(cow is { SlaughterDesignatable: true, Slaughter: false } && cow.Downed != true && cow.InMentalState != true && cow.Pregnant != true && cow.ColonistBonded != true, "cow-before: want a slaughterable, undesignated cow", cow);
        var mother = await AnimalAsync("mother-before", herd.Mother).ConfigureAwait(false);
        Want(mother.Pregnant == true, "mother-before: want the raw pregnant fact", mother);

        // Train: SetWantedRecursive, read back at once; resending applies again.
        var train = Order(herd.Dog, Operations.HusbandryOrder.Train) with { TrainableDef = "Obedience" };
        var trained = await AppliedAsync("apply-train", train).ConfigureAwait(false);
        Want(trained is { Wanted: true, TrainableDef: "Obedience" }, "apply-train: want Obedience wanted", trained);
        dog = await AnimalAsync("dog-after-train", herd.Dog).ConfigureAwait(false);
        Want(dog.Training.Any(t => t is { DefName: "Obedience", Wanted: true }), "dog-after-train: want Obedience wanted", dog);
        _ = await AppliedAsync("reapply-train", train).ConfigureAwait(false);

        // Slaughter, twice.
        foreach (var key in new[] { "apply-slaughter", "reapply-slaughter" })
        {
            var effect = await AppliedAsync(key, Order(herd.Cow, Operations.HusbandryOrder.Slaughter)).ConfigureAwait(false);
            Want(effect.SlaughterDesignated == true, $"{key}: want a slaughter designation", effect);
        }

        cow = await AnimalAsync("cow-after-slaughter", herd.Cow).ConfigureAwait(false);
        Want(cow.Slaughter, "cow-after-slaughter: want the designation observable", cow);

        // Tame: the factionless muffalo is only in the includeWild read and the wild census.
        Want(await FindAsync("wild-plain-read", herd.Wild, false).ConfigureAwait(false) is null, "wild-plain-read: a factionless animal is in the player herd read", herd.Wild);
        var wild = await AnimalAsync("wild-before", herd.Wild, includeWild: true).ConfigureAwait(false);
        Want(wild is { Tameable: true } && wild.Tame != true, "wild-before: want a tameable, undesignated muffalo", wild);
        var factsReply = await s.CallAsync("colony-facts-wild", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var upkeep = TypedReads.Observed("colony-facts-wild", NativeOps.ReadColonyFacts, factsReply, factsReply.Outcome?.Observed).Upkeep?.Outcome?.Observed
            ?? throw new AcceptanceException("colony-facts-wild: the upkeep section is unavailable");
        Want(upkeep.WildAnimals.Any(a => a.Pawn?.Id == herd.Wild) && !upkeep.Animals.Any(a => a.Pawn?.Id == herd.Wild), "colony-facts-wild: want the muffalo in the wild census only", herd.Wild);

        await RefusedAsync("tame-player-animal", Order(herd.Cow, Operations.HusbandryOrder.Tame), Common.FailureCode.NotFound).ConfigureAwait(false);
        foreach (var key in new[] { "apply-tame", "reapply-tame" })
        {
            var effect = await AppliedAsync(key, Order(herd.Wild, Operations.HusbandryOrder.Tame)).ConfigureAwait(false);
            Want(effect.TameDesignated == true, $"{key}: want a tame designation", effect);
        }

        wild = await AnimalAsync("wild-after-tame", herd.Wild, includeWild: true).ConfigureAwait(false);
        Want(wild is { Tame: true } && wild.Tameable != true, "wild-after-tame: want the designation observable and no longer a candidate", wild);

        // Release on the unbonded, unmastered father.
        var father = await AnimalAsync("father-before", herd.Father).ConfigureAwait(false);
        Want(father.SafeToRelease == true, "father-before: want safe to release", father);
        var released = await AppliedAsync("apply-release", Order(herd.Father, Operations.HusbandryOrder.Release)).ConfigureAwait(false);
        Want(released.ReleaseDesignated == true, "apply-release: want a release designation", released);
        father = await AnimalAsync("father-after-release", herd.Father).ConfigureAwait(false);
        Want(father.Release, "father-after-release: want the designation observable", father);

        // Animals-tab settings on the obedient guard; the untrained dog refuses master and following, a missing area refuses.
        var guard = await AnimalAsync("guard-before", herd.Guard).ConfigureAwait(false);
        Want(guard is { Obedient: true, SupportsAllowedAreas: true } && string.IsNullOrEmpty(guard.AllowedAreaId) && string.IsNullOrEmpty(guard.MasterId), "guard-before: want an obedient guard with no area or master", guard);
        await RefusedAsync("master-untrained", Order(herd.Dog, Operations.HusbandryOrder.Master) with { TargetId = herd.Handler }, Common.FailureCode.InvalidRequest).ConfigureAwait(false);
        await RefusedAsync("follow-untrained", Order(herd.Dog, Operations.HusbandryOrder.FollowDrafted) with { Follow = true }, Common.FailureCode.InvalidRequest).ConfigureAwait(false);
        await RefusedAsync("area-missing", Order(herd.Guard, Operations.HusbandryOrder.AllowedArea) with { TargetId = "Area_Allowed_999999" }, Common.FailureCode.NotFound).ConfigureAwait(false);

        var area = await AppliedAsync("apply-area", Order(herd.Guard, Operations.HusbandryOrder.AllowedArea) with { TargetId = herd.Area }).ConfigureAwait(false);
        Want(area.AllowedAreaId == herd.Area, "apply-area: want the area in the effect", area);
        guard = await AnimalAsync("guard-after-area", herd.Guard).ConfigureAwait(false);
        Want(guard.AllowedAreaId == herd.Area, "guard-after-area: want the area observable", guard);
        var cleared = await AppliedAsync("apply-area-clear", Order(herd.Guard, Operations.HusbandryOrder.AllowedArea)).ConfigureAwait(false);
        Want(cleared.AllowedAreaId is "", "apply-area-clear: want an empty area in the effect", cleared);
        guard = await AnimalAsync("guard-after-area-clear", herd.Guard).ConfigureAwait(false);
        Want(string.IsNullOrEmpty(guard.AllowedAreaId), "guard-after-area-clear: want no area", guard);

        var master = await AppliedAsync("apply-master", Order(herd.Guard, Operations.HusbandryOrder.Master) with { TargetId = herd.Handler }).ConfigureAwait(false);
        Want(master.MasterId == herd.Handler, "apply-master: want the handler in the effect", master);
        guard = await AnimalAsync("guard-after-master", herd.Guard).ConfigureAwait(false);
        Want(guard.MasterId == herd.Handler, "guard-after-master: want the master observable", guard);

        // Each follow flag is written on its own and leaves the other.
        var drafted = await AppliedAsync("apply-follow-drafted", Order(herd.Guard, Operations.HusbandryOrder.FollowDrafted) with { Follow = true }).ConfigureAwait(false);
        Want(drafted.FollowDrafted == true, "apply-follow-drafted: want followDrafted in the effect", drafted);
        guard = await AnimalAsync("guard-after-follow-drafted", herd.Guard).ConfigureAwait(false);
        Want(guard.FollowDrafted == true && guard.FollowFieldwork != true, "guard-after-follow-drafted: want followDrafted only", guard);
        var fieldwork = await AppliedAsync("apply-follow-fieldwork", Order(herd.Guard, Operations.HusbandryOrder.FollowFieldwork) with { Follow = true }).ConfigureAwait(false);
        Want(fieldwork.FollowFieldwork == true, "apply-follow-fieldwork: want followFieldwork in the effect", fieldwork);
        guard = await AnimalAsync("guard-after-follow-fieldwork", herd.Guard).ConfigureAwait(false);
        Want(guard is { FollowDrafted: true, FollowFieldwork: true }, "guard-after-follow-fieldwork: want both flags", guard);

        // The standing removals cancel, read back, and cancel again as they stand.
        foreach (var (id, order, slaughter) in new[] { (herd.Cow, Operations.HusbandryOrder.CancelSlaughter, true), (herd.Father, Operations.HusbandryOrder.CancelRelease, false) })
        {
            foreach (var key in new[] { $"cancel-{order}", $"recancel-{order}" })
            {
                var effect = await AppliedAsync(key, Order(id, order)).ConfigureAwait(false);
                Want((slaughter ? effect.SlaughterDesignated : effect.ReleaseDesignated) == false, $"{key}: want the designation cleared in the effect", effect);
            }

            var animal = await AnimalAsync($"after-{order}", id).ConfigureAwait(false);
            Want(!(slaughter ? animal.Slaughter : animal.Release), $"after-{order}: want the designation gone", animal);
        }
    }
}
