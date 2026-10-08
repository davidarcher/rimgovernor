package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ForwardObserved are the projector inputs the routine facts do not already
// carry (food, sleeping range and conditions are read from the facts).
type ForwardObserved struct {
	Power   []PowerNetworkFact                `json:",omitempty"`
	Turrets domain.Fact[[]DefenseTurretFacts] `json:",omitzero"`
}

// ForwardInputsOf assembles the projector inputs from the routine facts.
func ForwardInputsOf(f RoundsFacts, p RoundsPolicy) ForwardInputs {
	in := ForwardInputs{Power: f.Forward.Power, Sleeping: SleepingRange{Min: f.SleepingMin, Max: f.SleepingMax}, Conditions: f.DisasterConditions, Turrets: f.Forward.Turrets, Policy: p,
		Construction: ConstructionInputs{Deficit: f.ConstructionDeficit, Admitted: f.Admitted, Stock: f.Resources, Items: f.Items},
		Fuel:         FuelInputs{Consumers: f.Fuel, Stock: StockReader{Resources: f.Resources, Wood: f.Wood}},
		AnimalFeed:   f.animalFeedInputs(), Runways: f.ResourceRunways, Items: f.Items}
	if supply, known := f.AnimalUpkeep.Food.Value(); known {
		in.Food = supply
	}
	return in
}
