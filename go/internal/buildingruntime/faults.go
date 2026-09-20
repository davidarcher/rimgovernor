package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"strings"
)

// Faults are the acceptance harness's fault injections into a live
// scheduler (#633): a named catalog planner that fails every step, one
// that never returns (blocks until its step context ends) and an epoch
// renewal that silently does nothing so the native lease lapses. They
// exist so the campaign/* cases can prove which failures leave safe play
// running (an optional planner) and which stop it (a critical planner held
// past the wall budget, authority revoked after the lease expired). Parsed
// from FaultsEnv by serve; never set in ordinary play.
type Faults struct {
	// FailPlanners names catalog planners whose run returns errFaultInjected
	// at once, before any native read.
	FailPlanners map[string]bool
	// HangPlanners names catalog planners whose run blocks until the step
	// (critical) or wave (optional) context ends.
	HangPlanners map[string]bool
	// DropRenewal makes RenewEpoch return without renewing the owned epoch:
	// native stops the clock lease_expired and revokes authority at the
	// next generation.
	DropRenewal bool
}

// FaultsEnv names the environment variable serve reads Faults from: a
// semicolon-separated list of `planner:<name>=fail`, `planner:<name>=hang`
// and `renewal=drop`.
const FaultsEnv = "RIMGOVERNOR_FAULT_INJECT"

var errFaultInjected = errors.New("fault injected")

// ParseFaults reads FaultsEnv's format; an empty string is no fault.
func ParseFaults(raw string) (Faults, error) {
	var f Faults
	for _, item := range strings.Split(raw, ";") {
		item = strings.TrimSpace(item)
		if item == "" {
			continue
		}
		key, value, ok := strings.Cut(item, "=")
		if !ok {
			return Faults{}, fmt.Errorf("%s: %q is not key=value", FaultsEnv, item)
		}
		switch {
		case key == "renewal" && value == "drop":
			f.DropRenewal = true
		case strings.HasPrefix(key, "planner:"):
			name := strings.TrimPrefix(key, "planner:")
			if name == "" {
				return Faults{}, fmt.Errorf("%s: %q names no planner", FaultsEnv, item)
			}
			switch value {
			case "fail":
				if f.FailPlanners == nil {
					f.FailPlanners = map[string]bool{}
				}
				f.FailPlanners[name] = true
			case "hang":
				if f.HangPlanners == nil {
					f.HangPlanners = map[string]bool{}
				}
				f.HangPlanners[name] = true
			default:
				return Faults{}, fmt.Errorf("%s: planner fault %q is not fail or hang", FaultsEnv, value)
			}
		default:
			return Faults{}, fmt.Errorf("%s: unknown fault %q", FaultsEnv, item)
		}
	}
	return f, nil
}

// Empty reports no fault configured.
func (f Faults) Empty() bool {
	return len(f.FailPlanners) == 0 && len(f.HangPlanners) == 0 && !f.DropRenewal
}

// String renders the faults in FaultsEnv's format, for the startup log.
func (f Faults) String() string {
	var parts []string
	for name := range f.FailPlanners {
		parts = append(parts, "planner:"+name+"=fail")
	}
	for name := range f.HangPlanners {
		parts = append(parts, "planner:"+name+"=hang")
	}
	if f.DropRenewal {
		parts = append(parts, "renewal=drop")
	}
	return strings.Join(parts, ";")
}

// Validate refuses a planner name the catalog does not know, so a typo
// never passes for a clean run.
func (f Faults) Validate() error {
	known := map[string]bool{}
	for _, entry := range plannerCatalog {
		known[entry.name] = true
	}
	for _, names := range []map[string]bool{f.FailPlanners, f.HangPlanners} {
		for name := range names {
			if !known[name] {
				return fmt.Errorf("%s: no catalog planner named %q", FaultsEnv, name)
			}
		}
	}
	return nil
}

// plannerFault wraps run with the fault configured for the named planner,
// if any: a failing planner returns at once, a hanging one waits out ctx.
func (f Faults) plannerFault(name string, ctx context.Context, run func() error) func() error {
	switch {
	case f.FailPlanners[name]:
		return func() error { return fmt.Errorf("%w: %s fails every step", errFaultInjected, name) }
	case f.HangPlanners[name]:
		return func() error {
			<-ctx.Done()
			return fmt.Errorf("%w: %s hung until %w", errFaultInjected, name, ctx.Err())
		}
	}
	return run
}
