package domain

import "testing"

func TestResourceDirectiveBounds(t *testing.T) {
	d, err := NewResourceDirective("Steel", 250, ResourceSpendingDefenseOnly)
	if err != nil || !d.Set() || d.Resource() != "Steel" || d.Reserve() != 250 || d.Spending() != ResourceSpendingDefenseOnly {
		t.Fatal("valid resource directive", d, err)
	}
	if !d.Restricted() {
		t.Fatal("defense_only belongs in the dispatched stopped list")
	}
	normal, err := DefaultResourceDirective("Steel")
	if err != nil || normal.Reserve() != 0 || normal.Spending() != ResourceSpendingNormal || normal.Restricted() {
		t.Fatal("default resource directive", normal, err)
	}
	if (ResourceDirective{}).Set() {
		t.Fatal("the zero directive means the player has said nothing")
	}
	for _, tc := range []struct {
		name     string
		resource string
		reserve  int64
		spending ResourceSpending
	}{
		{"blank resource", " ", 0, ResourceSpendingNormal},
		{"negative reserve", "Steel", -1, ResourceSpendingNormal},
		{"oversized reserve", "Steel", MaxResourceReserve + 1, ResourceSpendingNormal},
		{"unsupported spending", "Steel", 0, ResourceSpending("hoard")},
		{"empty spending", "Steel", 0, ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if _, err := NewResourceDirective(tc.resource, tc.reserve, tc.spending); err == nil {
				t.Fatal("expected refusal")
			}
		})
	}
}

func TestResourcePolicyPatchSetsExactlyOneHalf(t *testing.T) {
	if (ResourcePolicyPatch{}).Validate() == nil {
		t.Fatal("an empty request changes nothing")
	}
	both := ResourcePolicyPatch{Resource: "Steel", Spending: Some(ResourceSpendingStop), Reserve: Some(int64(5))}
	if both.Validate() == nil {
		t.Fatal("a request may not set both halves")
	}
	if (ResourcePolicyPatch{Resource: " ", Reserve: Some(int64(5))}).Validate() == nil {
		t.Fatal("a blank resource is refused")
	}
	if (ResourcePolicyPatch{Resource: "Steel", Reserve: Some(int64(-1))}).Validate() == nil {
		t.Fatal("a negative reserve is refused")
	}
	if (ResourcePolicyPatch{Resource: "Steel", Spending: Some(ResourceSpending("hoard"))}).Validate() == nil {
		t.Fatal("an unsupported restriction is refused")
	}
}

// Each command preserves the half it does not name, the exact behaviour the
// two Python doc strings promise.
func TestResourcePolicyPatchPreservesTheOtherHalf(t *testing.T) {
	base, err := NewResourceDirective("Steel", 250, ResourceSpendingStop)
	if err != nil {
		t.Fatal(err)
	}
	spending, err := ResourcePolicyPatch{Resource: "Steel", Spending: Some(ResourceSpendingNormal)}.Apply(base)
	if err != nil || spending.Reserve() != 250 || spending.Spending() != ResourceSpendingNormal {
		t.Fatal("changing spending must preserve the reserve", spending, err)
	}
	reserve, err := ResourcePolicyPatch{Resource: "Steel", Reserve: Some(int64(0))}.Apply(base)
	if err != nil || reserve.Reserve() != 0 || reserve.Spending() != ResourceSpendingStop {
		t.Fatal("changing the reserve must preserve the restriction", reserve, err)
	}
	// A first mention merges onto {reserve 0, spending normal}, as Python's
	// setdefault does.
	first, err := ResourcePolicyPatch{Resource: "Plasteel", Reserve: Some(int64(30))}.Apply(ResourceDirective{})
	if err != nil || first.Resource() != "Plasteel" || first.Reserve() != 30 || first.Spending() != ResourceSpendingNormal {
		t.Fatal("first mention merges onto the default entry", first, err)
	}
	if _, err = (ResourcePolicyPatch{Resource: "Plasteel", Reserve: Some(int64(30))}).Apply(base); err == nil {
		t.Fatal("a request may not merge onto another resource's entry")
	}
}

func TestResourceProductionPolicyFoldsWholeSet(t *testing.T) {
	steel, err := NewResourceDirective("Steel", 250, ResourceSpendingStop)
	if err != nil {
		t.Fatal(err)
	}
	// A zero reserve carries no floor (Python's "if v" filter) but an unnamed
	// restriction still stops the resource.
	wood, err := NewResourceDirective("WoodLog", 0, ResourceSpendingDefenseOnly)
	if err != nil {
		t.Fatal(err)
	}
	silver, err := NewResourceDirective("Silver", 40, ResourceSpendingNormal)
	if err != nil {
		t.Fatal(err)
	}
	value, err := ResourceProductionPolicy([]ResourceDirective{wood, steel, silver})
	if err != nil {
		t.Fatal(err)
	}
	floors := value.Floors()
	if len(floors) != 2 || floors[0].Resource != "Silver" || floors[0].Floor != 40 || floors[1].Resource != "Steel" || floors[1].Floor != 250 {
		t.Fatal("incorrect folded floors", floors)
	}
	stopped := value.Stopped()
	if len(stopped) != 2 || stopped[0] != "Steel" || stopped[1] != "WoodLog" {
		t.Fatal("incorrect folded stopped list", stopped)
	}
	// The folded value must be exactly what the existing dispatch action
	// accepts, unchanged.
	if _, err = NewProductionPolicyAction("resource-policy-action-1", value); err != nil {
		t.Fatal("folded policy must construct the existing action", err)
	}
	if _, err = ResourceProductionPolicy([]ResourceDirective{steel, steel}); err == nil {
		t.Fatal("a duplicate resource is refused")
	}
	if _, err = ResourceProductionPolicy([]ResourceDirective{{}}); err == nil {
		t.Fatal("an unset directive is refused")
	}
	empty, err := ResourceProductionPolicy(nil)
	if err != nil || len(empty.Floors()) != 0 || len(empty.Stopped()) != 0 {
		t.Fatal("an empty set folds to an empty replacement", empty, err)
	}
}
