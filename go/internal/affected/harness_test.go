package affected

import (
	"slices"
	"strings"
	"testing"
)

// A harness source reaches an area through the objects its change taints
// (#348): a helper only one area calls names that area; a source on the
// runner's path names every area, sampled unless the area's own sources
// use a tainted object; a harness test file names nothing.
func TestSelectScopesHarnessSources(t *testing.T) {
	r := repo(t)
	sel, err := Select(r, []string{"go/internal/nativeaccept/clock.go"})
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Equal(sel.Cases, []string{"speedmatrix"}) || len(sel.Sampled) != 0 || sel.AllHarnesses {
		t.Errorf("clock.go selected %v (sampled %v, all %v), want speedmatrix alone", sel.Cases, sel.Sampled, sel.AllHarnesses)
	}
	if why := strings.Join(sel.Why["speedmatrix"], "\n"); !strings.Contains(why, "the area uses na.RequireHealthyColonists of go/internal/nativeaccept/clock.go") {
		t.Errorf("speedmatrix why:\n%s", why)
	}

	sel, err = Select(r, []string{"go/internal/nativeaccept/stale.go"})
	if err != nil {
		t.Fatal(err)
	}
	all, err := caseAreas(r + "/go")
	if err != nil {
		t.Fatal(err)
	}
	if len(sel.Cases) != len(all) {
		t.Errorf("stale.go (on the runner's path) selected %d of %d areas", len(sel.Cases), len(all))
	}
	if len(sel.Sampled) == 0 || len(sel.Sampled) == len(sel.Cases) {
		t.Errorf("stale.go sampled %d of %d areas, want some but not all", len(sel.Sampled), len(sel.Cases))
	}
	for _, area := range sel.Sampled {
		if !slices.Contains(sel.Cases, area) {
			t.Errorf("sampled area %s is not selected", area)
		}
		why := strings.Join(sel.Why[area], "\n")
		if strings.Contains(why, "the area uses") || !strings.Contains(why, "sampled:") {
			t.Errorf("sampled area %s why:\n%s", area, why)
		}
	}
	if !slices.Contains(sel.Sampled, "power") {
		t.Errorf("power runs whole for stale.go: %v", sel.Why["power"])
	}
	if why := strings.Join(sel.Why["lifecycle"], "\n"); !strings.Contains(why, "the runner uses na.") {
		t.Errorf("lifecycle why lacks the runner's use:\n%s", why)
	}

	sel, err = Select(r, []string{"go/internal/nativeaccept/clock_test.go"})
	if err != nil {
		t.Fatal(err)
	}
	if len(sel.Cases) != 0 {
		t.Errorf("a harness test file selected %v", sel.Cases)
	}
	if !slices.Contains(sel.Packages, "github.com/davidarcher/RimGovernor/go/internal/nativeaccept") {
		t.Errorf("a harness test file still tests its package: %v", sel.Packages)
	}
}

// The edited declarations of a file version pair, comments aside.
func TestChangedDeclKeys(t *testing.T) {
	before := []byte(`package p

// A doc.
func A() int { return 1 }

func (s *S) M() {}

type S struct{ X int }

const (
	K = 1
	L = 2
)
`)
	after := []byte(`package p

// Another doc.
func A() int { return 1 }

func (s *S) M() { s.X++ }

type S struct{ X int }

const (
	K = 1
	L = 3
)

var N = 4
`)
	keys, ok := changedDeclKeys(before, after)
	if !ok {
		t.Fatal("versions differ, want a key set")
	}
	var got []string
	for key := range keys {
		got = append(got, key)
	}
	slices.Sort(got)
	if want := []string{"L", "N", "S.M"}; !slices.Equal(got, want) {
		t.Errorf("changed keys %v, want %v", got, want)
	}
	if _, ok := changedDeclKeys(before, before); ok {
		t.Error("identical versions should fall back to every declaration")
	}
	if _, ok := changedDeclKeys([]byte("package p\nfunc ("), after); ok {
		t.Error("an unparsable version should fall back to every declaration")
	}
}
