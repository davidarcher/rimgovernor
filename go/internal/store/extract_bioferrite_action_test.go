package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An extract_bioferrite pawn setting persists its pawn and flag.
func TestExtractBioferriteSettingRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	on, err := domain.NewExtractBioferriteSetting("entity-3", true)
	if err != nil {
		t.Fatal(err)
	}
	off, err := domain.NewExtractBioferriteSetting("entity-4", false)
	if err != nil {
		t.Fatal(err)
	}
	var actions []domain.Action
	for id, value := range map[domain.ActionID]domain.PawnSettings{"bio-on": on, "bio-off": off} {
		a, err := domain.NewPawnSettingsAction(id, value)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	p, err := domain.NewPlan("bio-plan", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "bio-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 2 {
		t.Fatal(got)
	}
	for _, a := range got {
		v, ok := a.PawnSettings()
		flag, isExtract := v.ExtractBioferrite()
		if !ok || !isExtract || v != on && v != off || flag != (v == on) {
			t.Fatal(v)
		}
	}
}
