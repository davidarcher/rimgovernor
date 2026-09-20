package cases

import (
	"context"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// A -through run ends after its stage: the Run context is cut with a
// cause stagedThrough reads back, the report says which stage and how,
// and any other stage passes through untouched.
func TestEndThroughCutsTheRunAfterItsStage(t *testing.T) {
	ctx, cut := context.WithCancelCause(context.Background())
	defer cut(nil)
	s := &session{through: "kitchen", cutRun: cut, report: na.NewReport("t", true), stagesDir: "stages"}
	if err := s.endThrough("feed", "captured"); err != nil || ctx.Err() != nil {
		t.Fatalf("an earlier stage ended the run: %v %v", err, ctx.Err())
	}
	if err := s.endThrough("kitchen", "hit"); err == nil {
		t.Fatal("the through stage did not end the run")
	}
	through := stagedThrough(ctx)
	if through == nil || through.Stage != "kitchen" || through.Outcome != "hit" {
		t.Errorf("stagedThrough = %+v", through)
	}
	block, _ := s.report["staged_through"].(map[string]any)
	if block["stage"] != "kitchen" || block["outcome"] != "hit" {
		t.Errorf("report = %v", s.report["staged_through"])
	}
	if stagedThrough(context.Background()) != nil {
		t.Error("an uncut context reads as staged through")
	}
}

func TestCheckThrough(t *testing.T) {
	c := Case{Name: "x/chain", Stages: []string{"feed", "kitchen"}}
	t.Setenv(StagesEnv, "")
	if err := checkThrough(c, Options{}); err != nil {
		t.Errorf("no -through: %v", err)
	}
	if err := checkThrough(c, Options{Through: "kitchen"}); err != nil {
		t.Errorf("declared stage: %v", err)
	}
	for name, opts := range map[string]Options{
		"undeclared": {Through: "cold"},
		"postmortem": {Through: "feed", PostmortemOnly: true},
		"repeat":     {Through: "feed", Repeat: 2},
		"break":      {Through: "feed", Break: na.Breakpoint{Stage: "feed"}},
	} {
		if err := checkThrough(c, opts); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	if err := checkThrough(Case{Name: "x/plain"}, Options{Through: "feed"}); err == nil {
		t.Error("a case without stages accepted -through")
	}
	t.Setenv(StagesEnv, "0")
	if err := checkThrough(c, Options{Through: "feed"}); err == nil {
		t.Error("staging off accepted -through")
	}
}
