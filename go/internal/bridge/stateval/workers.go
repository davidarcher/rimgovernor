package stateval

import (
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The StatWorker subclasses (epic #2621, #2639). A stat with no workerClass
// runs the base StatWorker, the core in eval.go and show.go; a subclass
// overrides some of its virtual methods. A Worker is one ported subclass and
// implements, in addition to Class, exactly the overrides the game's class has
// (a class that overrides only display strings is a plain Worker, which
// behaves as the base):
//
//   - baseValueWorker: GetBaseValueFor
//   - unfinalizedWorker: GetValueUnfinalized; Evaluator.baseUnfinalized is
//     base.GetValueUnfinalized
//   - showWorker: ShouldShowFor; the base func is base.ShouldShowFor
//
// Inheritance between worker classes is a Go helper both share, not embedding
// of another Worker.

// Worker is one ported StatWorker subclass.
type Worker interface {
	// Class is the StatWorker class name, as in stat_classes.tsv.
	Class() string
}

type baseValueWorker interface {
	BaseValue(req *Request) (float32, error)
}

type unfinalizedWorker interface {
	Unfinalized(req *Request) (float32, error)
}

type showWorker interface {
	Show(req *Request, base func() (bool, error)) (bool, error)
}

// ownedWorkers are the StatWorker subclasses a Go function owns, by class.
func ownedWorkers() map[string]Worker {
	var workers []Worker
	workers = append(workers, pawnWorkerList()...)
	workers = append(workers, thingWorkerList()...)
	workers = append(workers, gearWorkerList()...)
	workers = append(workers, pawnStatWorkerList()...)
	owned := make(map[string]Worker, len(workers))
	for _, w := range workers {
		owned[w.Class()] = w
	}
	return owned
}

// worker is the stat's ported worker: nil for the base worker, or the
// NotMirrored error naming a subclass no Go function owns.
func (e *Evaluator) worker(stat *d.StatDef, fact string) (Worker, error) {
	class := workerClass(stat)
	if class == baseWorker {
		return nil, nil
	}
	if w, ok := e.workers[class]; ok {
		return w, nil
	}
	return nil, &NotMirrored{Class: class, Fact: fact}
}
