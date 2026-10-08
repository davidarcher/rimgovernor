package main

import "sync"

// problemsRefresh publishes immutable completed snapshots. WebView bindings
// only request work and copy the snapshot; recorder I/O stays off the UI thread.
type problemsRefresh struct {
	mu   sync.Mutex
	done chan struct{}
	view ProblemsView
	all  string
	load func([]string, string) (ProblemsView, string)
}

func newProblemsRefresh(t *recorderTail) *problemsRefresh {
	return &problemsRefresh{
		view: ProblemsView{Path: t.path, Empty: "Loading flight recorder…", Events: []ProblemEvent{}, Kinds: []KindCount{}},
		load: func(hidden []string, needle string) (ProblemsView, string) {
			return t.view(hidden, needle), t.allProblems()
		},
	}
}

func (p *problemsRefresh) poll(hidden []string, needle string) ProblemsView {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.done == nil {
		p.done = make(chan struct{})
		hidden = append([]string(nil), hidden...)
		go func() {
			view, all := p.load(hidden, needle)
			p.mu.Lock()
			p.view, p.all = view, all
			close(p.done)
			p.done = nil
			p.mu.Unlock()
		}()
	}
	return p.view
}

func (p *problemsRefresh) allProblems() string {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.all
}
