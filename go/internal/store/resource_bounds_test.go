package store

import (
	"context"
	"strings"
	"testing"
)

func TestAdmissionResourceContractBounds(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name  string
		valid bool
	}{
		{strings.Repeat("x", 256), true}, {strings.Repeat("x", 257), false},
		{strings.Repeat("🧱", 64), true}, {strings.Repeat("🧱", 65), false},
		{"wood\x00", false}, {string([]byte{0xff}), false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s, path := fixture(t)
			a := evidence(10, 1)
			a.Costs[0].Definition = tc.name
			_, err := s.ReserveAndPrepare(context.Background(), "p", "a", a)
			if (err == nil) != tc.valid {
				t.Fatalf("admission error=%v valid=%v", err, tc.valid)
			}
			if !tc.valid {
				return
			}
			if err = s.Close(); err != nil {
				t.Fatal(err)
			}
			reopened := open(t, path)
			state, err := reopened.LoadPlan(context.Background(), "p")
			if err != nil || len(state.Admissions) != 1 || state.Admissions[0].Admission.Costs[0].Definition != tc.name {
				t.Fatalf("reopened resource lost: %v", err)
			}
		})
	}
}
