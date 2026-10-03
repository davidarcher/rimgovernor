package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The herd's precept facts (#1644, epic #1653) come from the shared precept
// rule: each animal action is bound to the HistoryEventDef the game raises
// for it (HistoryEventDefOf in Assembly-CSharp), and ActionStance answers
// what the colony ideoligion makes of it.
//
// Selling an animal raises no history event in the game (only SoldSlave,
// SoldPrisoner and SoldOrgan exist), so no precept bears on a sale and the
// herd plan sells without consulting the ideoligion.
const (
	// eventSlaughteredAnimal is raised by any slaughter of an animal;
	// eventSlaughteredVeneratedAnimal additionally by a venerated race's.
	eventSlaughteredAnimal          = "SlaughteredAnimal"
	eventSlaughteredVeneratedAnimal = "SlaughteredVeneratedAnimal"
	eventAteMeat                    = "AteMeat"
	eventAteVeneratedAnimalMeat     = "AteVeneratedAnimalMeat"
)

// ApplyHerdPrecepts sets each animal's SlaughterBarred and EatingBarred from
// the ideoligion. ideologyActive is whether the load has Ideology defs: a
// game without Ideology has no precepts and bars nothing; with Ideology, an
// unread ideoligion or veneration leaves both unknown (the herd plan then
// holds removal, sale and slaughter). A race is barred when any action it
// raises is not allowed or approved: penalised, forbidden or unknown.
func ApplyHerdPrecepts(animals domain.Fact[[]UpkeepAnimal], ideology domain.Fact[Ideoligion], ideologyActive bool) domain.Fact[[]UpkeepAnimal] {
	rows, known := animals.Value()
	if !known {
		return animals
	}
	out := make([]UpkeepAnimal, len(rows))
	for i, a := range rows {
		out[i] = a
		if !ideologyActive {
			out[i].Herd.SlaughterBarred, out[i].Herd.EatingBarred = domain.Known(false), domain.Known(false)
			continue
		}
		out[i].Herd.SlaughterBarred = preceptBars(ideology, a.Herd.Venerated, eventSlaughteredAnimal, eventSlaughteredVeneratedAnimal)
		out[i].Herd.EatingBarred = preceptBars(ideology, a.Herd.Venerated, eventAteMeat, eventAteVeneratedAnimalMeat)
	}
	return domain.Known(out)
}

// preceptBars is whether the colony's precepts bar the action: base for any
// animal, plus venerated for a venerated race. Unknown when the ideoligion or
// the veneration is unread.
func preceptBars(ideology domain.Fact[Ideoligion], venerated domain.Fact[bool], base, veneratedEvent string) domain.Fact[bool] {
	v, vk := venerated.Value()
	if !vk {
		return domain.Unknown[bool]()
	}
	events := []string{base}
	if v {
		events = append(events, veneratedEvent)
	}
	barred := false
	for _, event := range events {
		switch ActionStance(ideology, PreceptAction{HistoryEvent: event}, PreceptSubject{}).Stance {
		case PreceptAllowed, PreceptApproved:
		case PreceptPenalised, PreceptForbidden:
			barred = true
		default:
			return domain.Unknown[bool]()
		}
	}
	return domain.Known(barred)
}
