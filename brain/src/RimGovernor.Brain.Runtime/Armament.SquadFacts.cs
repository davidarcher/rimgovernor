namespace RimGovernor.Brain.Runtime;

/// <summary>
/// Resolves pawn rows' primary weapons. A gear reference carries no def name, so a row's primary is found in
/// the frame's things table and its facts are the catalog's rows for that def.
/// </summary>
internal sealed partial class Armament
{
    internal Bridge.DefinitionCatalog? Catalog { get; init; }

    internal Things Things { get; init; }

    /// <summary>
    /// The newest frame's things table and the load's catalog from native; a source that serves neither (a
    /// test double) resolves only unarmed pawns.
    /// </summary>
    internal static async Task<Armament> ReadAsync(object native, RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) => await ReadArmamentAsync(native, identity, ct).ConfigureAwait(false);

    /// <summary>
    /// The weapon facts of the equipment's primary; an unarmed pawn is the zero WeaponDef. The frame's things table
    /// holds every thing its sections reference, so an armed primary it cannot resolve is a failed read.
    /// </summary>
    internal Pol.WeaponDef Primary(RimGovernor.Brain.GameFacts.Observations.PawnEquipment equipment) =>
        equipment.Armed ? Catalog?.WeaponOf(PrimaryDefName(equipment)) ?? throw new RequiredFactException("definition catalog of an armed pawn's primary") : new Pol.WeaponDef();

    /// <summary>The def name of the equipment's primary weapon, empty when the pawn is unarmed.</summary>
    internal string PrimaryDefName(RimGovernor.Brain.GameFacts.Observations.PawnEquipment equipment)
    {
        if (!equipment.Armed)
        {
            return "";
        }
        foreach (var item in equipment.Equipped)
        {
            if (item.Thing.Id == equipment.PrimaryId && Things.TryRow(item.Thing, out var row))
            {
                return row.Entity.DefName;
            }
        }
        throw new RequiredFactException($"primary weapon {equipment.PrimaryId} among the equipped things");
    }
}

partial class Armament
{
    /// <summary>The armament: the newest frame's things table and the load's catalog.</summary>
    internal static async Task<Armament> ReadArmamentAsync(object native, RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        new() { Things = await Gear.FrameThingsOfAsync(native, identity, ct).ConfigureAwait(false), Catalog = await EquipPawn.PawnCatalogAsync(native, identity, ct).ConfigureAwait(false) };

    /// <summary>
    /// The opponent facts of a pawn row: the race's base body size from the catalog, the manhunter mental state
    /// exact-matched against Manhunter/ManhunterPermanent, and melee power as MeleeDPS times summary health. An animal
    /// carries no ranged weapon, and neither does an entity whose attack native read as melee only.
    /// </summary>
    internal static Pol.SquadThreatFacts SquadThreatFacts(RimGovernor.Brain.GameFacts.Observations.PawnState row, Bridge.DefinitionCatalog catalog, bool ranged)
    {
        var facts = new Pol.SquadThreatFacts
        {
            Id = new PawnId(row.Pawn.Id),
            Dead = row.Dead,
            Downed = row.Downed,
            Humanlike = row.Humanlike,
            Animal = row.Animal,
            Anomaly = row.Anomaly,
            BodySize = catalog.BaseBodySize(row.Pawn.DefName),
            Manhunter = row.MentalState?.DefName is "Manhunter" or "ManhunterPermanent",
            MeleePower = row.Equipment.MeleeDps * row.Health.SummaryFraction,
        };
        return facts with { RangedEquipped = ranged && !facts.Animal && !Pol.CombatAnomalyRules.MeleeEntity(facts) };
    }

    /// <summary>
    /// Reads row's defender facts. <paramref name="needed"/> is plannedDrafts: a drafted pawn a live plan needs
    /// is spoken for (DraftOwned); any other drafted pawn is free to take.
    /// </summary>
    internal static Pol.SquadDefenderFacts SquadDefenderFacts(RimGovernor.Brain.GameFacts.Observations.PawnState row, IReadOnlySet<Domain.PawnId> needed, Armament arms, Pol.DefenderArmor armor) =>
        SquadDefenderFactsFrom(new Pol.SquadDefenderFacts { Armor = armor }, row, needed, arms);

    /// <summary>
    /// SquadDefenderFacts over a seed: a derived record stays its own type through the copies.
    /// </summary>
    internal static Pol.SquadDefenderFacts SquadDefenderFactsFrom(Pol.SquadDefenderFacts seed, RimGovernor.Brain.GameFacts.Observations.PawnState row, IReadOnlySet<Domain.PawnId> needed, Armament arms)
    {
        var id = new PawnId(row.Pawn.Id);
        var weapon = arms.Primary(row.Equipment);
        return seed with
        {
            Id = id,
            Dead = row.Dead,
            Downed = row.Downed,
            Drafted = row.Drafted,
            MentalState = row.MentalState is not null,
            DraftOwned = needed.Contains(id),
            Deathresting = row.Biotech is RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active { Facts: var biotech } && biotech.Deathrest is { Deathresting: true },
            PlayerForced = row.Job.PlayerForced,
            QueuedJobs = row.Job.QueuedJobs,
            NeedsTend = row.Health.NeedsTend,
            HealthFraction = row.Health.SummaryFraction,
            ViolenceCapable = !row.Biography.DisabledWorkTags.Contains("Violent"),
            RangedEquipped = weapon.Ranged,
            MeleeEquipped = weapon.Melee,
            Armed = row.Equipment.Armed,
            MeleePower = row.Equipment.MeleeDps * row.Health.SummaryFraction,
        };
    }
}
