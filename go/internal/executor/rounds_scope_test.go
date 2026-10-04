package executor

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type roundsScopeFunc func(context.Context, domain.GenerationSnapshot, domain.GenerationSnapshot) error

func (f roundsScopeFunc) AuthorizeRoundsPlan(ctx context.Context, root, target domain.GenerationSnapshot) error {
	return f(ctx, root, target)
}

func TestRoundsScopeRecheckedBeforeDispatchWithoutChangingAuthority(t *testing.T) {
	for _, revoke := range []bool{false, true} {
		f := newFixture(t)
		root := f.authority
		root.Snapshot.Plan = "player-plan"
		if err := f.executor.UpdateAuthority(root); err != nil {
			t.Fatal(err)
		}
		allowed := true
		checks := 0
		f.executor.roundsScope = roundsScopeFunc(func(_ context.Context, actual, target domain.GenerationSnapshot) error {
			checks++
			if actual != root.Snapshot || target != f.authority.Snapshot || !allowed {
				return ErrAuthority
			}
			return nil
		})
		f.env.onInspect = func(_ int, in IntentInspection) IntentInspection {
			if revoke {
				allowed = false
			}
			return in
		}
		result, err := f.executor.runOne(context.Background(), f.plan.ID(), f.action.ID())
		if revoke {
			if !errors.Is(err, ErrAuthority) || result.NativeCalled {
				t.Fatal(result, err)
			}
		} else if err != nil || !result.NativeCalled || checks < 2 {
			t.Fatal(result, err, checks)
		}
		if f.executor.current() != root {
			t.Fatal("routine execution changed player authority")
		}
	}
}
