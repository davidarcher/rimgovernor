package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// PersonalShareOf is the colonist's personal wealth share (#1846): the one
// accessor the bedroom, gear and surgery gates read to ask whether their
// remaining share covers an upgrade (PersonalShare.Allows). A colonist the
// projection holds no share for (no routine reading, an unread roster, a
// slave or prisoner) gets policy.UnknownPersonalShare: necessities only.
func (r ColonyProjection) PersonalShareOf(pawn policy.PawnID) policy.PersonalShare {
	if s, ok := r.PersonalShares[pawn]; ok {
		return s
	}
	return policy.UnknownPersonalShare()
}

// personalShares fills p.PersonalShares from the reading's wealth, roster,
// gear and sleeping census (#1846). Soldiers are the pawns whose gear role
// derives to soldier; doctors are policy.ShareDoctors. Spent is unknown for
// everyone while the sleeping census is, and for a colonist whose worn gear
// or equipped weapon was not read. Nothing here calls native.
func personalShares(p *ColonyProjection, frame bridge.RoutineFrame, pawns *o.PawnSnapshot) {
	work, known := p.WorkPawns.Value()
	if !known || pawns == nil {
		return
	}
	profiles := policy.Profiles(work)
	doctors := policy.ShareDoctors(profiles)
	soldiers := map[policy.PawnID]bool{}
	worn := map[policy.PawnID]domain.Fact[[]policy.PersonalItem]{}
	if gear, ok := p.Facts.Gear.Value(); ok {
		for _, g := range gear.Pawns {
			if model, ok := g.LoadoutModel.Value(); ok {
				soldiers[g.Pawn] = policy.DeriveGearRole(model.Role) == policy.GearSoldier
				items := make([]policy.PersonalItem, 0, len(model.Worn))
				for _, w := range model.Worn {
					items = append(items, policy.PersonalItemOf(w))
				}
				worn[g.Pawn] = domain.Known(items)
			} else if state, ok := g.Policy.Value(); ok {
				soldiers[g.Pawn] = policy.DeriveGearRole(state.Role) == policy.GearSoldier
			}
		}
	}
	sleeping, sleepingKnown := p.Facts.Sleeping.Value()
	slaves := map[policy.PawnID]bool{}
	for _, s := range sleeping.Slaves {
		slaves[s.ID] = true
	}
	rows := map[string]*o.PawnState{}
	for _, row := range pawns.Pawns {
		rows[row.GetPawn().GetId()] = row
	}
	parts := map[policy.PawnID]domain.Fact[[]policy.InstalledPart]{}
	if care, ok := p.Facts.MedicalPawns.Value(); ok {
		for _, c := range care {
			parts[c.ID] = c.InstalledParts
		}
	}
	spendPawns := make([]policy.PersonalSpendPawn, 0, len(profiles))
	members := make([]policy.ShareMember, 0, len(profiles))
	for _, profile := range profiles {
		free := !slaves[profile.ID]
		gear := domain.Unknown[[]policy.PersonalItem]()
		if items, ok := worn[profile.ID].Value(); ok {
			if weapon, has, read := equippedWeapon(rows[string(profile.ID)], frame.Tables, frame.Catalog); read {
				if has {
					items = append(items, weapon)
				}
				gear = domain.Known(items)
			}
		}
		installed, partsRead := parts[profile.ID].Value()
		spendPawns = append(spendPawns, policy.PersonalSpendPawn{ID: profile.ID, Free: free, Gear: gear, Parts: installed, PartsUnread: !partsRead})
		members = append(members, policy.ShareMember{Profile: profile, Free: free, Soldier: soldiers[profile.ID], Doctor: doctors[profile.ID], Spent: domain.Unknown[float64]()})
	}
	if sleepingKnown {
		spent := policy.PersonalSpent(policy.PersonalSpendInput{Sleeping: sleeping, BedPrice: marketBedPrice(frame.Catalog), PartPrice: partPrice(p.Facts.Items), Pawns: spendPawns})
		for i, m := range members {
			if s, ok := spent[m.Profile.ID]; ok {
				members[i].Spent = s
			}
		}
	}
	p.PersonalShares = policy.PersonalShares(p.Facts.PersonalPool(), members)
}

// partPrice adapts ItemFacts.MarketValue to the (price, priced) lookup
// PersonalSpent takes for installed part items; a def with no catalog value is
// unpriced.
func partPrice(items policy.ItemFacts) func(policy.Resource) (float64, bool) {
	return func(item policy.Resource) (float64, bool) {
		v, err := items.MarketValue(item)
		return v, err == nil
	}
}

// marketBedPrice prices a bed by (def, stuff) from the catalog's MarketValue
// stat rows; nil without a catalog, which leaves every bed unpriced.
func marketBedPrice(catalog *bridge.DefinitionCatalog) policy.BedPrice {
	if catalog == nil {
		return nil
	}
	return func(def, stuff policy.Resource) (float64, bool) {
		v, err := optionStat(catalog, string(def), string(stuff), statMarketValue, true)
		return v, err == nil
	}
}

// equippedWeapon is the pawn's equipped primary weapon as a personal item,
// priced from the catalog's MarketValue row for its (def, stuff). read is
// false when the equipment census was not read cleanly or the weapon cannot be
// priced; has is false for an unarmed pawn. An unobserved quality reads as
// Normal, as for the armory.
func equippedWeapon(row *o.PawnState, tables bridge.Tables, catalog *bridge.DefinitionCatalog) (item policy.PersonalItem, has, read bool) {
	e := row.GetEquipment()
	if e == nil || e.Armed == nil || len(e.Issues) > 0 {
		return item, false, false
	}
	if !e.GetArmed() {
		return item, false, true
	}
	if catalog == nil {
		return item, false, false
	}
	for _, g := range e.Equipped {
		if g.GetThing().GetId() == "" || g.GetThing().GetId() != e.GetPrimaryId() {
			continue
		}
		thing, ok := tables.Things.Row(g.GetThing())
		def := thing.GetThing().GetDefName()
		if !ok || def == "" || g.ConditionFraction == nil {
			return item, false, false
		}
		quality := 2
		if q := g.GetQuality(); q != o.Quality_QUALITY_UNSPECIFIED {
			quality = int(q) - 1
		}
		base, err := optionStat(catalog, def, g.GetStuff(), statMarketValue, true)
		if err != nil {
			return item, false, false
		}
		return policy.PersonalItem{Definition: policy.Resource(def), Stuff: policy.Resource(g.GetStuff()), Quality: quality, Condition: g.GetConditionFraction(), Base: domain.Known(base)}, true, true
	}
	return item, false, false
}
