package buildingruntime

import (
	"reflect"
	"testing"
)

// inlinePlannerSet is the planner set, priorities and queue order Step
// carried as inline g.Go blocks before the catalog; the catalog must queue
// exactly this set for a full step.
var inlinePlannerSet = []struct {
	name     string
	priority int
}{
	{"work", plannerFoothold}, {"fields", plannerFoothold}, {"foodStorage", plannerFoothold}, {"foodAcquisition", plannerFoothold},
	{"woodAcquisition", plannerMaintenance}, {"supplies", plannerFoothold}, {"sleeping", plannerFoothold}, {"power", plannerFoothold},
	{"temperature", plannerFoothold}, {"refrigeration", plannerMaintenance}, {"lighting", plannerMaintenance}, {"flooring", plannerMaintenance},
	{"routes", plannerMaintenance}, {"cooking", plannerFoothold}, {"butcher", plannerMaintenance}, {"cookingBills", plannerFoothold},
	{"preservationBills", plannerFoothold}, {"butcherBills", plannerMaintenance}, {"comfort", plannerComfort}, {"workshop", plannerMaintenance},
	{"hospital", plannerCritical}, {"expansion", plannerComfort}, {"defense", plannerPreempt}, {"tend", plannerCritical},
	{"rescue", plannerCritical}, {"equip", plannerMaintenance}, {"secureSupplies", plannerFoothold}, {"repair", plannerMaintenance},
	{"fireSafety", plannerFoothold}, {"clean", plannerMaintenance}, {"waste", plannerMaintenance}, {"moodRelief", plannerMaintenance},
	{"haul", plannerMaintenance}, {"gear", plannerMaintenance}, {"medical", plannerCritical}, {"foodStorageUpkeep", plannerFoothold},
	{"animalContainment", plannerMaintenance}, {"recovery", plannerCritical}, {"husbandry", plannerMaintenance}, {"prisonerInteraction", plannerMaintenance},
	{"populationCustody", plannerFoothold}, {"research", plannerMaintenance}, {"naming", plannerPreempt}, {"resource", plannerMaintenance},
	{"animalFeed", plannerMaintenance}, {"productionPolicy", plannerMaintenance}, {"caravanJourney", plannerMaintenance}, {"homeCoverage", plannerComfort},
	{"stoneShell", plannerComfort}, {"defenseLayout", plannerMaintenance},
}

// TestPlannerCatalogMatchesInlineSet: the catalog is the inline set in the
// inline order with the inline priorities, every entry is configured by a
// distinct config field, and a full step queues every configured entry.
func TestPlannerCatalogMatchesInlineSet(t *testing.T) {
	t.Parallel()
	if len(plannerCatalog) != len(inlinePlannerSet) {
		t.Fatalf("catalog has %d planners, inline set had %d", len(plannerCatalog), len(inlinePlannerSet))
	}
	for i, want := range inlinePlannerSet {
		got := plannerCatalog[i]
		if got.name != want.name || got.priority != want.priority {
			t.Fatalf("catalog[%d] = %s/%d, inline had %s/%d", i, got.name, got.priority, want.name, want.priority)
		}
		if got.configured == nil || got.run == nil {
			t.Fatalf("%s lacks configured/run", got.name)
		}
		if got.configured(&ClockSchedulerConfig{}) {
			t.Fatalf("%s reports configured on an empty config", got.name)
		}
	}
	// Every configurable planner field of the config selects exactly one
	// catalog entry: setting the pointer to a zero value of its type turns
	// on one entry and no other.
	var config ClockSchedulerConfig
	v := reflect.ValueOf(&config).Elem()
	entries := 0
	for i := 0; i < v.NumField(); i++ {
		field := v.Field(i)
		if field.Kind() != reflect.Ptr {
			continue
		}
		name := v.Type().Field(i).Name
		if name == "Routine" {
			continue
		}
		field.Set(reflect.New(field.Type().Elem()))
		on := 0
		for _, entry := range plannerCatalog {
			if entry.configured(&config) {
				on++
			}
		}
		field.Set(reflect.Zero(field.Type()))
		if on != 1 {
			t.Fatalf("config field %s selects %d catalog entries", name, on)
		}
		entries++
	}
	if entries != len(plannerCatalog) {
		t.Fatalf("%d config fields for %d catalog entries", entries, len(plannerCatalog))
	}
}
