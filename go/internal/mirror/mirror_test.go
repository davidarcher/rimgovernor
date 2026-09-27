package mirror

import "testing"

// TestPutReplacesAndRescopes: a table replaces the section whole, reaches
// the recorder, and a new scope empties every section.
func TestPutReplacesAndRescopes(t *testing.T) {
	m := New()
	var recorded []Published
	m.SetRecorder(func(p Published) { recorded = append(recorded, p) })
	a := Scope{Load: "l", Map: 1, Generation: 1}
	Put(m, a, "s", map[string]int{"x": 1, "y": 2}, At(10))
	Put(m, a, "other", map[string]int{"z": 3}, At(10))
	second := Put(m, a, "s", map[string]int{"x": 5}, At(20))
	got, ok := Get[string, int](m, a, "s")
	if !ok || len(got.Rows) != 1 || got.Rows["x"] != 5 || got.AsOf != At(20) || got.Version != second.Version {
		t.Fatalf("table = %+v ok=%v", got, ok)
	}
	if _, ok := Get[string, int](m, Scope{Load: "l", Map: 2, Generation: 1}, "s"); ok {
		t.Fatal("table served under another scope")
	}
	b := Scope{Load: "l", Map: 2, Generation: 1}
	Put(m, b, "s", map[string]int(nil), At(30))
	if _, ok := Get[string, int](m, b, "other"); ok {
		t.Fatal("rescope kept a section of the old world")
	}
	if got, ok := Get[string, int](m, b, "s"); !ok || got.Rows == nil || len(got.Rows) != 0 {
		t.Fatalf("empty put = %+v ok=%v", got, ok)
	}
	if len(recorded) != 4 || recorded[3].Scope != b || recorded[2].AsOf != At(20) {
		t.Fatalf("recorded = %+v", recorded)
	}
}
