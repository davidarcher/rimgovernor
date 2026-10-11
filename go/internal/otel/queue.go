package otel

import (
	"context"
	"sync"
	"sync/atomic"
	"time"
)

// Drop reasons (the bounded `reason` dimension of the dropped metric).
const (
	ReasonQueueFull    = "queue_full"
	ReasonExportFailed = "export_failed"
)

// dropStats counts what one pipeline (a signal going to a sink) lost.
type dropStats struct {
	signal, sink string
	queueFull    atomic.Uint64
	exportFailed atomic.Uint64

	warnEvery time.Duration
	warn      func(msg string, args ...any)
	lastWarn  atomic.Int64 // unix nanos
	unwarned  atomic.Uint64
}

// DropCount is one pipeline's cumulative loss, for tests and status surfaces.
type DropCount struct {
	Signal, Sink, Reason string
	Count                uint64
}

func (d *dropStats) add(reason string, n uint64) {
	if n == 0 {
		return
	}
	if reason == ReasonQueueFull {
		d.queueFull.Add(n)
	} else {
		d.exportFailed.Add(n)
	}
	d.unwarned.Add(n)
	now := time.Now().UnixNano()
	last := d.lastWarn.Load()
	if now-last < int64(d.warnEvery) || !d.lastWarn.CompareAndSwap(last, now) {
		return
	}
	if lost := d.unwarned.Swap(0); lost > 0 && d.warn != nil {
		d.warn("telemetry dropped items", "signal", d.signal, "sink", d.sink, "reason", reason, "dropped_since_last_warning", lost)
	}
}

func (d *dropStats) counts() []DropCount {
	return []DropCount{
		{d.signal, d.sink, ReasonQueueFull, d.queueFull.Load()},
		{d.signal, d.sink, ReasonExportFailed, d.exportFailed.Load()},
	}
}

// queue is a bounded asynchronous hand-off from a hot path to one exporter.
// offer never blocks: a full queue drops the item and counts it. One goroutine
// drains the queue in batches and calls export; a slow or stalled exporter holds
// only that goroutine (bounded by exportTimeout) while the queue fills and
// drops, so no caller ever waits on a sink. Each sink gets its own queue so a
// dead collector cannot starve the file sink.
type queue[T any] struct {
	stats         *dropStats
	items         chan T
	batchSize     int
	flushInterval time.Duration
	exportTimeout time.Duration
	// urgent marks an item whose batch must be exported now and made durable.
	urgent func(T) bool
	export func(ctx context.Context, batch []T, durable bool) error

	flushReq chan chan struct{}
	stop     chan struct{}
	stopped  atomic.Bool
	done     chan struct{}

	ctx    context.Context // cancelled to abandon a stalled export on shutdown
	cancel context.CancelFunc
	once   sync.Once
}

func newQueue[T any](stats *dropStats, size, batch int, interval, timeout time.Duration, urgent func(T) bool, export func(context.Context, []T, bool) error) *queue[T] {
	q := &queue[T]{
		stats: stats, items: make(chan T, size), batchSize: batch,
		flushInterval: interval, exportTimeout: timeout, urgent: urgent, export: export,
		flushReq: make(chan chan struct{}), stop: make(chan struct{}), done: make(chan struct{}),
	}
	q.ctx, q.cancel = context.WithCancel(context.Background())
	go q.run()
	return q
}

// offer enqueues without blocking and reports whether the item was kept.
func (q *queue[T]) offer(v T) bool {
	if q.stopped.Load() {
		q.stats.add(ReasonQueueFull, 1)
		return false
	}
	select {
	case q.items <- v:
		return true
	default:
		q.stats.add(ReasonQueueFull, 1)
		return false
	}
}

func (q *queue[T]) run() {
	defer close(q.done)
	batch := make([]T, 0, q.batchSize)
	durable := false
	timer := time.NewTimer(q.flushInterval)
	defer timer.Stop()
	flush := func() {
		if len(batch) == 0 {
			return
		}
		ctx, cancel := context.WithTimeout(q.ctx, q.exportTimeout)
		err := q.export(ctx, batch, durable)
		cancel()
		if err != nil {
			q.stats.add(ReasonExportFailed, uint64(len(batch)))
		}
		clear(batch)
		batch, durable = batch[:0], false
	}
	take := func(v T) {
		batch = append(batch, v)
		if q.urgent != nil && q.urgent(v) {
			durable = true
		}
	}
	drain := func() {
		for {
			select {
			case v := <-q.items:
				take(v)
				if len(batch) >= q.batchSize {
					flush()
				}
			default:
				flush()
				return
			}
		}
	}
	for {
		select {
		case v := <-q.items:
			take(v)
			if durable || len(batch) >= q.batchSize {
				flush()
			}
		case <-timer.C:
			flush()
			timer.Reset(q.flushInterval)
		case ack := <-q.flushReq:
			drain()
			close(ack)
		case <-q.stop:
			drain()
			return
		}
	}
}

// forceFlush exports everything queued before the call, or returns when ctx ends.
func (q *queue[T]) forceFlush(ctx context.Context) error {
	if q.stopped.Load() {
		return nil
	}
	ack := make(chan struct{})
	select {
	case q.flushReq <- ack:
	case <-q.done:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
	select {
	case <-ack:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// shutdown drains and stops the goroutine. If ctx ends first, a stalled export
// is abandoned and ctx's error returned.
func (q *queue[T]) shutdown(ctx context.Context) error {
	q.once.Do(func() {
		q.stopped.Store(true)
		close(q.stop)
	})
	select {
	case <-q.done:
		return nil
	case <-ctx.Done():
		q.cancel()
		return ctx.Err()
	}
}
