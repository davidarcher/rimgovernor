package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func spend(resource Resource, n int64) domain.Fact[ResourceConsumption] {
	return domain.Known(ResourceConsumption{WindowDays: ResourceRateWindowDays, Recurring: map[Resource]int64{resource: n}})
}

func TestResourceRunwayRateWindow(t *testing.T) {
	// 150 steel over the 15-day window is 10 a day.
	consumption := spend("Steel", 150)
	for _, tc := range []struct {
		name       string
		stock, ore int64
		days       float64
		deficit    bool
		target     int64
	}{
		{"short", 40, 20, 4, true, 70},
		{"threshold", 40, 30, 5, false, 0},
		{"long", 100, 30, 11, false, 0},
		{"below reserve", 10, 0, 0, true, 70},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := ForecastResourceRunway("Steel", domain.Known(tc.stock), domain.Known(tc.ore), 20, 20*60000, consumption)
			if n, k := r.ConsumptionPerDay.Value(); !k || n != 10 {
				t.Fatal(r)
			}
			if d, k := r.DaysLeft.Value(); !k || d != tc.days {
				t.Fatal(r)
			}
			if d, k := r.Deficit.Value(); !k || d != tc.deficit || r.Target != tc.target || r.WindowDays != 15 {
				t.Fatal(r)
			}
		})
	}
	// Any targeted resource reads its own rate; steel's spend is not its.
	r := ForecastResourceRunway("WoodLog", domain.Known(int64(3)), domain.Known(int64(1)), 0, 0, spend("WoodLog", 15))
	if d, k := r.DaysLeft.Value(); !k || d != 4 {
		t.Fatal(r)
	}
	if n, k := ForecastResourceRunway("Silver", domain.Known(int64(3)), domain.Known(int64(1)), 0, 0, consumption).ConsumptionPerDay.Value(); !k || n != 0 {
		t.Fatal("unspent resource has a zero rate")
	}
}

func TestResourceRunwayUnknownAndZeroRate(t *testing.T) {
	day := domain.Known(ResourceConsumption{WindowDays: 1, Recurring: map[Resource]int64{}})
	r := ForecastResourceRunway("Steel", domain.Known(int64(50)), domain.Known(int64(0)), 20, 60010, day)
	if n, k := r.ConsumptionPerDay.Value(); !k || n != 0 {
		t.Fatal(r)
	}
	if _, k := r.DaysLeft.Value(); k {
		t.Fatal("zero rate has finite runway")
	}
	if d, k := r.Deficit.Value(); !k || d {
		t.Fatal(r)
	}
	ten := spend("Steel", 150)
	r = ForecastResourceRunway("Steel", domain.Known(int64(20)), domain.Unknown[int64](), 0, 0, ten)
	if d, k := r.StockDays.Value(); !k || d != 2 {
		t.Fatal(r)
	}
	if _, k := r.DaysLeft.Value(); k {
		t.Fatal("unknown ore treated as zero")
	}
	if _, k := ForecastResourceRunway("Steel", domain.Unknown[int64](), domain.Known(int64(0)), 0, 0, ten).DaysLeft.Value(); k {
		t.Fatal("unknown stock")
	}
	if _, k := ForecastResourceRunway("Steel", domain.Known(int64(1)), domain.Known(int64(0)), 0, 0, domain.Unknown[ResourceConsumption]()).ConsumptionPerDay.Value(); k {
		t.Fatal("unknown consumption has a rate")
	}
	short := domain.Known(ResourceConsumption{WindowDays: 0.5, Recurring: map[Resource]int64{"Steel": 5}})
	if _, k := ForecastResourceRunway("Steel", domain.Known(int64(1)), domain.Known(int64(0)), 0, 0, short).ConsumptionPerDay.Value(); k {
		t.Fatal("short history")
	}
}

func hour(h int, rows ...ConsumptionRow) ConsumptionHour { return ConsumptionHour{Hour: h, Rows: rows} }

func TestConsumptionLedgerWindowAndRecurringReasons(t *testing.T) {
	var l ConsumptionLedger
	if l.Since() != -1 {
		t.Fatal("empty ledger asks for the whole window")
	}
	if _, k := l.Window(5).Value(); k {
		t.Fatal("empty ledger has a window")
	}
	// Ring starts at hour 10; hours 10..59 are complete (50 hours).
	l.Merge(ConsumptionPage{CurrentHour: 60, FirstHour: 10, Hours: []ConsumptionHour{
		hour(12, ConsumptionRow{"Steel", "bill_ingredient", 30}, ConsumptionRow{"Steel", "construction", 500}, ConsumptionRow{"Steel", "sold", 9}),
		hour(50, ConsumptionRow{"Steel", "bill_ingredient", 20}, ConsumptionRow{"MedicineHerbal", "medicine_tend", 4}),
	}})
	if l.Since() != 59 {
		t.Fatal(l.Since())
	}
	c, k := l.Window(15).Value()
	if !k || c.WindowDays != 50.0/24 || c.Recurring["Steel"] != 50 || c.Recurring["MedicineHerbal"] != 4 {
		t.Fatal(c, k)
	}
	// A 1-day window sees only the last 24 hours: hour 50's spend.
	if c, _ = l.Window(1).Value(); c.WindowDays != 1 || c.Recurring["Steel"] != 20 {
		t.Fatal(c)
	}
	// The next read adds only hours after 59; the earlier ones stay.
	l.Merge(ConsumptionPage{CurrentHour: 70, FirstHour: 10, Hours: []ConsumptionHour{
		hour(65, ConsumptionRow{"Steel", "fuel_loaded", 6}, ConsumptionRow{"Steel", "fuel_loaded", -8})}})
	if l.Since() != 69 {
		t.Fatal(l.Since())
	}
	if c, _ = l.Window(15).Value(); c.Recurring["Steel"] != 48 {
		t.Fatal("a removal nets against the load", c)
	}
	// Hours older than the 60-day ring drop.
	l.Merge(ConsumptionPage{CurrentHour: 70 + ConsumptionWindowHours, FirstHour: 70})
	if len(l.hours) != 0 {
		t.Fatal(l.hours)
	}
}
