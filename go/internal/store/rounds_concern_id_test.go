package store

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func roundsIDs(t *testing.T, out RoundsResult) map[domain.ConcernID]domain.StandardID {
	t.Helper()
	ids := map[domain.ConcernID]domain.StandardID{}
	for i, b := range out.Review.Standards {
		if out.Standards[i].Standard.Kind != b.Concern || out.Standards[i].Standard.ID != b.Standard {
			t.Fatal("binding disagrees with its row", b.Standard)
		}
		ids[b.Concern] = b.Standard
	}
	if len(ids) == 0 {
		t.Fatal("no routine goals")
	}
	return ids
}

func TestRoundsGoalIdentitySameWorldKeepsOneRow(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	first := roundsIDs(t, reviewRounds(t, s, &r))
	for range 3 {
		out := reviewRounds(t, s, &r)
		for need, id := range roundsIDs(t, out) {
			if want, ok := first[need]; ok && id != want {
				t.Fatal("revision minted a new row", need, want, id)
			}
		}
	}
	for _, id := range first {
		if !strings.HasSuffix(string(id), "-0") {
			t.Fatal("first generation is not 0", id)
		}
	}
}

func TestRoundsGoalIdentityWorldChangeInvalidatesAndMintsNewRow(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	first := roundsIDs(t, reviewRounds(t, s, &r))
	r.Current.Load = "another-load"
	out := reviewRounds(t, s, &r)
	for need, id := range roundsIDs(t, out) {
		if old, ok := first[need]; ok {
			if id == old || id[:24] == old[:24] {
				t.Fatal("different world reused identity", old, id)
			}
			g, err := s.LoadStandard(t.Context(), old)
			if err != nil {
				t.Fatal(err)
			}
			if g.Standard.Status != domain.StandardVoided {
				t.Fatal("world change did not invalidate", old, g.Standard.Status)
			}
		}
	}
}

func TestRoundsGoalIdentityRewindBumpsGeneration(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	first := roundsIDs(t, reviewRounds(t, s, &r))
	r.Tick = 1
	out := reviewRounds(t, s, &r)
	for need, id := range roundsIDs(t, out) {
		old, ok := first[need]
		if !ok {
			continue
		}
		if want := strings.TrimSuffix(string(old), "-0") + "-1"; string(id) != want {
			t.Fatal("rewind did not bump generation", old, id)
		}
		g, err := s.LoadStandard(t.Context(), old)
		if err != nil {
			t.Fatal(err)
		}
		if g.Standard.Status != domain.StandardVoided {
			t.Fatal("rewound row not terminal", g.Standard.Status)
		}
	}
}
