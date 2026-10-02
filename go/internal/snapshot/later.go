package snapshot

import "sync"

// Recording is evidence for replay, never part of a decision, and encoding
// a 250x250 planning window costs 1-2 s: Later runs a recording job on one
// background goroutine, in submission order, so the facts store's lock, the
// player gate and the planner steps never wait on it. Jobs keep what they
// were handed (a mirror table's rows are never mutated after publishing;
// a colony reading is read-only once taken), so a job reads it later.
var later struct {
	mu      sync.Mutex
	idle    sync.Cond
	jobs    []func()
	running bool
}

func init() { later.idle.L = &later.mu }

// Later queues job behind the recordings already queued.
func Later(job func()) {
	later.mu.Lock()
	defer later.mu.Unlock()
	later.jobs = append(later.jobs, job)
	if !later.running {
		later.running = true
		go drainLater()
	}
}

func drainLater() {
	later.mu.Lock()
	for len(later.jobs) > 0 {
		job := later.jobs[0]
		later.jobs[0] = nil
		later.jobs = later.jobs[1:]
		later.mu.Unlock()
		job()
		later.mu.Lock()
	}
	later.running = false
	later.idle.Broadcast()
	later.mu.Unlock()
}

// Flush returns once every recording queued before it is written: a serve
// calls it on its way out, a test before it reads the stream.
func Flush() {
	later.mu.Lock()
	defer later.mu.Unlock()
	for later.running {
		later.idle.Wait()
	}
}
