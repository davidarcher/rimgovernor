package domain

import "testing"

func TestPopulationDirectiveBoundsPawnAndDecision(t *testing.T) {
	t.Parallel()
	for _, decision := range []PopulationDecision{PopulationRescue, PopulationCapture, PopulationRecruit, PopulationIgnore} {
		directive, err := NewPopulationDirective("Thing_Human1", decision)
		if err != nil || !directive.Set() || directive.Pawn() != "Thing_Human1" || directive.Decision() != decision {
			t.Fatal(directive, err)
		}
		if directive.Withdrawn() != (decision == PopulationIgnore) || directive.RequiresPolicy() == (decision == PopulationIgnore) {
			t.Fatal("ignore alone withdraws orders and needs no policy", decision)
		}
	}
	for _, invalid := range []PopulationDecision{"", "release", "Rescue", "banish"} {
		if _, err := NewPopulationDirective("Thing_Human1", invalid); err == nil {
			t.Fatal("unsupported decision must be rejected", invalid)
		}
	}
	for _, pawn := range []PawnID{"", "   ", "Thing_\x00Human"} {
		if _, err := NewPopulationDirective(pawn, PopulationRescue); err == nil {
			t.Fatal("invalid pawn must be rejected", pawn)
		}
	}
}

func TestPopulationDirectiveZeroValueSaysNothing(t *testing.T) {
	t.Parallel()
	var zero PopulationDirective
	if zero.Set() || zero.Withdrawn() || zero.RequiresPolicy() {
		t.Fatal("the zero directive is the absence of a direction")
	}
	first, err := NewPopulationDirective("Thing_Human1", PopulationRescue)
	if err != nil {
		t.Fatal(err)
	}
	same, err := NewPopulationDirective("Thing_Human1", PopulationRescue)
	if err != nil || first != same {
		t.Fatal("directives must compare by value", first, same, err)
	}
}
