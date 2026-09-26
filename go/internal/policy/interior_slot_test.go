package policy

import "testing"

func TestInteriorSlotAcceptsFamily(t *testing.T) {
	stove := InteriorPiece{Def: KitchenStoveDefinition}
	bench := InteriorPiece{Def: workshopBenchDef}
	cases := []struct {
		slot InteriorPiece
		def  string
		want bool
	}{
		{stove, "FueledStove", true},
		{stove, "ElectricStove", true},
		{stove, "TableButcher", false},
		{stove, "ElectricSmithy", false},
		{bench, "ElectricSmithy", true},
		{bench, "HandTailoringBench", true},
		{bench, "ElectricStove", false},
		{bench, "TableButcher", false},
		{InteriorPiece{Def: "Bed"}, "DoubleBed", false},
	}
	for _, c := range cases {
		if got := c.slot.Accepts(c.def); got != c.want {
			t.Errorf("%s slot accepts %s = %v, want %v", c.slot.Def, c.def, got, c.want)
		}
	}
}
