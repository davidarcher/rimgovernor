package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Quality price factors of the MarketValue stat (StatPart_Quality on
// MarketValueBase in Core Stats_Basics_General.xml, decompiled
// StatPart_Quality.TransformValue): a thing's value becomes
// val + min(val x factor - val, maxGain), that is min(val x factor,
// val + maxGain), indexed Awful=0 through Legendary=6. Awful and Poor lose
// value, Good and above gain up to the cap.
var (
	marketQualityFactor  = [...]float64{.5, .75, 1, 1.25, 1.5, 2.5, 5}
	marketQualityMaxGain = [...]float64{1e7, 1e7, 1e7, 500, 1000, 2000, 3000}
)

// marketHealthCurve is StatPart_Health on MarketValue (same file): the value
// factor by hit-point fraction, linear between the points and flat outside
// them. It applies to every def that uses hit points and lets health affect
// price, which covers apparel, weapons and beds.
var marketHealthCurve = [...][2]float64{{0, 0}, {.5, .1}, {.6, .5}, {.9, 1}}

// ItemMarketValue is an item's MarketValue from its value at Normal quality
// and full hit points (GearOption.Cost, the catalog's (def, stuff) row), its
// quality (Awful=0 through Legendary=6) and its hit-point fraction. Unknown
// for a quality outside the range, a condition outside 0..1 or a base that
// is not finite and non-negative (#1838).
func ItemMarketValue(base float64, quality int, condition float64) domain.Fact[float64] {
	if quality < 0 || quality >= len(marketQualityFactor) || !finiteUnit(condition) || !finite(base) || base < 0 {
		return domain.Unknown[float64]()
	}
	value := min(base*marketQualityFactor[quality], base+marketQualityMaxGain[quality])
	return domain.Known(value * marketHealth(condition))
}

func marketHealth(x float64) float64 {
	p := marketHealthCurve
	if x >= p[len(p)-1][0] {
		return p[len(p)-1][1]
	}
	for i := 1; i < len(p); i++ {
		if x <= p[i][0] {
			return p[i-1][1] + (p[i][1]-p[i-1][1])*(x-p[i-1][0])/(p[i][0]-p[i-1][0])
		}
	}
	return p[len(p)-1][1]
}

// qualityByName maps the native QualityCategory name to its index.
func qualityByName(name string) (int, bool) {
	i := slices.Index([]string{"Awful", "Poor", "Normal", "Good", "Excellent", "Masterwork", "Legendary"}, name)
	return i, i >= 0
}

// PersonalItem is one thing a colonist carries on themselves: a worn garment
// or the equipped weapon. Base is its MarketValue at Normal quality and full
// hit points (the catalog row for its def and stuff); Condition is its
// hit-point fraction.
type PersonalItem struct {
	Definition, Stuff Resource
	Quality           int
	Condition         float64
	Base              domain.Fact[float64]
}

// PersonalItemOf is a loadout option as a carried item, priced from the
// option's Cost.
func PersonalItemOf(o GearOption) PersonalItem {
	return PersonalItem{Definition: o.Definition, Stuff: o.Stuff, Quality: o.Quality, Condition: o.Condition, Base: domain.Known(o.Cost)}
}

// PersonalSpendPawn is one colonist's carried gear. Gear is unknown until
// the loadout and equipment are read.
type PersonalSpendPawn struct {
	ID PawnID
	// Free is a free colonist; slaves and prisoners are never charged.
	Free bool
	Gear domain.Fact[[]PersonalItem]
}

// BedPrice is a bed definition's MarketValue at Normal quality and full hit
// points for the given stuff (the catalog's (def, stuff) row); false when the
// catalog has none. ItemFacts.Market is per def only and so cannot stand in
// for a stuffed bed.
type BedPrice func(def, stuff Resource) (float64, bool)

// PersonalSpendInput is everything the derived spent reads.
type PersonalSpendInput struct {
	Sleeping SleepingObservation
	BedPrice BedPrice
	Pawns    []PersonalSpendPawn
}

// spendSum accumulates a spent; one unknown part makes the whole unknown.
type spendSum struct {
	total   float64
	unknown bool
}

func (s *spendSum) add(f domain.Fact[float64], share float64) {
	if v, ok := f.Value(); ok {
		s.total += v * share
	} else {
		s.unknown = true
	}
}

func (s spendSum) fact() domain.Fact[float64] {
	if s.unknown {
		return domain.Unknown[float64]()
	}
	return domain.Known(s.total)
}

// PersonalSpent derives what each free colonist holds now (#1838): their
// share of the owned bed, of the contents of the bedroom they own, and their
// worn apparel and equipped weapon, each at its quality and condition price.
// Nothing is persisted, so a lost item frees budget. Installed parts are
// added by #1839. Keyed by free colonists only; a slave or prisoner has no
// entry, and a colonist whose bed, room or gear is unread is Unknown (never
// zero) so a gate on it refuses upgrades instead of guessing.
//
// A bedroom is one RoomQualityTargets sees: a room holding a humanlike,
// non-medical, non-prisoner bed with owners, and slave beds never charge a
// free colonist. The room's RoomStatWorker_Wealth sums every building, plant
// and floor in it, the beds included, so contents are the room Wealth less
// its beds, split evenly among the room's owners, and each bed's value is
// split among that bed's own owners: a couple pays half each, a barracks
// splits the room but each pays only the bed they own. The proxy needs no
// per-piece contract. A bed's hit points are not read, so it is priced at
// full health, which the health curve leaves at 1 down to 90%.
func PersonalSpent(in PersonalSpendInput) map[PawnID]domain.Fact[float64] {
	rooms, roomsKnown := in.Sleeping.Rooms.Value()
	wealth := map[string]float64{}
	for _, r := range rooms {
		if q, ok := r.Quality.Value(); ok && finite(q.Wealth) && q.Wealth >= 0 {
			wealth[r.ID] = q.Wealth
		}
	}
	beds := map[string]*spendSum{}
	owners := map[string][]PawnID{}
	spent := map[PawnID]*spendSum{}
	of := func(p PawnID) *spendSum {
		if spent[p] == nil {
			spent[p] = &spendSum{}
		}
		return spent[p]
	}
	for _, bed := range in.Sleeping.Beds {
		room, ok := bed.Room.Value()
		humanlike, _ := bed.Humanlike.Value()
		medical, _ := bed.Medical.Value()
		prisoners, _ := bed.Prisoners.Value()
		if !ok || room == "" || !humanlike || medical || prisoners || bed.Slaves || len(bed.Owners) == 0 {
			continue
		}
		value := in.bedValue(bed)
		if beds[room] == nil {
			beds[room] = &spendSum{}
		}
		beds[room].add(value, 1)
		for _, o := range bed.Owners {
			if !slices.Contains(owners[room], o) {
				owners[room] = append(owners[room], o)
			}
			of(o).add(value, 1/float64(len(bed.Owners)))
		}
	}
	for room, pawns := range owners {
		contents := domain.Unknown[float64]()
		w, wok := wealth[room]
		if b, bok := beds[room].fact().Value(); roomsKnown && wok && bok {
			contents = domain.Known(max(w-b, 0))
		}
		for _, o := range pawns {
			of(o).add(contents, 1/float64(len(pawns)))
		}
	}
	out := map[PawnID]domain.Fact[float64]{}
	for _, p := range in.Pawns {
		if !p.Free {
			continue
		}
		s := spendSum{}
		if held := spent[p.ID]; held != nil {
			s = *held
		}
		items, ok := p.Gear.Value()
		if !ok {
			s.unknown = true
		}
		for _, it := range items {
			base, bok := it.Base.Value()
			if !bok {
				s.unknown = true
				continue
			}
			s.add(ItemMarketValue(base, it.Quality, it.Condition), 1)
		}
		out[p.ID] = s.fact()
	}
	return out
}

func (in PersonalSpendInput) bedValue(bed SleepingBed) domain.Fact[float64] {
	quality, ok := bed.Quality.Value()
	q, known := qualityByName(quality)
	stuff, _ := bed.Stuff.Value()
	if !ok || !known || in.BedPrice == nil {
		return domain.Unknown[float64]()
	}
	base, priced := in.BedPrice(bed.Definition, Resource(stuff))
	if !priced {
		return domain.Unknown[float64]()
	}
	return ItemMarketValue(base, q, 1)
}
