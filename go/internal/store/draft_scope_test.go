package store

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestDraftScopeSupersessionReopensWithoutInventingEvidence(t *testing.T) {
	for _, known := range []bool{false, true} {
		name := "unknown"
		if known {
			name = "owned-release-dispatched"
		}
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			s, path, v, a := draftFixture(t)
			claim := draftDispatch(t, s, v, a)
			if known {
				if _, err := s.RecordDraftReceipt(ctx, v.Plan, v.Action, 1, domain.ReceiptAccepted, domain.Known(claim)); err != nil {
					t.Fatal(err)
				}
				if _, err := s.BeginDraftCleanup(ctx, v.Plan, v.Action, domain.DraftReleaseRequest{Claim: claim, PawnSnapshotToken: "exact-token", Observed: a.Snapshot, Tick: a.Tick}); err != nil {
					t.Fatal(err)
				}
			}
			before := draftProgress(t, s, v).View()
			prior, _ := before.DraftCleanup.Value()
			observed := a.Snapshot
			observed.Load = "new-load"
			observed.Native = 0
			event := domain.DraftScopeSupersession{Action: v.Action, Attempt: 1, Origin: a.Snapshot, Observed: observed, Tick: 0}
			if _, err := s.ObserveDraftScopeSupersession(ctx, v.Plan, event); err != nil {
				t.Fatal(err)
			}
			s.Close()
			s = open(t, path)
			after := draftProgress(t, s, v).View()
			cleanup, _ := after.DraftCleanup.Value()
			if cleanup.Stage != domain.DraftSuperseded || cleanup.Claim != prior.Claim || cleanup.Release != prior.Release {
				t.Fatal("changed original evidence", cleanup)
			}
			after.DraftCleanup = before.DraftCleanup
			if after != before {
				t.Fatal("changed ordinary progress")
			}
			if _, err := s.ObserveDraftScopeSupersession(ctx, v.Plan, event); err == nil {
				t.Fatal("replayed intent admitted twice")
			}
			if known {
				release, _ := prior.Release.Value()
				if _, err := s.RecordDraftCleanup(ctx, v.Plan, v.Action, release, domain.DraftReleaseConfirmed); err == nil {
					t.Fatal("late cleanup response accepted")
				}
			}
		})
	}
}

func TestDraftScopeSupersessionRollbackAndInvalidEvidence(t *testing.T) {
	ctx := context.Background()
	s, _, v, a := draftFixture(t)
	draftDispatch(t, s, v, a)
	before := draftProgress(t, s, v).View()
	observed := a.Snapshot
	observed.Map++
	event := domain.DraftScopeSupersession{Action: v.Action, Attempt: 1, Origin: a.Snapshot, Observed: observed, Tick: 0}
	if _, err := s.db.Exec("CREATE TRIGGER fail_scope BEFORE INSERT ON transitions BEGIN SELECT RAISE(ABORT,'injected'); END"); err != nil {
		t.Fatal(err)
	}
	if _, err := s.ObserveDraftScopeSupersession(ctx, v.Plan, event); err == nil {
		t.Fatal("expected transaction failure")
	}
	if draftProgress(t, s, v).View() != before {
		t.Fatal("partial supersession")
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_scope"); err != nil {
		t.Fatal(err)
	}
	invalid := event
	invalid.Origin.Direction++
	if _, err := s.ObserveDraftScopeSupersession(ctx, v.Plan, invalid); err == nil {
		t.Fatal("wrong origin accepted")
	}
	invalid = event
	invalid.Observed = event.Origin
	if _, err := s.ObserveDraftScopeSupersession(ctx, v.Plan, invalid); err == nil {
		t.Fatal("same world accepted")
	}
	if draftProgress(t, s, v).View() != before {
		t.Fatal("invalid evidence changed state")
	}
}

func TestDraftScopeEventRejectsCorruption(t *testing.T) {
	for _, kind := range []string{"missing", "extra-arm", "wrong-origin", "unknown-field", "foreign-namespace"} {
		t.Run(kind, func(t *testing.T) {
			ctx := context.Background()
			s, path, v, a := draftFixture(t)
			claim := draftDispatch(t, s, v, a)
			if _, err := s.RecordDraftReceipt(ctx, v.Plan, v.Action, 1, domain.ReceiptAccepted, domain.Known(claim)); err != nil {
				t.Fatal(err)
			}
			observed := a.Snapshot
			observed.Colony = "replacement"
			event := domain.DraftScopeSupersession{Action: v.Action, Attempt: 1, Origin: a.Snapshot, Observed: observed, Tick: 0}
			if _, err := s.ObserveDraftScopeSupersession(ctx, v.Plan, event); err != nil {
				t.Fatal(err)
			}
			var data []byte
			var seq int
			if err := s.db.QueryRow("SELECT sequence,payload FROM transitions ORDER BY sequence DESC LIMIT 1").Scan(&seq, &data); err != nil {
				t.Fatal(err)
			}
			var stored transition
			if err := json.Unmarshal(data, &stored); err != nil {
				t.Fatal(err)
			}
			switch kind {
			case "missing":
				stored.DraftScopeSupersession = nil
			case "extra-arm":
				stored.DraftResult = &draftResultEvent{}
			case "wrong-origin":
				stored.DraftScopeSupersession.Origin.Native++
			case "unknown-field":
				data = append(data[:len(data)-1], []byte(",\"unknown\":1}")...)
			case "foreign-namespace":
				if _, err := s.db.Exec("UPDATE metadata SET controller_session_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'"); err != nil {
					t.Fatal(err)
				}
			}
			if kind != "unknown-field" {
				data, _ = json.Marshal(stored)
			}
			if _, err := s.db.Exec("UPDATE transitions SET payload=? WHERE sequence=?", data, seq); err != nil {
				t.Fatal(err)
			}
			s.Close()
			s = open(t, path)
			if _, err := s.LoadPlan(ctx, v.Plan); err == nil {
				t.Fatal("corrupt persisted evidence accepted")
			}
		})
	}
}
