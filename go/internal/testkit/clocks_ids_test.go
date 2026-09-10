package testkit

import (
	"sync"
	"testing"
	"time"
)

func TestInjectedReplayClockAndIDs(t *testing.T) {
	start := time.Date(2026, 9, 10, 12, 0, 0, 0, time.UTC)
	clock := NewManualClock(start)
	ids := []ID{"action-α", "receipt-1"}
	source := NewSequenceIDs(ids)
	ids[0] = "changed-outside-source"
	for index, want := range []ID{"action-α", "receipt-1"} {
		id, err := source.Next()
		if err != nil || id != want || !clock.Now().Equal(start.Add(time.Duration(index)*time.Second)) {
			t.Fatalf("replay step %d: id=%q time=%v error=%v", index, id, clock.Now(), err)
		}
		clock.Advance(time.Second)
	}
	for range 2 {
		if _, err := source.Next(); err == nil {
			t.Fatal("exhaustion invented an ID")
		}
	}
	clock.Advance(-3 * time.Second)
	if !clock.Now().Equal(start.Add(-time.Second)) {
		t.Fatal("explicit rewind was lost")
	}
	if _, err := NewSequenceIDs(nil).Next(); err == nil {
		t.Fatal("empty sequence invented an ID")
	}
}

func TestReplaySourcesConcurrentAccess(t *testing.T) {
	clock := NewManualClock(time.Time{})
	source := NewSequenceIDs([]ID{"a", "b"})
	results := make(chan ID, 2)
	var workers sync.WaitGroup
	for range 2 {
		workers.Go(func() {
			clock.Advance(time.Second)
			_ = clock.Now()
			id, err := source.Next()
			if err != nil {
				t.Error(err)
			}
			results <- id
		})
	}
	workers.Wait()
	close(results)
	seen := make(map[ID]bool)
	for id := range results {
		seen[id] = true
	}
	if !seen["a"] || !seen["b"] || !clock.Now().Equal(time.Time{}.Add(2*time.Second)) {
		t.Fatalf("lost source update: IDs=%v time=%v", seen, clock.Now())
	}
}
