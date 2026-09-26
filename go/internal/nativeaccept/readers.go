package nativeaccept

import (
	"context"
	"fmt"
	"sort"
	"sync"
	"time"
)

// ObservationReaders are the observation-load row's concurrent readers
// (#656): Count clients each polling /api/state every Interval for the whole
// row, the way several open dashboards read the service's published view.
// They add read pressure beside the controller's own reads; they never
// change anything. Latencies are recorded so a reader starved by the
// controller (or the controller by them) is visible in the row.
type ObservationReaders struct {
	cancel context.CancelFunc
	wg     sync.WaitGroup
	mu     sync.Mutex
	count  int
	reads  uint64
	errors uint64
	lat    []float64
	start  time.Time
}

// StartObservationReaders starts count readers against the service.
func StartObservationReaders(ctx context.Context, service *ServiceProcess, count int, interval time.Duration) *ObservationReaders {
	ctx, cancel := context.WithCancel(ctx)
	r := &ObservationReaders{cancel: cancel, count: count, start: time.Now()}
	for i := 0; i < count; i++ {
		r.wg.Add(1)
		go func() {
			defer r.wg.Done()
			ticker := time.NewTicker(interval)
			defer ticker.Stop()
			for {
				select {
				case <-ctx.Done():
					return
				case <-ticker.C:
				}
				began := time.Now()
				_, status, err := service.API("GET", "/api/state", nil, "")
				ms := float64(time.Since(began).Microseconds()) / 1000
				r.mu.Lock()
				if err != nil || status != 200 {
					r.errors++
				} else {
					r.reads++
					r.lat = append(r.lat, ms)
				}
				r.mu.Unlock()
			}
		}()
	}
	return r
}

// Stop ends the readers and summarizes what they read.
func (r *ObservationReaders) Stop() map[string]any {
	r.cancel()
	r.wg.Wait()
	r.mu.Lock()
	defer r.mu.Unlock()
	lat := append([]float64(nil), r.lat...)
	sort.Float64s(lat)
	rank := func(q float64) float64 {
		if len(lat) == 0 {
			return 0
		}
		i := int(q*float64(len(lat))+0.999999) - 1
		if i < 0 {
			i = 0
		}
		if i >= len(lat) {
			i = len(lat) - 1
		}
		return lat[i]
	}
	return map[string]any{
		"readers": r.count, "reads": r.reads, "errors": r.errors, "duration_s": time.Since(r.start).Seconds(),
		"read_p50_ms": rank(0.50), "read_p95_ms": rank(0.95), "read_max_ms": rank(1),
	}
}

// ReaderProblems refuses a readers summary that shows no load was applied:
// a row claiming observation load must have read successfully.
func ReaderProblems(summary map[string]any) []string {
	reads, _ := summary["reads"].(uint64)
	if reads == 0 {
		return []string{fmt.Sprintf("observation readers made no successful read (%v errors)", summary["errors"])}
	}
	return nil
}
