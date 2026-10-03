package bridge

import (
	"fmt"
	"math/rand"
	"testing"
)

// TestTableMatchesMapAndKeepsVersions (#1578): a random run of sets and
// deletes agrees with a plain map at every step, and every earlier
// version still reads as it did.
func TestTableMatchesMapAndKeepsVersions(t *testing.T) {
	rng := rand.New(rand.NewSource(1))
	var table Table[int]
	want := map[string]int{}
	type version struct {
		table Table[int]
		want  map[string]int
	}
	var versions []version
	for step := 0; step < 4000; step++ {
		key := fmt.Sprintf("k%d", rng.Intn(600))
		if rng.Intn(3) == 0 {
			table = table.Delete(key)
			delete(want, key)
		} else {
			table = table.Set(key, step)
			want[key] = step
		}
		if table.Len() != len(want) {
			t.Fatalf("step %d: len %d want %d", step, table.Len(), len(want))
		}
		if step%400 == 0 {
			cp := map[string]int{}
			for k, v := range want {
				cp[k] = v
			}
			versions = append(versions, version{table, cp})
		}
	}
	check := func(v version) {
		seen := 0
		for k, got := range v.table.All() {
			if w, ok := v.want[k]; !ok || w != got {
				t.Fatalf("%s = %d, want %d (%v)", k, got, w, ok)
			}
			seen++
		}
		if seen != len(v.want) || v.table.Len() != len(v.want) {
			t.Fatalf("iterated %d, len %d, want %d", seen, v.table.Len(), len(v.want))
		}
		for k, w := range v.want {
			if got, ok := v.table.Get(k); !ok || got != w {
				t.Fatalf("get %s = %d %v, want %d", k, got, ok, w)
			}
		}
	}
	check(version{table, want})
	for _, v := range versions {
		check(v)
	}
	if _, ok := table.Get("absent"); ok || table.Delete("absent").Len() != table.Len() {
		t.Fatal("an absent key was found or removed")
	}
	for k := range want {
		table = table.Delete(k)
	}
	if table.Len() != 0 || table.root != nil {
		t.Fatalf("emptied table keeps %d rows", table.Len())
	}
}

// TestTableDeltaCostIsIndependentOfSize: updating one row allocates the
// same few path nodes in a 100-row table and a 100000-row one.
func TestTableDeltaCostIsIndependentOfSize(t *testing.T) {
	cost := func(rows int) float64 {
		var table Table[int]
		for i := 0; i < rows; i++ {
			table = table.Set(fmt.Sprintf("row-%d", i), i)
		}
		n := 0
		return testing.AllocsPerRun(50, func() {
			n++
			_ = table.Set(fmt.Sprintf("row-%d", n%rows), -n)
		})
	}
	small, large := cost(100), cost(100000)
	t.Logf("allocations per row update: %.0f at 100 rows, %.0f at 100000", small, large)
	if large > small+10 {
		t.Fatalf("update cost grew with the table: %.0f vs %.0f", small, large)
	}
}

func BenchmarkTableUpdate(b *testing.B) {
	for _, rows := range []int{1000, 100000} {
		var table Table[int]
		for i := 0; i < rows; i++ {
			table = table.Set(fmt.Sprintf("row-%d", i), i)
		}
		b.Run(fmt.Sprint(rows), func(b *testing.B) {
			for i := 0; i < b.N; i++ {
				_ = table.Set(fmt.Sprintf("row-%d", i%rows), i)
			}
		})
	}
}
