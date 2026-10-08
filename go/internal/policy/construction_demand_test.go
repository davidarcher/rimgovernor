package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func census(rows ...Amount) domain.Fact[[]Amount] { return domain.Known(rows) }

// A research bench blueprint short of 25 steel names the steel; a site the
// stock already covers (waiting on a hauler) adds nothing.
func TestConstructionDemandBlueprintDeficit(t *testing.T) {
	in := ConstructionDemandInput{
		Owed:  domain.Known(map[Resource]int64{"Steel": 25, "WoodLog": 10}),
		Stock: StockReader{Resources: census(Amount{Resource: "Steel", Count: 4}, Amount{Resource: "WoodLog", Count: 40})},
	}
	if got := ConstructionDemand(in); !reflect.DeepEqual(got, map[Resource]int64{"Steel": 25}) {
		t.Fatalf("needs = %v", got)
	}
	in.Owed = domain.Unknown[map[Resource]int64]()
	if got := ConstructionDemand(in); got != nil {
		t.Fatalf("unknown deficit = %v", got)
	}
	in.Owed = domain.Known(map[Resource]int64{"Steel": 25})
	in.Stock = StockReader{}
	if got := ConstructionDemand(in); got != nil {
		t.Fatalf("unknown stock = %v", got)
	}
}

// A shell admitted short of wood asks for its open costs, each action once,
// wherever the current stock falls short; stock that covers them asks nothing.
func TestConstructionDemandAdmittedMethodCosts(t *testing.T) {
	in := ConstructionDemandInput{
		Admitted: []AdmittedCost{{"WoodLog", "a", 120}, {"WoodLog", "a", 100}, {"WoodLog", "b", 80}, {"Steel", "c", 5}},
		Stock:    StockReader{Resources: census(Amount{Resource: "Steel", Count: 5}), Wood: domain.Known(int64(150))},
	}
	if got := ConstructionDemand(in); !reflect.DeepEqual(got, map[Resource]int64{"WoodLog": 200}) {
		t.Fatalf("needs = %v", got)
	}
	in.Stock.Wood = domain.Known(int64(200))
	if got := ConstructionDemand(in); got != nil {
		t.Fatalf("covered = %v", got)
	}
}

// The wood latch floors WoodLog whatever the stock; it merges with the other
// sources by maximum.
func TestConstructionDemandWoodLatch(t *testing.T) {
	in := ConstructionDemandInput{WoodFloor: 300, Stock: StockReader{Wood: domain.Known(int64(10))}}
	if got := ConstructionDemand(in); !reflect.DeepEqual(got, map[Resource]int64{"WoodLog": 300}) {
		t.Fatalf("latch = %v", got)
	}
	in.Admitted = []AdmittedCost{{"WoodLog", "a", 500}}
	if got := ConstructionDemand(in); got["WoodLog"] != 500 {
		t.Fatalf("larger admitted cost must stand: %v", got)
	}
	in.WoodFloor = 0
	in.Admitted = nil
	if got := ConstructionDemand(in); got != nil {
		t.Fatalf("latch off = %v", got)
	}
}

func TestStockReaderWoodFact(t *testing.T) {
	s := StockReader{Resources: census(Amount{Resource: "WoodLog", Count: 3}, Amount{Resource: "Steel", Count: 7}), Wood: domain.Known(int64(9))}
	if n, _ := s.Count("WoodLog").Value(); n != 9 {
		t.Fatal("wood fact must override the census row", n)
	}
	if s.Units("Steel") != 7 || s.Units("Gold") != 0 {
		t.Fatal("census count")
	}
	if rows, known := s.Census(nil).Value(); !known || len(rows) != 2 {
		t.Fatal(rows)
	}
	if _, known := (StockReader{Wood: domain.Known(int64(9))}).Census(map[Resource]int64{"WoodLog": 5, "Steel": 1}).Value(); known {
		t.Fatal("unknown census stays unknown with other targets")
	}
}
