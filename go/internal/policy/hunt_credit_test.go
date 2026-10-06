package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func huntCorpse(id string, rot int64) FoodStock {
	return FoodStock{ID: id, Corpse: true, Perishable: domain.Known(true), RotTicks: domain.Known(rot)}
}

func TestHuntKillCreditsPotentialYield(t *testing.T) {
	var d DeliveryCredit
	got := d.HuntDelivered([]HuntKill{{CorpseID: "c1", Potential: 20}}, nil, []FoodStock{huntCorpse("c1", 60000)}, true)
	if got != 20 {
		t.Fatalf("credit %v, want the 20 potential yield", got)
	}
}

func TestHuntCorpseCreditDecaysWithItsRotClockToZero(t *testing.T) {
	var d DeliveryCredit
	kills := []HuntKill{{CorpseID: "c1", Potential: 20}}
	want := []struct {
		rot    int64
		credit float64
	}{{60000, 20}, {30000, 10}, {15000, 5}, {0, 0}}
	for _, w := range want {
		if got := d.HuntDelivered(kills, nil, []FoodStock{huntCorpse("c1", w.rot)}, true); got != w.credit {
			t.Fatalf("rot %d: credit %v, want %v", w.rot, got, w.credit)
		}
	}
}

func TestHuntFrozenCorpseDoesNotDecay(t *testing.T) {
	var d DeliveryCredit
	kills := []HuntKill{{CorpseID: "c1", Potential: 20}}
	d.HuntDelivered(kills, nil, []FoodStock{huntCorpse("c1", 60000)}, true)
	frozen := FoodStock{ID: "c1", Corpse: true, Perishable: domain.Known(false)}
	if got := d.HuntDelivered(kills, nil, []FoodStock{frozen}, true); got != 20 {
		t.Fatalf("frozen corpse credit %v, want 20", got)
	}
}

func TestHuntButcheringReplacesTheCorpseCredit(t *testing.T) {
	var d DeliveryCredit
	kills := []HuntKill{{CorpseID: "c1", Potential: 20}}
	butchers := []HuntButcher{{CorpseID: "c1", MeatNutrition: 18}}
	// The corpse is gone from the census and its meat is credited once.
	if got := d.HuntDelivered(kills, butchers, nil, true); got != 18 {
		t.Fatalf("butchered credit %v, want the 18 meat alone", got)
	}
	// A corpse still listed beside its butcher record is not credited twice.
	if got := d.HuntDelivered(kills, butchers, []FoodStock{huntCorpse("c1", 60000)}, true); got != 18 {
		t.Fatalf("butchered corpse still listed credited %v, want 18", got)
	}
}

func TestHuntCorpseHauledTwiceIsCreditedOnce(t *testing.T) {
	var d DeliveryCredit
	kills := []HuntKill{{CorpseID: "c1", Potential: 20}, {CorpseID: "c1", Potential: 20}}
	if got := d.HuntDelivered(kills, nil, []FoodStock{huntCorpse("c1", 60000)}, true); got != 20 {
		t.Fatalf("credit %v, want one corpse's 20", got)
	}
}

// The human-butchery row stays a humanlike corpse's one credit: no kill
// record naming it earns the hunt group anything.
func TestHuntHumanlikeCorpseIsNotKillCredit(t *testing.T) {
	var d DeliveryCredit
	human := huntCorpse("h1", 60000)
	human.IsHumanlike = true
	if got := d.HuntDelivered([]HuntKill{{CorpseID: "h1", Potential: 30}}, nil, []FoodStock{human}, true); got != 0 {
		t.Fatalf("humanlike corpse credited %v as a hunt", got)
	}
}

func TestHuntMissingCorpseIsZeroOnlyWithACompleteCensus(t *testing.T) {
	var d DeliveryCredit
	kills := []HuntKill{{CorpseID: "c1", Potential: 20}}
	if got := d.HuntDelivered(kills, nil, nil, true); got != 0 {
		t.Fatalf("rotted-away corpse credited %v", got)
	}
	if got := d.HuntDelivered(kills, nil, nil, false); got != 20 {
		t.Fatalf("corpse missing from an incomplete census credited %v, want held at 20", got)
	}
}

// An unbutchered corpse rotting away takes the group's factor to zero.
func TestHuntRottingCorpseLowersTheGroupFactor(t *testing.T) {
	var d DeliveryCredit
	kills := []HuntKill{{CorpseID: "c1", Potential: 20}}
	const rotDays = 2.5
	rate := 20.0 / 3 // expects one deer's meat per three days
	channel := []CreditChannel{{Source: HuntSource, Expected: rate, CycleDays: HuntCreditWindowDays - LedgerReplantDays}}
	var last CreditResult
	for day := 0.0; day <= 10; day++ {
		rot := int64((rotDays - day) * float64(domain.TicksPerDay))
		stocks := []FoodStock{huntCorpse("c1", max(0, rot))}
		if rot <= 0 {
			stocks = nil
		}
		in := CreditInput{Tick: domain.Tick(day * float64(domain.TicksPerDay)), LoadToken: "a", Known: true,
			Delivered: map[string]float64{HuntSource: d.HuntDelivered(kills, nil, stocks, true)}}
		if day == 0 {
			in.Delivered[HuntSource] = 0
			d.Observe(in, channel)
			d.arm(d.groups[HuntSource], in.Tick)
			continue
		}
		last = d.Observe(in, channel)[HuntSource]
	}
	if last.Factor != 0 {
		t.Fatalf("factor %v after the corpse rotted away, want 0", last.Factor)
	}
	if last.State != CandidateDelivering {
		t.Fatalf("state %v, want Delivering after a kill", last.State)
	}
}

func huntKillsAt(days ...float64) []HuntKill {
	var out []HuntKill
	for _, d := range days {
		out = append(out, HuntKill{Tick: int64(d * float64(domain.TicksPerDay))})
	}
	return out
}

func TestHuntCadenceIsTheDefaultUntilThereIsHistory(t *testing.T) {
	now := int64(10 * domain.TicksPerDay)
	if got := HuntCadence(huntKillsAt(8, 9), 2, now); got != HuntKillsPerHunterDay {
		t.Fatalf("two kills: cadence %v, want the default", got)
	}
	if got := HuntCadence(huntKillsAt(4, 6, 8, 10), 0, now); got != HuntKillsPerHunterDay {
		t.Fatalf("no hunters: cadence %v, want the default", got)
	}
	// Four kills over the six days since the oldest, two hunters.
	if got := HuntCadence(huntKillsAt(4, 6, 8, 10), 2, now); math.Abs(got-4.0/(2*6)) > 1e-9 {
		t.Fatalf("cadence %v, want kills per hunter-day over the span", got)
	}
}

func deerChannel(id string, nutrition float64) SupplyCandidate {
	return huntChannel(id, []AcquisitionSource{{ID: id, Hunt: true, Food: true, NutritionYield: nutrition, Designated: true}}, false, 1, 0)
}

func isDesignated(c SupplyCandidate) bool {
	state, _ := c.State.Value()
	return state == CandidateDesignated
}

// A hunt channel is a finite source: the nutrition of its animals in reach.
func TestHuntChannelCarriesItsStock(t *testing.T) {
	c := deerChannel("deer", 80)
	if stock, known := c.Nutrition().StockCap.Value(); !known || stock != 80 {
		t.Fatalf("stock %v known %v, want 80", stock, known)
	}
	if c.Source != HuntSource || !isDesignated(c) {
		t.Fatalf("source %q designated %v", c.Source, c.State)
	}
}

// Hunters cap the throughput of the channels: two hunters kill two of the
// five 20-nutrition animals a day, so each channel keeps 2/5 of its rate and
// of its work.
func TestHuntThroughputScalesRateAndWorkToTheHunters(t *testing.T) {
	var channels []SupplyCandidate
	for _, id := range []string{"a", "b", "c", "d", "e"} {
		channels = append(channels, deerChannel(id, 20))
	}
	got := HuntThroughput(channels, 2, 1)
	for i, c := range got {
		rate, _ := c.Nutrition().PerDay.Value()
		work, _ := c.LaborPerDay.Value()
		wantWork, _ := channels[i].LaborPerDay.Value()
		if math.Abs(rate-8) > 1e-9 || math.Abs(work-wantWork*0.4) > 1e-9 {
			t.Fatalf("%s: rate %v work %v, want 8 and 0.4 of %v", c.ID, rate, work, wantWork)
		}
		if orig, _ := channels[i].Nutrition().PerDay.Value(); orig != 20 {
			t.Fatalf("input channel changed to %v", orig)
		}
	}
	if kept := HuntThroughput(channels, 5, 1); len(kept) != 5 || kept[0].Nutrition().PerDay != channels[0].Nutrition().PerDay {
		t.Fatal("hunters who out-kill the herd must leave the rates alone")
	}
	if kept := HuntThroughput(channels, 0, 1); kept[0].Nutrition().PerDay != channels[0].Nutrition().PerDay {
		t.Fatal("no known hunters must leave the rates alone")
	}
}
