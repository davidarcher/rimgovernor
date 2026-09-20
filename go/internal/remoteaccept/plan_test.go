package remoteaccept

import (
	"reflect"
	"testing"
)

func TestPlanShardsDependencyGroups(t *testing.T) {
	names := []string{"a/one", "sustained/matrix-a", "sustained/matrix-b", "tools/variantsavegen-a", "tools/variantsavegen-b", "z/last"}
	want := []PlannedShard{
		{ID: "s1", Cases: []string{"a/one", "tools/variantsavegen-b", "sustained/matrix-b"}},
		{ID: "s2", Cases: []string{"tools/variantsavegen-a", "sustained/matrix-a", "z/last"}},
	}
	got, err := PlanShards(names, 2, DependencyAlgorithm)
	if err != nil || !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v, %v; want %+v", got, err, want)
	}
	got, err = PlanShards(names[1:5], 32, DependencyAlgorithm)
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
