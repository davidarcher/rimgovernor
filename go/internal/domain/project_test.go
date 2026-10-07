package domain

import "testing"

func TestProjectFinishesOnceAndRegressOpensNewRow(t *testing.T) {
	_, scope := fixture(t)
	p, e := NewProject("project-0011223344556677-cook-0", "cook", 2, scope)
	if e != nil {
		t.Fatal(e)
	}
	if p, e = ReviewProject(p, scope, FindingUnmet, false); e != nil || p.Status != ProjectOpen || p.Finding != FindingUnmet {
		t.Fatal(p, e)
	}
	if p, e = ReviewProject(p, scope, FindingMet, true); e != nil || p.Status != ProjectOpen {
		t.Fatal("recovery with open work finished the project", p, e)
	}
	if p, e = ReviewProject(p, scope, FindingMet, false); e != nil || p.Status != ProjectCompleted {
		t.Fatal(p, e)
	}
	// A review that changes nothing returns the project equal to its input.
	if again, e := ReviewProject(p, scope, FindingUnclear, false); e != nil || again != p {
		t.Fatal(again, e)
	}
	if p2, e := ReviewProject(p, scope, FindingUnmet, true); e != nil || p2.Status != ProjectCompleted {
		t.Fatal("open work regressed a finished project", p2, e)
	}
	if !ProjectRegressed(p, FindingUnmet, false) {
		t.Fatal("deficit did not regress")
	}
	if _, e = ReviewProject(p, scope, FindingUnmet, false); e == nil {
		t.Fatal("regressed project reviewed in place")
	}
	next, e := NewProject("project-0011223344556677-cook-1", p.Kind, p.Priority, scope)
	if e != nil || next.Status != ProjectOpen || next.ID == p.ID || p.Status != ProjectCompleted {
		t.Fatal("new row must open beside the finished record", next, e)
	}
}

func TestProjectInvalidatedOnWorldChange(t *testing.T) {
	_, scope := fixture(t)
	p, _ := NewProject("project-0011223344556677-cook-0", "cook", 2, scope)
	moved := scope
	moved.Map++
	if g, e := ReviewProject(p, moved, FindingUnmet, false); e != nil || g.Status != ProjectVoided {
		t.Fatal("map change did not invalidate", g, e)
	}
	c, _ := ReviewProject(p, moved, FindingUnmet, false)
	if g, e := ReviewProject(c, scope, FindingMet, false); e != nil || g.Status != ProjectVoided {
		t.Fatal("invalidated project reviewed back to life", g, e)
	}
	bad := p
	bad.Status, bad.Finding = ProjectCompleted, FindingUnmet
	if bad.Validate() == nil {
		t.Fatal("finished project without recovery validated")
	}
}
