package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestGearRecipeIngredientsSingleAllowedDefName(t *testing.T) {
	rows := []*o.IngredientRequirement{
		{AllowedDefNames: []string{"Steel"}, Required: proto.Float64(10), Complete: proto.Bool(true)},
	}
	fact := GearRecipeIngredients(rows)
	got, known := fact.Value()
	if !known {
		t.Fatalf("expected known ingredients")
	}
	want := [][]policy.Amount{{{Resource: "Steel", Count: 10}}}
	if len(got) != 1 || len(got[0]) != 1 || got[0][0] != want[0][0] {
		t.Fatalf("got %v want %v", got, want)
	}
}

func TestGearRecipeIngredientsMultipleAllowedDefNamesShareTheSlotCount(t *testing.T) {
	rows := []*o.IngredientRequirement{
		{AllowedDefNames: []string{"Steel", "Plasteel"}, Required: proto.Float64(10), Complete: proto.Bool(true)},
	}
	got, known := GearRecipeIngredients(rows).Value()
	want := []policy.Amount{{Resource: "Steel", Count: 10}, {Resource: "Plasteel", Count: 10}}
	if !known || len(got) != 1 || len(got[0]) != 2 || got[0][0] != want[0] || got[0][1] != want[1] {
		t.Fatalf("got %v known %v", got, known)
	}
	rows[0].AllowedDefNames = []string{"Steel", "Steel"}
	if _, known = GearRecipeIngredients(rows).Value(); known {
		t.Fatal("duplicate alternative accepted")
	}
}

func TestGearRecipeIngredientsIncompleteRowUnknown(t *testing.T) {
	rows := []*o.IngredientRequirement{
		{AllowedDefNames: []string{"Steel"}, Required: proto.Float64(10), Complete: proto.Bool(false)},
	}
	if _, known := GearRecipeIngredients(rows).Value(); known {
		t.Fatalf("expected unknown for an incomplete row")
	}
}

func TestGearRecipeIngredientsFractionalRequiredUnknown(t *testing.T) {
	rows := []*o.IngredientRequirement{
		{AllowedDefNames: []string{"Steel"}, Required: proto.Float64(2.5), Complete: proto.Bool(true)},
	}
	if _, known := GearRecipeIngredients(rows).Value(); known {
		t.Fatalf("expected unknown for a fractional required amount")
	}
}

func TestGearRecipeIngredientsMissingRequiredUnknown(t *testing.T) {
	rows := []*o.IngredientRequirement{
		{AllowedDefNames: []string{"Steel"}, Complete: proto.Bool(true)},
	}
	if _, known := GearRecipeIngredients(rows).Value(); known {
		t.Fatalf("expected unknown when Required is unset")
	}
}

func TestGearRecipeIngredientsZeroOrNegativeRequiredUnknown(t *testing.T) {
	rows := []*o.IngredientRequirement{
		{AllowedDefNames: []string{"Steel"}, Required: proto.Float64(0), Complete: proto.Bool(true)},
	}
	if _, known := GearRecipeIngredients(rows).Value(); known {
		t.Fatalf("expected unknown for a zero required amount")
	}
}

func TestGearRecipeIngredientsMultipleSlots(t *testing.T) {
	rows := []*o.IngredientRequirement{
		{AllowedDefNames: []string{"Steel"}, Required: proto.Float64(10), Complete: proto.Bool(true)},
		{AllowedDefNames: []string{"ComponentIndustrial"}, Required: proto.Float64(2), Complete: proto.Bool(true)},
	}
	got, known := GearRecipeIngredients(rows).Value()
	if !known || len(got) != 2 {
		t.Fatalf("got %v known %v", got, known)
	}
	if got[0][0].Resource != "Steel" || got[1][0].Resource != "ComponentIndustrial" {
		t.Fatalf("got %v", got)
	}
}

func TestGearRecipeIngredientsEmptyIsKnownEmpty(t *testing.T) {
	got, known := GearRecipeIngredients(nil).Value()
	if !known || len(got) != 0 {
		t.Fatalf("expected known empty ingredient list, got %v known %v", got, known)
	}
}

func TestGearRecipeIngredientsOneUnknownSlotPoisonsWholeRecipe(t *testing.T) {
	rows := []*o.IngredientRequirement{
		{AllowedDefNames: []string{"Steel"}, Required: proto.Float64(10), Complete: proto.Bool(true)},
		{AllowedDefNames: []string{"Steel"}, Required: proto.Float64(10), Complete: proto.Bool(false)},
	}
	if _, known := GearRecipeIngredients(rows).Value(); known {
		t.Fatalf("expected the whole recipe to be unknown when any one slot is unknown")
	}
}
