package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Realized consumption. Native counts what the colony's stock was
// spent on, per (definition, reason), into a saved hourly ring; Go holds the
// hours it was sent and reads a rate over any window as a subtraction.

const (
	// ConsumptionHourTicks is one ring hour.
	ConsumptionHourTicks = 2500
	// ConsumptionWindowHours is the ring's depth: 60 days.
	ConsumptionWindowHours = 60 * 24
	// ResourceRateWindowDays is the window a maintenance rate is read over.
	ResourceRateWindowDays = 15
)

// recurringReasons are the reasons whose spend repeats without a decision:
// only these feed a resource runway. Construction is a project (it stays on
// ConstructionDemand); the rest are losses.
var recurringReasons = map[string]bool{
	"bill_ingredient": true, "medicine_tend": true, "food_eaten": true, "drug_dose": true, "animal_feed": true,
	"nutrient_paste": true, "fuel_loaded": true, "shell_loaded": true, "apparel_wear": true,
}

// ConsumptionRow is one (resource, reason) spend; Count is signed (an ejected
// refuel or a removed shell subtracts).
type ConsumptionRow struct {
	Resource Resource
	Reason   string
	Count    int64
}

// ConsumptionHour is one completed ring hour, sparse.
type ConsumptionHour struct {
	Hour int
	Rows []ConsumptionRow
}

// ConsumptionPage is one native read: the hours after the asked-for hour.
// CurrentHour is still filling and never listed; FirstHour is the first hour
// the ring covers.
type ConsumptionPage struct {
	CurrentHour, FirstHour int
	Hours                  []ConsumptionHour
}

// ResourceConsumption is the recurring spend over the span the ledger covers
// inside a rate window.
type ResourceConsumption struct {
	WindowDays float64
	Recurring  map[Resource]int64
}

// ConsumptionLedger is Go's in-memory copy of the ring. The zero value holds
// nothing and asks for the whole window.
type ConsumptionLedger struct {
	have           bool
	through, first int
	hours          map[int][]ConsumptionRow
}

// Since is the hour to pass native: the last completed hour held, or -1 for
// the whole window.
func (l *ConsumptionLedger) Since() int {
	if !l.have {
		return -1
	}
	return l.through
}

// Merge folds one page in. Every hour up to CurrentHour-1 is now held, listed
// or not.
func (l *ConsumptionLedger) Merge(p ConsumptionPage) {
	if l.hours == nil {
		l.hours = map[int][]ConsumptionRow{}
	}
	for _, h := range p.Hours {
		l.hours[h.Hour] = h.Rows
	}
	l.have, l.through, l.first = true, p.CurrentHour-1, p.FirstHour
	for hour := range l.hours {
		if hour <= l.through-ConsumptionWindowHours {
			delete(l.hours, hour)
		}
	}
}

// Window is the recurring spend over the last days days the ring covers.
func (l *ConsumptionLedger) Window(days int) domain.Fact[ResourceConsumption] {
	if !l.have {
		return domain.Unknown[ResourceConsumption]()
	}
	start := max(l.first, l.through+1-days*24)
	out := ResourceConsumption{Recurring: map[Resource]int64{}}
	if l.through+1 > start {
		out.WindowDays = float64(l.through+1-start) / 24
	}
	for hour := start; hour <= l.through; hour++ {
		for _, row := range l.hours[hour] {
			if recurringReasons[row.Reason] {
				out.Recurring[row.Resource] += row.Count
			}
		}
	}
	for resource, n := range out.Recurring {
		out.Recurring[resource] = max(0, n)
	}
	return domain.Known(out)
}

// KnownConsumptionReason reports whether native's reason is one this build
// names.
func KnownConsumptionReason(reason string) bool {
	if recurringReasons[reason] {
		return true
	}
	switch reason {
	case "construction", "rot", "deterioration", "fire", "sold", "stolen", "destroyed_other":
		return true
	}
	return false
}
