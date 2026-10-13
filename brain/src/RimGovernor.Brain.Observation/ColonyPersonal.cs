namespace RimGovernor.Brain.Observation;

public sealed partial record ColonyProjection
{
    /// <summary>
    /// The colonist's personal wealth share: the one accessor the bedroom, gear and surgery gates read to ask whether
    /// their remaining share covers an upgrade. A colonist the projection holds no share for (no routine reading, an
    /// unread roster, a slave or prisoner) gets UnknownPersonalShare: necessities only.
    /// </summary>
    public PersonalShare PersonalShareOf(PawnId pawn) =>
        Facts.PersonalShares is { } shares && shares.TryGetValue(pawn, out var s) ? s : PersonalShare.UnknownPersonalShare();

    /// <summary>
    /// The surgery ranking context over the projection: the hospital bed, the elective gate (PersonalShareOf) and
    /// the work profiles. The surgery review and the part-demand path read the same one.
    /// </summary>
    public SurgeryContext SurgeryContext()
    {
        var ctx = new SurgeryContext { Elective = new ElectiveShare { Items = Facts.Items, Of = PersonalShareOf } };
        if (PolicyFunctions.HospitalBedReady(Facts.Sleeping).TryGetValue(out var bed) && bed)
        {
            ctx = ctx with { HospitalBed = true };
        }
        if (WorkPawns.TryGetValue(out var pawns))
        {
            ctx = ctx with { Profiles = PolicyFunctions.Profiles(pawns) };
        }
        return ctx;
    }
}

public static partial class ObservationPackage
{
    /// <summary>
    /// Fills p.Facts.PersonalShares from the reading's wealth, roster, gear and sleeping census. Soldiers are the
    /// pawns whose gear role derives to soldier; doctors are ShareDoctors. Spent is unknown for everyone while the
    /// sleeping census is, and for a colonist whose worn gear or equipped weapon was not read. Nothing here calls
    /// native.
    /// </summary>
    internal static ColonyProjection PersonalShares(ColonyProjection p, RoundsFrame frame, DefinitionCatalog? catalog, O.PawnSnapshot? pawns)
    {
        // A live reading always sets the map, so an empty one gates every colonist.
        p = p with { Facts = p.Facts with { PersonalShares = new Dictionary<PawnId, PersonalShare>() } };
        if (!p.WorkPawns.TryGetValue(out var work) || pawns is null)
        {
            return p;
        }
        var profiles = PolicyFunctions.Profiles(work);
        var doctors = PolicyFunctions.ShareDoctors(profiles);
        var soldiers = new Dictionary<PawnId, bool>();
        var worn = new Dictionary<PawnId, Fact<EquatableArray<PersonalItem>>>();
        if (p.Facts.Gear.TryGetValue(out var gear))
        {
            foreach (var g in gear.Pawns)
            {
                if (g.LoadoutModel.TryGetValue(out var model))
                {
                    soldiers[g.Pawn] = PolicyFunctions.DeriveGearRole(model.Role) == GearRole.Soldier;
                    var items = new List<PersonalItem>(model.Worn.Count);
                    foreach (var w in model.Worn)
                    {
                        items.Add(PolicyFunctions.PersonalItemOf(w));
                    }
                    worn[g.Pawn] = Known(EquatableArray.From(items));
                }
                else if (g.Policy.TryGetValue(out var state))
                {
                    soldiers[g.Pawn] = PolicyFunctions.DeriveGearRole(state.Role) == GearRole.Soldier;
                }
            }
        }
        var sleepingKnown = p.Facts.Sleeping.TryGetValue(out var sleeping);
        var slaves = new HashSet<PawnId>();
        if (sleeping is not null)
        {
            foreach (var s in sleeping.Slaves)
            {
                slaves.Add(s.Id);
            }
        }
        var rows = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
        foreach (var row in pawns.Pawns)
        {
            rows[row.Pawn?.Id ?? ""] = row;
        }
        var parts = new Dictionary<PawnId, Fact<EquatableArray<InstalledPart>>>();
        if (p.Facts.MedicalPawns.TryGetValue(out var care))
        {
            foreach (var c in care)
            {
                parts[c.Id] = c.InstalledParts;
            }
        }
        var spendPawns = new List<PersonalSpendPawn>(profiles.Count);
        var members = new List<ShareMember>(profiles.Count);
        foreach (var profile in profiles)
        {
            var free = !slaves.Contains(profile.Id);
            var pawnGear = Unknown<EquatableArray<PersonalItem>>();
            if (worn.TryGetValue(profile.Id, out var wornFact) && wornFact.TryGetValue(out var items))
            {
                rows.TryGetValue(profile.Id.Value, out var pawnRow);
                if (EquippedWeapon(pawnRow, frame.Tables, catalog, out var weapon, out var has))
                {
                    var all = new List<PersonalItem>(items);
                    if (has)
                    {
                        all.Add(weapon);
                    }
                    pawnGear = Known(EquatableArray.From(all));
                }
            }
            parts.TryGetValue(profile.Id, out var partsFact);
            var partsRead = partsFact.TryGetValue(out var installed);
            spendPawns.Add(new PersonalSpendPawn { Id = profile.Id, Free = free, Gear = pawnGear, Parts = partsRead ? installed : default, PartsUnread = !partsRead });
            members.Add(new ShareMember { Profile = profile, Free = free, Soldier = soldiers.GetValueOrDefault(profile.Id), Doctor = doctors.GetValueOrDefault(profile.Id), Spent = Unknown<double>() });
        }
        if (sleeping is not null)
        {
            var spent = PolicyFunctions.PersonalSpent(new PersonalSpendInput { Sleeping = sleeping, BedPrice = MarketBedPrice(catalog), PartPrice = PartPrice(p.Facts.Items), Pawns = spendPawns });
            for (var i = 0; i < members.Count; i++)
            {
                if (spent.TryGetValue(members[i].Profile.Id, out var s))
                {
                    members[i] = members[i] with { Spent = s };
                }
            }
        }
        return p with { Facts = p.Facts with { PersonalShares = PolicyFunctions.PersonalShares(p.Facts.PersonalPoolOf(), members) } };
    }

    /// <summary>Adapts ItemFacts.MarketValue to the (price, priced) lookup PersonalSpent takes for installed part items; a def with no catalog value is unpriced.</summary>
    private static Func<Resource, (double Price, bool Priced)> PartPrice(ItemFacts items) =>
        item =>
        {
            try
            {
                return (items.MarketValue(item), true);
            }
            catch (DomainException)
            {
                return (0, false);
            }
        };

    /// <summary>Prices a bed by (def, stuff) from the catalog's MarketValue stat rows; null without a catalog, which leaves every bed unpriced.</summary>
    private static BedPrice? MarketBedPrice(DefinitionCatalog? catalog)
    {
        if (catalog is null)
        {
            return null;
        }
        return (def, stuff) =>
        {
            try
            {
                return (OptionStat(catalog, def.Value, stuff.Value, StatMarketValue, true), true);
            }
            catch (DomainException)
            {
                return (0, false);
            }
        };
    }

    /// <summary>
    /// The pawn's equipped primary weapon as a personal item, priced from the catalog's MarketValue row for its
    /// (def, stuff). Returns false when the equipment census was not read cleanly or the weapon cannot be priced;
    /// has is false for an unarmed pawn. An unobserved quality reads as Normal, as for the armory.
    /// </summary>
    private static bool EquippedWeapon(O.PawnState? row, Tables tables, DefinitionCatalog? catalog, out PersonalItem item, out bool has)
    {
        item = default!;
        has = false;
        var e = row?.Equipment;
        if (e is null || e.Armed is null || e.Issues.Count > 0)
        {
            return false;
        }
        if (!e.Armed.GetValueOrDefault())
        {
            return true;
        }
        if (catalog is null)
        {
            return false;
        }
        foreach (var g in e.Equipped)
        {
            var id = g.Thing?.Id ?? "";
            if (id.Length == 0 || id != (e.PrimaryId ?? ""))
            {
                continue;
            }
            var ok = tables.Things.TryGetRow(g.Thing, out var thing);
            var def = thing?.Entity?.DefName ?? "";
            if (!ok || def.Length == 0 || g.ConditionFraction is null)
            {
                return false;
            }
            var quality = g.Quality is not null ? (int)g.Quality.GetValueOrDefault() : 2;
            double baseValue;
            try
            {
                baseValue = OptionStat(catalog, def, (g.Stuff ?? ""), StatMarketValue, true);
            }
            catch (DomainException)
            {
                return false;
            }
            item = new PersonalItem { Definition = new Resource(def), Stuff = new Resource((g.Stuff ?? "")), Quality = quality, Condition = g.ConditionFraction.GetValueOrDefault(), Base = Known(baseValue) };
            has = true;
            return true;
        }
        return false;
    }
}
