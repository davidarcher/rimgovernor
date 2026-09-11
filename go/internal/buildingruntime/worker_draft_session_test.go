package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"google.golang.org/protobuf/proto"
)

// Drive real Session and executor work sequentially: the native fixture is not a
// background game and intentionally has no independently synchronized mutation.
func draftSessionWorker(t *testing.T, session *Session, journal *store.Store) *Worker {
	t.Helper()
	player, err := newPlayer(context.Background(), PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, journal, session, session.control.config.Worlds)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		if err := player.Close(context.Background()); err != nil {
			t.Error(err)
		}
	})
	return &Worker{player: player, session: session, config: WorkerConfig{StepInterval: 10 * time.Millisecond, MaxBackoff: time.Second, StepTimeout: time.Second}, waits: make(map[domain.ActionID]workerWait)}
}

func TestWorkerActualDraftSessionReleasesDisabledStandalone(t *testing.T) {
	session, native, journal, _ := draftSessionFixture(t, false)
	worker := draftSessionWorker(t, session, journal)
	if session.State().Enabled {
		t.Fatal("fixture unexpectedly enabled")
	}
	before, err := journal.LoadPlan(context.Background(), "plan")
	if err != nil {
		t.Fatal(err)
	}
	if before.Progress[0].View().Stage != domain.Completed || before.Progress[0].View().Unresolved {
		t.Fatal("requires terminal ordinary progress")
	}
	now := time.Now()
	if err = worker.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	after, err := journal.LoadPlan(context.Background(), "plan")
	if err != nil {
		t.Fatal(err)
	}
	cleanup, _ := after.Progress[0].View().DraftCleanup.Value()
	if cleanup.Stage != domain.DraftReleased || native.releases != 1 || native.writes != 0 || after.Progress[0].View().Attempt != 1 {
		t.Fatal(cleanup, native.releases, native.writes)
	}
	for i := 1; i <= 3; i++ {
		if err = worker.step(context.Background(), now.Add(time.Duration(i)*time.Second)); err != nil {
			t.Fatal(err)
		}
	}
	if native.releases != 1 || len(worker.waits) != 0 {
		t.Fatal("terminal cleanup polled again")
	}
	authority := session.control.native.(*controlNative)
	if authority.acquires.Load() != 0 || authority.renews.Load() != 0 || authority.revokes.Load() != 0 || session.State().Enabled {
		t.Fatal("cleanup mutated authority")
	}
}

func TestWorkerActualDraftSessionRecoversUnknownBeforeRelease(t *testing.T) {
	session, native, journal, _ := draftSessionFixture(t, true)
	worker := draftSessionWorker(t, session, journal)
	now := time.Now()
	if err := worker.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	state, err := journal.LoadPlan(context.Background(), "plan")
	if err != nil {
		t.Fatal(err)
	}
	cleanup, _ := state.Progress[0].View().DraftCleanup.Value()
	claim, known := cleanup.Claim.Value()
	if !known || claim.Attempt != 1 || cleanup.Stage != domain.DraftCleanupRequired || native.releases != 0 || native.lookups != 1 {
		t.Fatal("release preceded durable reconciliation", cleanup, native.releases, native.lookups)
	}
	if err = worker.step(context.Background(), now.Add(worker.config.StepInterval)); err != nil {
		t.Fatal(err)
	}
	state, err = journal.LoadPlan(context.Background(), "plan")
	if err != nil {
		t.Fatal(err)
	}
	cleanup, _ = state.Progress[0].View().DraftCleanup.Value()
	retained, _ := cleanup.Claim.Value()
	if cleanup.Stage != domain.DraftReleased || retained != claim || native.releases != 1 || native.writes != 0 {
		t.Fatal(cleanup, native.releases, native.writes)
	}
	if err = worker.step(context.Background(), now.Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	authority := session.control.native.(*controlNative)
	if native.releases != 1 || authority.acquires.Load() != 0 || authority.renews.Load() != 0 || authority.revokes.Load() != 0 || session.State().ObservationKnown {
		t.Fatal("cleanup entered control target or reacquired")
	}
}

func TestWorkerActualDraftSessionRetiresReplacementWorldWithoutRelease(t *testing.T) {
	for _, unknown := range []bool{false, true} {
		name := "known-claim"
		if unknown {
			name = "unknown-claim"
		}
		t.Run(name, func(t *testing.T) {
			session, native, journal, _ := draftSessionFixture(t, unknown)
			worker := draftSessionWorker(t, session, journal)
			before, err := journal.LoadPlan(context.Background(), "plan")
			if err != nil {
				t.Fatal(err)
			}
			original := before.Progress[0].View()
			oldCleanup, _ := original.DraftCleanup.Value()
			native.context.Identity.LoadToken = proto.String("replacement-load")
			native.context.Tick = proto.Int64(0)
			native.context.NativeGeneration = nil
			if err = worker.step(context.Background(), time.Now()); err != nil {
				t.Fatal(err)
			}
			after, err := journal.LoadPlan(context.Background(), "plan")
			if err != nil {
				t.Fatal(err)
			}
			v := after.Progress[0].View()
			cleanup, _ := v.DraftCleanup.Value()
			if cleanup.Stage != domain.DraftSuperseded || cleanup.Claim != oldCleanup.Claim || cleanup.Release != oldCleanup.Release {
				t.Fatal("replacement invented ownership/release", cleanup)
			}
			v.DraftCleanup = original.DraftCleanup
			if v != original {
				t.Fatal("original progress evidence changed")
			}
			if native.releases != 0 || native.writes != 0 || native.reads != 0 || native.lookups != 0 {
				t.Fatal("called original world", native.releases, native.writes, native.reads, native.lookups)
			}
			authority := session.control.native.(*controlNative)
			if authority.acquires.Load() != 0 || authority.renews.Load() != 0 || authority.revokes.Load() != 0 || session.State().Enabled || session.State().ObservationKnown {
				t.Fatal("replacement acquired control scope")
			}
			if err = worker.step(context.Background(), time.Now().Add(time.Second)); err != nil {
				t.Fatal(err)
			}
			if len(worker.waits) != 0 {
				t.Fatal("superseded cleanup retained polling wait")
			}
		})
	}
}
