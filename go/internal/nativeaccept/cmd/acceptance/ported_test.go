package main

import (
	"bytes"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// TestPortedLoudLifecycleVideoCases pins the #145 cases' declarations: the
// Loud ones say why, the lifecycle ones stop the process, the video ones
// open the windowed profile, and the two that own their process do both.
func TestPortedLoudLifecycleVideoCases(t *testing.T) {
	for name, want := range map[string]struct {
		quiet    na.QuietMode
		noKeep   bool
		rendered bool
		owned    bool
	}{
		"combat/melee":            {quiet: na.Loud},
		"combat/ranged":           {quiet: na.Loud},
		"combat/explosive":        {quiet: na.Loud},
		"movement/arrival":        {quiet: na.Loud},
		"authority/disconnect":    {quiet: na.Loud},
		"lifecycle/shutdown":      {noKeep: true},
		"lifecycle/runtime-fault": {noKeep: true},
		"lifecycle/reuse":         {noKeep: true, owned: true},
		"lifecycle/headless-soak": {noKeep: true, owned: true},
		"video/stream":            {quiet: na.QuietIfAvailable, rendered: true},
		"video/feeds":             {quiet: na.QuietIfAvailable, rendered: true},
		"video/matrix":            {rendered: true},
		"video/source-spike":      {rendered: true},
	} {
		c, ok := cases.Lookup(name)
		if !ok {
			t.Errorf("%s is not registered", name)
			continue
		}
		if c.Quiet != want.quiet || c.NoKeep != want.noKeep || c.Rendered != want.rendered {
			t.Errorf("%s: quiet=%s noKeep=%v rendered=%v", name, c.Quiet, c.NoKeep, c.Rendered)
		}
		if _, owned := c.Start.(cases.Owned); owned != want.owned {
			t.Errorf("%s: Owned start = %v", name, owned)
		}
		if c.Quiet != na.QuietRequired && c.Reason == "" {
			t.Errorf("%s: %s without a Reason", name, c.Quiet)
		}
		if c.Budget <= 0 || c.Budget > 15*60e9 {
			t.Errorf("%s: budget %s", name, c.Budget)
		}
	}
}

func TestStopNeedsRoot(t *testing.T) {
	var stdout, stderr bytes.Buffer
	if code := run([]string{"stop"}, &stdout, &stderr); code != 2 {
		t.Fatalf("stop without -root exit %d: %s", code, stderr.String())
	}
}
