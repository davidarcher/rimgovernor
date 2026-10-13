namespace RimGovernor.Brain.BuildingRuntime;

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
    /// test double) leaves every armed pawn's weapon unknown.
    /// </summary>
    internal static async Task<Armament> ReadAsync(object native, C.Identity identity, CancellationToken ct) => await ReadArmamentAsync(native, identity, ct).ConfigureAwait(false);

    /// <summary>
    /// The weapon facts of the equipment's primary (Known false until the equipment and the things table say
    /// which weapon it is); an unarmed pawn is the known zero WeaponDef.
    /// </summary>
    internal (Pol.WeaponDef Weapon, bool Known) Primary(ObservationsPb.PawnEquipment? equipment)
    {
        if (equipment is null || Boundary.IssueField(equipment.Issues, "equipped") || Boundary.IssueField(equipment.Issues, "armed"))
        {
            return (new Pol.WeaponDef(), false);
        }
        return PrimaryWeapon(equipment);
    }

    /// <summary>
    /// Go's DefinitionCatalog.PrimaryWeapon: false while the equipment does not say (no armed flag, no primary id), the
    /// primary is not among the equipped items, or the things table does not hold its row yet; an unarmed pawn is the
    /// known empty weapon.
    /// </summary>
    private (Pol.WeaponDef Weapon, bool Known) PrimaryWeapon(ObservationsPb.PawnEquipment equipment)
    {
        if (!equipment.HasArmed)
        {
            return (new Pol.WeaponDef(), false);
        }
        if (!equipment.Armed)
        {
            return (new Pol.WeaponDef(), true);
        }
        if (!equipment.HasPrimaryId || Catalog is null)
        {
            return (new Pol.WeaponDef(), false);
        }
        foreach (var item in equipment.Equipped)
        {
            if ((item.Thing?.Id ?? "") != equipment.PrimaryId)
            {
                continue;
            }
            if (item.Thing is null || !Things.TryRow(item.Thing, out var row) || (row.Thing_?.DefName ?? "").Length == 0)
            {
                return (new Pol.WeaponDef(), false);
            }
            return (Catalog.WeaponOf(row.Thing_?.DefName ?? ""), true);
        }
        return (new Pol.WeaponDef(), false);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The armament Go's readArmament reads: the newest frame's things table and the load's catalog.</summary>
    internal static async Task<Armament> ReadArmamentAsync(object native, C.Identity identity, CancellationToken ct) =>
        new() { Things = await FrameThingsOfAsync(native, identity, ct).ConfigureAwait(false), Catalog = await PawnCatalogAsync(native, identity, ct).ConfigureAwait(false) };

    /// <summary>Requires the pawn's primary equipped weapon to be found. Unarmed is a known false, never unknown.</summary>
    internal static Fact<bool> RangedWeaponEquipped(ObservationsPb.PawnEquipment? equipment, Armament arms)
    {
        var (weapon, known) = arms.Primary(equipment);
        return known ? Fact.Known(weapon.Ranged) : Fact.Unknown<bool>();
    }

    /// <summary>
    /// Whether equipment is completely known: melee needs no specific weapon, only complete information about
    /// whether one is equipped (native picks fists or an equipped weapon).
    /// </summary>
    internal static Fact<bool> MeleeCapable(ObservationsPb.PawnEquipment? equipment)
    {
        if (equipment is null || !equipment.HasArmed || Boundary.IssueField(equipment.Issues, "equipped") || Boundary.IssueField(equipment.Issues, "armed"))
        {
            return Fact.Unknown<bool>();
        }
        var known = !equipment.Armed;
        if (equipment.Armed && equipment.HasPrimaryId)
        {
            foreach (var item in equipment.Equipped)
            {
                if (item.Thing?.Id == equipment.PrimaryId)
                {
                    known = true;
                }
            }
        }
        return Fact.Known(known);
    }

    /// <summary>
    /// Populates what the shared pawn snapshot carries, including the race's body size from the catalog's race
    /// rows and the manhunter mental state exact-matched against Manhunter/ManhunterPermanent.
    /// </summary>
    internal static Pol.SquadThreatFacts SquadThreatFacts(ObservationsPb.PawnState row, Pol.AnimalRaceCatalog races, Fact<bool> ranged)
    {
        var facts = new Pol.SquadThreatFacts
        {
            Id = new PawnId(row.Pawn?.Id ?? ""),
            Dead = Boundary.FactBool(row.HasDead, row.Dead),
            Downed = Boundary.FactBool(row.HasDowned, row.Downed),
        };
        if (row.HasHumanlike)
        {
            facts = facts with { Humanlike = Fact.Known(row.Humanlike) };
        }
        if (row.HasAnimal)
        {
            facts = facts with { Animal = Fact.Known(row.Animal) };
        }
        facts = facts with { Anomaly = BridgeApi.PawnAnomaly(row.Anomaly), RangedEquipped = ranged };
        // A wild animal has no equipment tracker (the equipped field reads as a missing native component); it
        // carries no ranged weapon either way, and neither does an entity whose attack native read as melee only.
        if (!facts.RangedEquipped.IsKnown && (facts.Animal == Fact.Known(true) || Pol.PolicyFunctions.MeleeEntity(facts)))
        {
            facts = facts with { RangedEquipped = Fact.Known(false) };
        }
        facts = facts with { Manhunter = ManhunterFact(row.HasMentalState ? row.MentalState : null, row.Issues) };
        if (races.Race(new Pol.Resource(row.Pawn?.DefName ?? ""), out var race))
        {
            facts = facts with { BodySize = race.BodySize };
        }
        return facts;
    }

    /// <summary>
    /// An exact mentalState comparison rather than the looser substring match NativePawnObservationTools.cs uses
    /// for HostileReason: only the two Manhunter mental-state defNames count.
    /// </summary>
    internal static Fact<bool> ManhunterFact(string? state, IEnumerable<ObservationsPb.ReadIssue> issues)
    {
        if (state is not null)
        {
            return Fact.Known(state is "Manhunter" or "ManhunterPermanent");
        }
        foreach (var issue in issues)
        {
            if (issue.Field == "mental_state" && issue.Unavailable?.Reason == CommonPb.UnavailableReason.NotApplicable)
            {
                return Fact.Known(false);
            }
        }
        return Fact.Unknown<bool>();
    }

    /// <summary>
    /// Reads row's defender facts. <paramref name="needed"/> is plannedDrafts: a drafted pawn a live plan needs
    /// is spoken for (DraftOwned); any other drafted pawn is free to take.
    /// </summary>
    internal static Pol.SquadDefenderFacts SquadDefenderFacts(ObservationsPb.PawnState row, IReadOnlySet<Domain.PawnId> needed, Fact<bool> ranged) =>
        SquadDefenderFactsFrom(new Pol.SquadDefenderFacts(), row, needed, ranged);

    /// <summary>
    /// SquadDefenderFacts over a seed: a derived record (the shrine defender) stays its own type through the copies,
    /// which stands in for Go embedding the squad facts in it.
    /// </summary>
    internal static Pol.SquadDefenderFacts SquadDefenderFactsFrom(Pol.SquadDefenderFacts seed, ObservationsPb.PawnState row, IReadOnlySet<Domain.PawnId> needed, Fact<bool> ranged)
    {
        var facts = seed with
        {
            Id = new PawnId(row.Pawn?.Id ?? ""),
            Dead = Boundary.FactBool(row.HasDead, row.Dead),
            Downed = Boundary.FactBool(row.HasDowned, row.Downed),
            Drafted = Boundary.FactBool(row.HasDrafted, row.Drafted),
            MentalState = Boundary.FactPresence(row.HasMentalState, row.Issues, "mental_state"),
        };
        facts = facts with { DraftOwned = Fact.Known(needed.Contains(new Domain.PawnId(facts.Id.Value))) };
        if (BridgeApi.PawnBiotech(row.Biotech).TryGetValue(out var bt))
        {
            facts = facts with { Deathresting = bt.IsDeathresting() };
        }
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "player_forced") && !Boundary.IssueField(job.Issues, "queued_jobs"))
        {
            facts = facts with { PlayerForced = Boundary.FactBool(job.HasPlayerForced, job.PlayerForced), QueuedJobs = Boundary.FactUint(job.HasQueuedJobs, job.QueuedJobs) };
        }
        if (row.Health is { } health && !Boundary.IssueField(health.Issues, "health"))
        {
            facts = facts with { NeedsTend = Boundary.FactBool(health.HasNeedsTend, health.NeedsTend) };
            if (health.HasSummaryFraction)
            {
                facts = facts with { HealthFraction = Fact.Known(health.SummaryFraction) };
            }
        }
        if (row.Biography is { } biography && !Boundary.IssueField(biography.Issues, "disabled_work_tags"))
        {
            var capable = !biography.DisabledWorkTags.Contains("Violent");
            facts = facts with { ViolenceCapable = Fact.Known(capable) };
        }
        facts = facts with { RangedEquipped = ranged, MeleeEquipped = MeleeCapable(row.Equipment) };
        if (row.Equipment is { } equipment && equipment.HasArmed && !Boundary.IssueField(equipment.Issues, "armed"))
        {
            facts = facts with { Armed = Fact.Known(equipment.Armed) };
        }
        return facts;
    }
}
