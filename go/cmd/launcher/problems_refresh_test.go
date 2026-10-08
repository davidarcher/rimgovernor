package main

import (
	"testing"
	"time"
)

func TestProblemsRefreshPollsWhileLoadBlocked(t *testing.T) {
	started := make(chan struct{})
	release := make(chan struct{})
	var loads int
	p := &problemsRefresh{view: ProblemsView{Empty: "loading"}, load: func(hidden []string, needle string) (ProblemsView, string) {
		loads++
		close(started)
		<-release
		if len(hidden) != 1 || hidden[0] != "hidden" || needle != "find" {
			t.Errorf("worker filters = %v, %q", hidden, needle)
		}
		return ProblemsView{Available: true, Rows: 42}, "completed problems"
	}}
	hidden := []string{"hidden"}
	p.poll(hidden, "find")
	<-started
	p.mu.Lock()
	done := p.done
	p.mu.Unlock()
	hidden[0] = "changed"
	returned := make(chan struct{})
	go func() {
		for range 20 {
			if v := p.poll(nil, "new filter"); v.Empty != "loading" {
				t.Errorf("unfinished snapshot published: %+v", v)
			}
			if all := p.allProblems(); all != "" {
				t.Errorf("unfinished copy published: %q", all)
			}
		}
		close(returned)
	}()
	select {
	case <-returned:
	case <-time.After(60 * time.Second):
		close(release)
		t.Fatal("UI callbacks waited for recorder load")
	}
	close(release)
	<-done
	p.mu.Lock()
	defer p.mu.Unlock()
	if loads != 1 || p.view.Rows != 42 || p.all != "completed problems" {
		t.Fatalf("loads=%d view=%+v copy=%q", loads, p.view, p.all)
	}
}
