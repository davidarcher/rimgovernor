package policy

import (
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Acquiring animals for food: a wild tameable animal and a trader's
// live animal are one-animal candidates beside slaughter. Both yield what the
// animal would give once it is the colony's, net of its feed: milk or eggs per
// day after the lead (the race's first milkable or reproductive stage), else
// its meat as a one-shot stock once adult. The husbandry planners and the
// trade planner stay the executors; they act on the entries the plan opens.

// animalInteractionTalks is the talk toils of one taming interaction: game
// code structure (JobDriver_InteractAnimal), not a value the game exposes.
const animalInteractionTalks = 3

// AnimalAcquisition is the census the acquisition candidates price from.
type AnimalAcquisition struct {
	Races       AnimalRaceCatalog
	Owned, Wild domain.Fact[[]UpkeepAnimal]
	Herd        HerdPolicy
	Handlers    domain.Fact[[]PawnProfile]
}

// acquiredYield is what one acquired animal yields: a net rate or a stock.
type acquiredYield struct {
	nutritionPerDay domain.Fact[float64]
	stock           domain.Fact[int64]
	leadDays        float64
	feed            float64
}

// room is whether the herd can take another animal of the race: not retired
// and below its ceiling when it has one.
func (in AnimalAcquisition) room(race Resource) bool {
	if in.Herd.Retired[race] {
		return false
	}
	ceiling, capped := in.Herd.PopulationMax[race]
	if !capped {
		return true
	}
	owned, _ := in.Owned.Value()
	count := int64(0)
	for _, a := range owned {
		if release, rk := a.Release.Value(); a.Definition == race && rk && !release {
			count++
		}
	}
	return count < ceiling
}

// yield prices one animal of race and gender, ageTicks old: the milk and eggs
// it gives net of feed, else its meat. Not ok when any fact it needs is unread.
func (in AnimalAcquisition) yield(race AnimalRace, gender string, ageTicks float64) (acquiredYield, bool) {
	feed, ok := race.AdultFeedPerDay.Value()
	if !ok || !foodNumber(feed) {
		return acquiredYield{}, false
	}
	lead := func(stage domain.Fact[int64]) (float64, bool) {
		ticks, known := stage.Value()
		return math.Max(0, (float64(ticks)-ageTicks)/domain.TicksPerDay), known
	}
	rate, productLead, products := 0.0, 0.0, false
	for _, p := range race.Products {
		stage := race.MilkableMinAgeTicks
		switch {
		case p.Kind == "eggs":
			if stage = race.ReproductiveMinAgeTicks; !known(stage) {
				stage = race.AdultMinAgeTicks
			}
		case p.Kind != "milk":
			continue
		}
		amount, ak := p.Amount.Value()
		interval, ik := p.IntervalDays.Value()
		nutrition, nk := p.NutritionPerUnit.Value()
		days, sk := lead(stage)
		if !sk {
			continue
		}
		if !ak || !ik || !nk || interval <= 0 {
			return acquiredYield{}, false
		}
		if gender != "Female" && (p.Kind == "milk" || p.FemaleOnly) {
			continue
		}
		rate += amount / interval * nutrition
		productLead, products = math.Max(productLead, days), true
	}
	if products && rate > feed {
		return acquiredYield{nutritionPerDay: domain.Known(rate - feed), stock: domain.Unknown[int64](), leadDays: productLead, feed: feed}, true
	}
	amount, mk := race.MeatAmount.Value()
	per, pk := race.MeatNutritionPerUnit.Value()
	days, ak := lead(race.AdultMinAgeTicks)
	if !mk || !pk || !ak {
		return acquiredYield{}, false
	}
	// The animal eats until it is slaughtered adult.
	meat := math.Floor(amount*per - feed*days)
	if meat < 1 {
		return acquiredYield{}, false
	}
	return acquiredYield{stock: domain.Known(int64(meat)), leadDays: days, feed: feed}, true
}

func (y acquiredYield) channel(kind CandidateKind, id string, upfront float64, terms []CandidateTerm) SupplyCandidate {
	c := FoodCandidate(kind, id, y.nutritionPerDay)
	c.Yields[0].StockCap = y.stock
	c.LaborPerDay, c.UpfrontCost.LaborTicks, c.LeadDays, c.State = domain.Known(0.0), domain.Known(upfront), domain.Known(y.leadDays), FoodState(domain.Known(false), domain.Unknown[bool]())
	c.Terms = append([]CandidateTerm{{Name: "feed_per_day", Value: y.feed}}, terms...)
	return c
}

// value ranks acquisitions of equal kind: yield (a rate, else a stock) per
// unit of upfront work.
func acquisitionValue(c SupplyCandidate) float64 {
	n, _ := c.Nutrition().PerDay.Value()
	s, _ := c.Nutrition().StockCap.Value()
	u, _ := c.UpfrontCost.LaborTicks.Value()
	return (n + float64(s)) / math.Max(1, u)
}

// bestAcquisition keeps the best rate candidate and the best one-shot stock
// candidate: the two are not comparable, the plan chooses between them.
func bestAcquisition(channels []SupplyCandidate) []SupplyCandidate {
	sort.Slice(channels, func(i, j int) bool {
		if a, b := acquisitionValue(channels[i]), acquisitionValue(channels[j]); a != b {
			return a > b
		}
		return channels[i].ID < channels[j].ID
	})
	var out []SupplyCandidate
	rate, stock := false, false
	for _, c := range channels {
		if _, isRate := c.Nutrition().PerDay.Value(); isRate && !rate {
			out, rate = append(out, c), true
		} else if !isRate && !stock {
			out, stock = append(out, c), true
		}
	}
	return out
}

// TameFoodChannels offers the best wild animals to tame (one for a rate, one for a stock): one a handler skilled
// enough can tame, tameable and not designated, of a race that is not
// dangerous, with herd room and feed. Its upfront work is the expected taming
// attempts (1 over the race's tame chance factor, the tamer's own chance
// unread) of three talks and the feeds each, its lead the time until the
// animal reaches the stage its yield needs, and failing an attempt risks the
// race's manhunter chance.
func TameFoodChannels(in AnimalAcquisition) []SupplyCandidate {
	wild, wk := in.Wild.Value()
	profiles, pk := in.Handlers.Value()
	if !wk || !pk || in.Herd.FeedShort {
		return nil
	}
	talk, tk := in.Races.Interaction.TalkTicks.Value()
	feedTicks, fk := in.Races.Interaction.FeedTicks.Value()
	feeds, sk := in.Races.Interaction.Feeds.Value()
	if !tk || !fk || !sk {
		return nil
	}
	var out []SupplyCandidate
	for _, a := range wild {
		tameable, tk := a.Tameable.Value()
		designated, dk := a.Tame.Value()
		race, rk := in.Races.Race(a.Definition)
		factor, ck := race.TameChanceFactor.Value()
		years, yk := a.Herd.AgeYears.Value()
		minimum, _ := a.MinimumHandlingSkill.Value()
		if !tk || !tameable || !dk || designated || !rk || !ck || !(factor > 0) || !yk || herdDangerous(a) || !in.room(a.Definition) {
			continue
		}
		if _, ok := TamerFor(profiles, minimum); !ok {
			continue
		}
		y, ok := in.yield(race, a.Gender, years*domain.DaysPerYear*domain.TicksPerDay)
		if !ok {
			continue
		}
		attempts := 1 / math.Min(1, factor)
		manhunter, _ := race.ManhunterOnTameFail.Value()
		c := y.channel(CandidateTame, "tame:"+string(a.ID), attempts*float64(animalInteractionTalks*talk+feeds*feedTicks),
			[]CandidateTerm{{Name: "expected_attempts", Value: attempts}, {Name: "talk_toils_per_attempt", Value: animalInteractionTalks}})
		if risk := math.Min(1, manhunter*(attempts-1)); risk > 0 {
			c.Risk = []CandidateRisk{{Kind: CandidateRevenge, Weight: risk}}
		}
		out = append(out, c)
	}
	return bestAcquisition(out)
}

// AnimalPurchaseFoodChannels offers, per trader the colony has recorded, the
// best live animal it sells: affordable above the reserve, of a race with herd
// room, a known sex and yield. The silver is the upfront work (as trade food),
// the lead zero: a trader's animal is read as adult, its age is not on the
// sheet. The ID names the trader, race and sex the trade planner buys.
func AnimalPurchaseFoodChannels(offers []TradeOffers, in AnimalAcquisition, silver, reserve int64) []SupplyCandidate {
	var out []SupplyCandidate
	for _, record := range offers {
		var perTrader []SupplyCandidate
		for _, row := range record.Rows {
			race, ok := in.Races.Race(Resource(row.Def))
			if !row.Pawn || !ok || row.Count < 1 || row.Gender == "" || strings.Contains(row.Def, "/") || !finite(row.Price) || row.Price <= 0 || row.Price > tradeBuyPriceCeiling || row.Price > float64(silver-reserve) || !in.room(race.Def) {
				continue
			}
			y, ok := in.yield(race, row.Gender, math.Inf(1))
			if !ok {
				continue
			}
			perTrader = append(perTrader, y.channel(CandidateAnimalBuy, record.Trader+"/"+row.Def+"/"+row.Gender, row.Price*tradeLaborPerSilver,
				[]CandidateTerm{{Name: "trade_silver", Value: row.Price}}))
		}
		out = append(out, bestAcquisition(perTrader)...)
	}
	return out
}

// FoodTameChoice is the wild animal the plan opened to tame, while a handler
// skilled enough exists and the herd's feed is known not short.
func FoodTameChoice(plan domain.Fact[FoodPlan], wild domain.Fact[[]UpkeepAnimal], feedShort domain.Fact[bool], handlers domain.Fact[[]PawnProfile]) HusbandryChoice {
	p, pk := plan.Value()
	rows, rk := wild.Value()
	short, fk := feedShort.Value()
	profiles, hk := handlers.Value()
	if !pk || !rk || !fk || !hk {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	if short {
		return HusbandryChoice{Reason: HusbandryNoDeficit}
	}
	for _, e := range p.Portfolio {
		if e.Channel.Kind != CandidateTame || e.Decision != FoodPlanOpen {
			continue
		}
		for _, a := range rows {
			minimum, _ := a.MinimumHandlingSkill.Value()
			if _, ok := TamerFor(profiles, minimum); ok && e.Channel.ID == "tame:"+string(a.ID) {
				if designated, dk := a.Tame.Value(); dk && !designated {
					return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryTame}
				}
			}
		}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}

// PlannedAnimalPurchases are the animals the plan opened to buy from trader,
// as the wants SelectAnimalPurchase fills.
func PlannedAnimalPurchases(plan domain.Fact[FoodPlan], trader string) []HerdWant {
	p, known := plan.Value()
	if !known {
		return nil
	}
	var out []HerdWant
	for _, e := range p.Portfolio {
		rest, ok := strings.CutPrefix(e.Channel.ID, trader+"/")
		race, gender, split := strings.Cut(rest, "/")
		if e.Channel.Kind == CandidateAnimalBuy && e.Decision == FoodPlanOpen && ok && split {
			out = append(out, HerdWant{Race: Resource(race), Male: gender == "Male", Female: gender == "Female"})
		}
	}
	return out
}
