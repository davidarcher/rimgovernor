package store

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	"math"
	"reflect"
	"strings"
	"testing"
)

func windowStoreFixture(t *testing.T) (*Store, string, string, ClockIntent) {
	t.Helper()
	s, path, profile := boundInbox(t)
	inbox, err := s.ReadClockInbox(context.Background(), profile)
	if err != nil {
		t.Fatal(err)
	}
	intent := clockIntent(clockTestID(t, s, "window"))
	intent.Window = &ClockWindowAdmission{Profile: inbox.Profile, Snapshot: intent.Snapshot, Tick: 10, MaxTicks: intent.Command.Start.MaxTicks}
	return s, path, profile, intent
}
func TestClockWindowStoreReplayCloneAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, profile, input := windowStoreFixture(t)
	saved, created, err := s.PrepareClock(ctx, input)
	if err != nil || !created || !reflect.DeepEqual(saved.Intent.Window, input.Window) {
		t.Fatal(saved, created, err)
	}
	input.Window.Tick = 11
	if saved.Intent.Window.Tick != 10 {
		t.Fatal("input alias")
	}
	if _, _, err = s.PrepareClock(ctx, input); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	input.Window.Tick = 10
	replay, created, err := s.PrepareClock(ctx, input)
	if err != nil || created || !reflect.DeepEqual(replay.Intent.Window, saved.Intent.Window) {
		t.Fatal(replay, err)
	}
	saved.Intent.Window.CapturedCursor = 99
	s.Close()
	s = open(t, path)
	loaded, err := s.LookupClockAttempt(ctx, input.RequestID)
	if err != nil || loaded.Intent.Window.CapturedCursor != 0 || loaded.Intent.Window.Profile != input.Window.Profile {
		t.Fatal(loaded, err)
	}
	if _, err = s.DispatchClock(ctx, input.RequestID); err != nil {
		t.Fatal(err)
	}
	// Later ingestion does not invalidate immutable already-dispatched recovery.
	request, page := inboxPage(0, 1, 0)
	if _, _, err = s.AppendClockEvents(ctx, profile, request, page); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LookupClockAttempt(ctx, input.RequestID); err != nil {
		t.Fatal(err)
	}
}
func TestClockWindowStoreDispatchReviewRaces(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	for _, kind := range []string{"append", "ack", "hold", "unreviewed"} {
		t.Run(kind, func(t *testing.T) {
			s, _, profile, input := windowStoreFixture(t)
			if kind != "append" {
				request, page := inboxPage(0, 1, 0)
				if kind == "hold" {
					reviewAppend(t, s, profile, 0, 0)
				} else {
					if _, _, err := s.AppendClockEvents(ctx, profile, request, page); err != nil {
						t.Fatal(err)
					}
				}
				if kind != "unreviewed" {
					review, err := s.ReviewClockEvents(ctx, profile, 0)
					if err != nil {
						t.Fatal(err)
					}
					input.Window.ReviewRevision = review.Revision
				}
				input.Window.CapturedCursor = 1
			}
			if _, _, err := s.PrepareClock(ctx, input); err != nil {
				t.Fatal(err)
			}
			if kind == "append" {
				request, page := inboxPage(0, 1, 0)
				if _, _, err := s.AppendClockEvents(ctx, profile, request, page); err != nil {
					t.Fatal(err)
				}
			}
			if kind == "ack" {
				if _, err := s.AcknowledgeClockEvents(ctx, profile, ClockAcknowledgement{"ack", 1, 1}); err != nil {
					t.Fatal(err)
				}
			}
			if _, err := s.DispatchClock(ctx, input.RequestID); !errors.Is(err, ErrConflict) {
				t.Fatal("generic dispatch bypassed review", err)
			}
			saved, err := s.LookupClockAttempt(ctx, input.RequestID)
			if err != nil || saved.Phase != ClockPrepared {
				t.Fatal(saved, err)
			}
		})
	}
}
func TestClockWindowStoreInvalidAdmission(t *testing.T) {
	t.Parallel()
	cases := map[string]func(*ClockIntent){
		"snapshot": func(v *ClockIntent) { v.Window.Snapshot.Direction++ }, "negative tick": func(v *ClockIntent) { v.Window.Tick = -1 }, "overflow": func(v *ClockIntent) { v.Window.Tick = domain.Tick(math.MaxInt64) }, "budget mismatch": func(v *ClockIntent) { v.Window.MaxTicks++ }, "zero budget": func(v *ClockIntent) { v.Window.MaxTicks = 0 }, "excess": func(v *ClockIntent) { v.Window.MaxTicks = 1800001 }, "negative cursor": func(v *ClockIntent) { v.Window.CapturedCursor = -1 }, "relative profile": func(v *ClockIntent) { v.Window.Profile = "relative" }, "foreign profile": func(v *ClockIntent) { v.Window.Profile = t.TempDir() }, "profile NUL": func(v *ClockIntent) { v.Window.Profile += "\x00" }, "profile long": func(v *ClockIntent) { v.Window.Profile += strings.Repeat("x", 4096) }, "not start": func(v *ClockIntent) { v.Command = bridge.ClockCommand{Renew: &bridge.ClockRenew{}} },
	}
	for name, edit := range cases {
		t.Run(name, func(t *testing.T) {
			s, _, _, input := windowStoreFixture(t)
			edit(&input)
			if _, _, err := s.PrepareClock(context.Background(), input); err == nil {
				t.Fatal("accepted invalid window")
			}
			var count int
			if err := s.db.QueryRow("SELECT count(*) FROM clock_attempts").Scan(&count); err != nil || count != 0 {
				t.Fatal(count, err)
			}
		})
	}
}
func TestClockWindowStoreCorruptionAndRollback(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	t.Run("atomic dispatch", func(t *testing.T) {
		s, _, _, input := windowStoreFixture(t)
		if _, _, err := s.PrepareClock(ctx, input); err != nil {
			t.Fatal(err)
		}
		if _, err := s.db.Exec(`CREATE TRIGGER reject_dispatch BEFORE UPDATE ON clock_attempts BEGIN SELECT RAISE(ABORT,'fixture'); END`); err != nil {
			t.Fatal(err)
		}
		if _, err := s.DispatchClock(ctx, input.RequestID); err == nil {
			t.Fatal("expected rollback")
		}
		v, err := s.LookupClockAttempt(ctx, input.RequestID)
		if err != nil || v.Phase != ClockPrepared {
			t.Fatal(v, err)
		}
	})
	for _, kind := range []string{"unknown field", "budget", "missing profile", "corrupt review"} {
		t.Run(kind, func(t *testing.T) {
			s, path, _, input := windowStoreFixture(t)
			if _, _, err := s.PrepareClock(ctx, input); err != nil {
				t.Fatal(err)
			}
			switch kind {
			case "missing profile":
				if _, err := s.db.Exec("DELETE FROM clock_inbox"); err != nil {
					t.Fatal(err)
				}
			case "corrupt review":
				if _, err := s.db.Exec("DELETE FROM clock_review"); err != nil {
					t.Fatal(err)
				}
			default:
				var payload []byte
				if err := s.db.QueryRow("SELECT payload FROM clock_attempts").Scan(&payload); err != nil {
					t.Fatal(err)
				}
				if kind == "unknown field" {
					payload = []byte(strings.Replace(string(payload), `"Window":{`, `"Window":{"Unexpected":true,`, 1))
				} else {
					var record clock.IntentRecord
					if err := json.Unmarshal(payload, &record); err != nil {
						t.Fatal(err)
					}
					record.Window.MaxTicks++
					payload, _ = json.Marshal(record)
				}
				if _, err := s.db.Exec("UPDATE clock_attempts SET payload=?", payload); err != nil {
					t.Fatal(err)
				}
			}
			s.Close()
			s = open(t, path)
			if kind == "corrupt review" {
				if _, err := s.DispatchClock(ctx, input.RequestID); err == nil {
					t.Fatal("dispatch without review provenance")
				}
			} else if _, err := s.LookupClockAttempt(ctx, input.RequestID); err == nil {
				t.Fatal("accepted corrupt window")
			}
		})
	}
}
func TestClockWindowStorePlainStartStillExplicit(t *testing.T) {
	t.Parallel()
	s, _, _, _ := windowStoreFixture(t)
	ctx := context.Background()
	if _, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "explicit"))); err != nil {
		t.Fatal(err)
	}
	if _, err := s.DispatchClock(ctx, clockTestID(t, s, "explicit")); err != nil {
		t.Fatal(err)
	}
}
