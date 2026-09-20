package remoteaccept

import (
	"math"
	"reflect"
	"testing"
)

func TestPlanShardsDependencyGroups(t *testing.T) {
	names := []string{"a/one", "sustained/matrix-a", "sustained/matrix-b", "tools/variantsavegen-a", "tools/variantsavegen-b", "z/last"}
	want := []PlannedShard{
		{ID: "s1", Cases: []string{"a/one", "tools/variantsavegen-b", "sustained/matrix-b"}},
		{ID: "s2", Cases: []string{"tools/variantsavegen-a", "sustained/matrix-a", "z/last"}},
	}
	got, err := PlanShards(names, 2, DependencyAlgorithm, nil)
	if err != nil || !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v, %v; want %+v", got, err, want)
	}
	got, err = PlanShards(names[1:5], 32, DependencyAlgorithm, nil)
	if err != nil || len(got) != 2 || len(got[0].Cases) != 2 || len(got[1].Cases) != 2 {
		t.Fatalf("dependency groups must not leave empty shards: %+v, %v", got, err)
	}
}

func TestEvaluateDependencyAlgorithm(t *testing.T) {
	f := fixtureRun(t)
	f.selection.Algorithm = DependencyAlgorithm
	f.save(t)
	if _, err := f.evaluate(t); err != nil {
		t.Fatal(err)
	}
	f.selection.Shards[0].Cases[0], f.selection.Shards[1].Cases[0] = f.selection.Shards[1].Cases[0], f.selection.Shards[0].Cases[0]
	f.save(t)
	if _, err := f.evaluate(t); err == nil {
		t.Fatal("invalid dependency assignment accepted")
	}
}

func TestPlanShardsBudgetBalanced(t *testing.T) {
	names := []string{"a/heavy", "b/small", "c/heavy", "d/small", "sustained/matrix-a", "tools/variantsavegen-a"}
	budgets := map[string]int64{"a/heavy": 8, "b/small": 2, "c/heavy": 8, "d/small": 2, "sustained/matrix-a": 7, "tools/variantsavegen-a": 3}
	want := []PlannedShard{
		{ID: "s1", Cases: []string{"tools/variantsavegen-a", "sustained/matrix-a"}},
		{ID: "s2", Cases: []string{"a/heavy", "b/small"}},
		{ID: "s3", Cases: []string{"c/heavy", "d/small"}},
	}
	got, err := PlanShards(names, 3, BudgetAlgorithm, budgets)
	if err != nil || !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v, %v; want %+v", got, err, want)
	}
	got, err = PlanShards(names[4:], 32, BudgetAlgorithm, budgets)
	if err != nil || !reflect.DeepEqual(got, want[:1]) {
		t.Fatalf("group must stay together without empty shards: %+v, %v", got, err)
	}
}

func TestPlanShardsRejectsInvalidBudgets(t *testing.T) {
	for _, budgets := range []map[string]int64{nil, {"a/one": 1}, {"a/one": 0, "b/two": 1}, {"a/one": -1, "b/two": 1}, {"a/one": math.MaxInt64, "b/two": 1}} {
		if _, err := PlanShards([]string{"a/one", "b/two"}, 2, BudgetAlgorithm, budgets); err == nil {
			t.Fatalf("accepted invalid budgets %v", budgets)
		}
	}
}

func TestEvaluateBudgetAlgorithm(t *testing.T) {
	f := fixtureRun(t)
	f.selection.Algorithm = BudgetAlgorithm
	// Equal costs reproduce the fixture's round-robin assignment.
	for i := range f.selection.Cases {
		f.selection.Cases[i].BudgetNS = 1
	}
	if _, err := f.evaluate(t); err != nil {
		t.Fatal(err)
	}
	f.selection.Cases[0].BudgetNS = 0
	if _, err := f.evaluate(t); err == nil {
		t.Fatal("missing budget accepted")
	}
	f.selection.Cases[0].BudgetNS = 1
	f.selection.Cases[len(f.selection.Cases)-1].BudgetNS = 10
	if _, err := f.evaluate(t); err == nil {
		t.Fatal("assignment inconsistent with recorded budgets accepted")
	}
}
