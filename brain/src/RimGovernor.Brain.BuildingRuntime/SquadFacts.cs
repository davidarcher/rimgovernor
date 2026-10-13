namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Resolves pawn rows' primary weapons. A gear reference carries no def name, so a row's primary is found in
/// the frame's things table and its facts are the catalog's rows for that def.
/// </summary>
internal sealed partial class Armament
{
    internal Bridge.DefinitionCatalog? Catalog { get; init; }

    internal Things Things { get; init; } = null!;

    /// <summary>
    /// The newest frame's things table and the load's catalog from native; a source that serves neither (a
    /// test double) leaves every armed pawn's weapon unknown.
    /// </summary>
    internal static Armament Read(CancellationToken ct, object native, C.Identity identity) => ReadArmament(ct, native, identity);

    /// <summary>
    /// The weapon facts of the equipment's primary (Known false until the equipment and the things table say
    /// which weapon it is); an unarmed pawn is the known zero WeaponDef.
    /// </summary>
    internal (Pol.WeaponDef Weapon, bool Known) Primary(ObservationsPb.PawnEquipment? equipment)
    {
        if (equipment is null || IssueField(equipment.Issues, "equipped") || IssueField(equipment.Issues, "armed"))
        {
            return (new Pol.WeaponDef(), false);
        }
        return Catalog!.PrimaryWeapon(equipment, Things);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The armament Go's readArmament reads: the newest frame's things table and the load's catalog.</summary>
    internal static Armament ReadArmament(CancellationToken ct, object native, C.Identity identity) =>
        new() { Things = FrameThings(ct, native, identity), Catalog = PawnCatalog(ct, native, identity) };

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
        if (equipment is null || !equipment.HasArmed || IssueField(equipment.Issues, "equipped") || IssueField(equipment.Issues, "armed"))
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
            Dead = FactBool(row.Dead),
            Downed = FactBool(row.Downed),
        };
        if (row.HasHumanlike)
        {
            facts = facts with { Humanlike = Fact.Known(row.Humanlike) };
        }
        if (row.HasAnimal)
        {
            facts = facts with { Animal = Fact.Known(row.Animal) };
        }
        facts = facts with { Anomaly = Bridge.Pawns.PawnAnomaly(row.Anomaly), RangedEquipped = ranged };
        // A wild animal has no equipment tracker (the equipped field reads as a missing native component); it
        // carries no ranged weapon either way, and neither does an entity whose attack native read as melee only.
        if (!facts.RangedEquipped.IsKnown && (facts.Animal == Fact.Known(true) || Pol.SquadThreatFacts.MeleeEntity(facts)))
        {
            facts = facts with { RangedEquipped = Fact.Known(false) };
        }
        facts = facts with { Manhunter = ManhunterFact(row.HasMentalState ? row.MentalState : null, row.Issues) };
        if (races.Race(new Pol.Resource(row.Pawn?.DefName ?? "")) is { } race)
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
    internal static Pol.SquadDefenderFacts SquadDefenderFacts(ObservationsPb.PawnState row, IReadOnlySet<Domain.PawnId> needed, Fact<bool> ranged)
    {
        var facts = new Pol.SquadDefenderFacts
        {
            Id = new PawnId(row.Pawn?.Id ?? ""),
            Dead = FactBool(row.Dead),
            Downed = FactBool(row.Downed),
            Drafted = FactBool(row.Drafted),
            MentalState = FactPresence(row.MentalState, row.Issues, "mental_state"),
        };
        facts = facts with { DraftOwned = Fact.Known(needed.Contains(facts.Id)) };
        if (Bridge.Pawns.PawnBiotech(row.Biotech).TryGetValue(out var bt))
        {
            facts = facts with { Deathresting = bt.IsDeathresting() };
        }
        if (row.Job is { } job && !IssueField(job.Issues, "player_forced") && !IssueField(job.Issues, "queued_jobs"))
        {
            facts = facts with { PlayerForced = FactBool(job.PlayerForced), QueuedJobs = FactUint(job.QueuedJobs) };
        }
        if (row.Health is { } health && !IssueField(health.Issues, "health"))
        {
            facts = facts with { NeedsTend = FactBool(health.NeedsTend) };
            if (health.HasSummaryFraction)
            {
                facts = facts with { HealthFraction = Fact.Known(health.SummaryFraction) };
            }
        }
        if (row.Biography is { } biography && !IssueField(biography.Issues, "disabled_work_tags"))
        {
            var capable = !biography.DisabledWorkTags.Contains("Violent");
            facts = facts with { ViolenceCapable = Fact.Known(capable) };
        }
        facts = facts with { RangedEquipped = ranged, MeleeEquipped = MeleeCapable(row.Equipment) };
        if (row.Equipment is { } equipment && equipment.HasArmed && !IssueField(equipment.Issues, "armed"))
        {
            facts = facts with { Armed = Fact.Known(equipment.Armed) };
        }
        return facts;
    }
}
