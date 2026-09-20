package bridge

import (
	"context"
	"strings"
	"sync"
	"time"
)

// AdmissionClass is the priority a native call is admitted under (#631).
// The bridge's MaxConcurrentCalls slots are shared by the control path
// (clock renew and stop, dispatch, authority), bulk observation (bundle and
// list reads of hundreds of kilobytes) and fallback media (base64 frames
// when the shared video buffer is unavailable). Without classes a burst of
// reads or frames takes every slot and the next renew queues behind them;
// with them one slot is reserved for control, observation and media each
// have a ceiling below the shared total, and a waiting control call is
// admitted before any waiting read whenever a slot frees.
type AdmissionClass string

const (
	AdmissionControl     AdmissionClass = "control"
	AdmissionObservation AdmissionClass = "observation"
	AdmissionMedia       AdmissionClass = "media"
)

// Slot layout: control may hold any of the MaxConcurrentCalls slots and
// always has one that no other class can take; observation and media are
// each capped below the non-reserved remainder so neither can starve the
// other, and together they never exceed it.
const (
	admissionControlReserved = 1
	admissionObservationMax  = 5
	admissionMediaMax        = 2
)

// admissionOutcome is what one admitted call learned about the queue: its
// class, how long it waited for a slot and how many calls of its class
// (and in total) were waiting ahead of it when it asked. It is copied into
// the call's timing so a flight.jsonl consumer can attribute gate waits
// per class.
type admissionOutcome struct {
	class      AdmissionClass
	wait       time.Duration
	queueDepth int
	classDepth int
}

type admissionWaiter struct {
	class AdmissionClass
	ready chan struct{}
}

// admission is the class-aware gate one Client admits its calls through.
type admission struct {
	mu       sync.Mutex
	capacity int
	held     map[AdmissionClass]int
	waiting  []*admissionWaiter
}

func newAdmission(capacity int) *admission {
	return &admission{capacity: capacity, held: map[AdmissionClass]int{}}
}

// classMax is the most slots a class may hold at once.
func (a *admission) classMax(class AdmissionClass) int {
	switch class {
	case AdmissionObservation:
		return admissionObservationMax
	case AdmissionMedia:
		return admissionMediaMax
	default:
		return a.capacity
	}
}

// admits reports whether class could take a slot now: under its own cap,
// and, for a non-control class, with the other classes together leaving
// the reserved control slots untouched.
func (a *admission) admits(class AdmissionClass) bool {
	total := 0
	for _, n := range a.held {
		total += n
	}
	if total >= a.capacity || a.held[class] >= a.classMax(class) {
		return false
	}
	if class != AdmissionControl && total-a.held[AdmissionControl] >= a.capacity-admissionControlReserved {
		return false
	}
	return true
}

// acquire blocks until a slot is admitted for class or ctx ends. A waiting
// control call is always chosen before waiting observation or media calls;
// within a class, waiters are served in arrival order.
func (a *admission) acquire(ctx context.Context, class AdmissionClass) (admissionOutcome, error) {
	began := time.Now()
	a.mu.Lock()
	outcome := admissionOutcome{class: class}
	for _, w := range a.waiting {
		outcome.queueDepth++
		if w.class == class {
			outcome.classDepth++
		}
	}
	// Control never waits while it can be admitted; another class takes a
	// free slot at once only when none of its own class is already waiting
	// (the queue holds only calls whose class could not take a slot).
	if a.admits(class) && (class == AdmissionControl || outcome.classDepth == 0) {
		a.held[class]++
		a.mu.Unlock()
		return outcome, nil
	}
	w := &admissionWaiter{class: class, ready: make(chan struct{})}
	a.waiting = append(a.waiting, w)
	a.mu.Unlock()
	select {
	case <-w.ready:
		outcome.wait = time.Since(began)
		return outcome, nil
	case <-ctx.Done():
		a.mu.Lock()
		select {
		case <-w.ready:
			// Admitted between ctx ending and the lock: give the slot back.
			a.held[class]--
			a.wakeLocked()
		default:
			a.removeLocked(w)
		}
		a.mu.Unlock()
		return outcome, ctx.Err()
	}
}

func (a *admission) release(class AdmissionClass) {
	a.mu.Lock()
	a.held[class]--
	a.wakeLocked()
	a.mu.Unlock()
}

func (a *admission) removeLocked(w *admissionWaiter) {
	for i, other := range a.waiting {
		if other == w {
			a.waiting = append(a.waiting[:i], a.waiting[i+1:]...)
			return
		}
	}
}

// wakeLocked admits every waiter a free slot can take, control first, then
// the others in arrival order.
func (a *admission) wakeLocked() {
	for progressed := true; progressed; {
		progressed = false
		var pick *admissionWaiter
		for _, w := range a.waiting {
			if !a.admits(w.class) {
				continue
			}
			if pick == nil || (w.class == AdmissionControl && pick.class != AdmissionControl) {
				pick = w
			}
			if pick.class == AdmissionControl {
				break
			}
		}
		if pick == nil {
			return
		}
		a.removeLocked(pick)
		a.held[pick.class]++
		close(pick.ready)
		progressed = true
	}
}

// snapshot reports the slots held and the calls waiting per class.
func (a *admission) snapshot() (held, waiting map[AdmissionClass]int) {
	a.mu.Lock()
	defer a.mu.Unlock()
	held = map[AdmissionClass]int{}
	for class, n := range a.held {
		held[class] = n
	}
	waiting = map[AdmissionClass]int{}
	for _, w := range a.waiting {
		waiting[w.class]++
	}
	return held, waiting
}

// nativeAdmissionClass is the class every reviewed rimgovernor/* method
// is admitted under. Every name protoCall's allowlist accepts must be
// listed (TestEveryReviewedMethodHasAnAdmissionClass); an unlisted name
// falls to the prefix rule below. Presentation leases and state reads are
// dashboard traffic and never take the control slot; only the frame
// transport is media.
var nativeAdmissionClass = map[string]AdmissionClass{
	"rimgovernor/clock_start":                          AdmissionControl,
	"rimgovernor/clock_renew":                          AdmissionControl,
	"rimgovernor/clock_change_speed":                   AdmissionControl,
	"rimgovernor/clock_pause":                          AdmissionControl,
	"rimgovernor/clock_read_events":                    AdmissionControl,
	"rimgovernor/clock_read_status":                    AdmissionControl,
	"rimgovernor/clock_read_attempt":                   AdmissionControl,
	"rimgovernor/authority_control":                    AdmissionControl,
	"rimgovernor/authority_read_status":                AdmissionControl,
	"rimgovernor/operations_execute":                   AdmissionControl,
	"rimgovernor/operations_preview":                   AdmissionControl,
	"rimgovernor/operations_release_owned_draft":       AdmissionControl,
	"rimgovernor/receipts_lookup":                      AdmissionControl,
	"rimgovernor/receipts_observe_progress":            AdmissionControl,
	"rimgovernor/placement_preview":                    AdmissionControl,
	"rimgovernor/lifecycle_read_identity":              AdmissionControl,
	"rimgovernor/lifecycle_read_tick":                  AdmissionControl,
	"rimgovernor/lifecycle_save":                       AdmissionControl,
	"rimgovernor/lifecycle_read_save":                  AdmissionControl,
	"rimgovernor/lifecycle_load":                       AdmissionControl,
	"rimgovernor/lifecycle_read_load":                  AdmissionControl,
	"rimgovernor/observations_read_status":             AdmissionObservation,
	"rimgovernor/observations_read_bundle":             AdmissionObservation,
	"rimgovernor/observations_list_pawns":              AdmissionObservation,
	"rimgovernor/observations_get_cells":               AdmissionObservation,
	"rimgovernor/observations_list_supplies":           AdmissionObservation,
	"rimgovernor/observations_read_colony_facts":       AdmissionObservation,
	"rimgovernor/observations_list_buildings":          AdmissionObservation,
	"rimgovernor/observations_list_rooms":              AdmissionObservation,
	"rimgovernor/observations_read_research":           AdmissionObservation,
	"rimgovernor/observations_list_wall_upgrade_sites": AdmissionObservation,
	"rimgovernor/observations_list_zones":              AdmissionObservation,
	"rimgovernor/observations_read_defense_site":       AdmissionObservation,
	"rimgovernor/observations_read_lines_of_fire":      AdmissionObservation,
	"rimgovernor/observations_read_spatial_access":     AdmissionObservation,
	"rimgovernor/observations_read_husbandry":          AdmissionObservation,
	"rimgovernor/observations_read_caravan_catalog":    AdmissionObservation,
	"rimgovernor/observations_read_world_progression":  AdmissionObservation,
	"rimgovernor/observations_read_world":              AdmissionObservation,
	"rimgovernor/observations_read_bills":              AdmissionObservation,
	"rimgovernor/observations_read_recipes":            AdmissionObservation,
	"rimgovernor/observations_list_resource_sources":   AdmissionObservation,
	"rimgovernor/observations_read_production_policy":  AdmissionObservation,
	"rimgovernor/observations_read_population":         AdmissionObservation,
	"rimgovernor/observations_read_trade_sheet":        AdmissionObservation,
	"rimgovernor/observations_list_traders":            AdmissionObservation,
	"rimgovernor/observations_read_excavation_site":    AdmissionObservation,
	"rimgovernor/observations_get_clearance_targets":   AdmissionObservation,
	"rimgovernor/observations_get_ancient_shrines":     AdmissionObservation,
	"rimgovernor/presentation_camera":                  AdmissionObservation,
	"rimgovernor/presentation_selection":               AdmissionObservation,
	"rimgovernor/presentation_colonists":               AdmissionObservation,
	"rimgovernor/presentation_notifications":           AdmissionObservation,
	"rimgovernor/presentation_render_state":            AdmissionObservation,
	"rimgovernor/presentation_render_demand":           AdmissionObservation,
	"rimgovernor/presentation_lease_video":             AdmissionObservation,
	"rimgovernor/presentation_capture_pawn":            AdmissionMedia,
	"rimgovernor/presentation_read_frame":              AdmissionMedia,
	"rimgovernor/presentation_acknowledge_frame":       AdmissionMedia,
}

// admissionClassOf classifies a call by the native tool it reaches (the
// inner rimgovernor/* method for games_call_tool, else the GABS tool
// itself). GABS lifecycle tools (connect, status, start, stop, attention)
// are control: they are the supervisor's own path. Unlisted rimgovernor
// methods classify by prefix so an acceptance NativeCall of a fixture or
// observation tool never takes the control slot.
func admissionClassOf(nativeTool string) AdmissionClass {
	if class, ok := nativeAdmissionClass[nativeTool]; ok {
		return class
	}
	switch {
	case strings.HasPrefix(nativeTool, "rimgovernor/observations_"), strings.HasPrefix(nativeTool, "rimgovernor/presentation_"), strings.HasPrefix(nativeTool, "home/"), strings.HasPrefix(nativeTool, "test/"):
		return AdmissionObservation
	}
	return AdmissionControl
}

type admissionClassKey struct{}

// WithAdmissionClass overrides the class a call is admitted under, for a
// caller whose use of a method differs from its default (a dashboard read
// of a control-classed status, say). Most callers never need it: the
// method's class in nativeAdmissionClass applies.
func WithAdmissionClass(ctx context.Context, class AdmissionClass) context.Context {
	return context.WithValue(ctx, admissionClassKey{}, class)
}

func admissionClassFrom(ctx context.Context) (AdmissionClass, bool) {
	class, ok := ctx.Value(admissionClassKey{}).(AdmissionClass)
	return class, ok
}
