package domain

import "testing"

func TestProjectFinishesOnceAndRegressOpensNewRow(t *testing.T) {
	_, scope := fixture(t)
	p, e := NewProject("project-0011223344556677-cook-0", "cook", 2, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	if p, e = ReviewProject(p, scope, 11, NeedDeficit, false); e != nil || p.Status != ProjectOpen || p.Need != NeedDeficit {
		t.Fatal(p, e)
	}
	if p, e = ReviewProject(p, scope, 12, NeedRecovered, true); e != nil || p.Status != ProjectOpen {
		t.Fatal("recovery with open work finished the project", p, e)
	}
	if p, e = ReviewProject(p, scope, 13, NeedRecovered, false); e != nil || p.Status != ProjectFinished {
		t.Fatal(p, e)
	}
	if p, e = ReviewProject(p, scope, 14, NeedUnknown, false); e != nil || p.Status != ProjectFinished || p.Tick != 14 {
		t.Fatal(p, e)
	}
	if p2, e := ReviewProject(p, scope, 15, NeedDeficit, true); e != nil || p2.Status != ProjectFinished {
		t.Fatal("open work regressed a finished project", p2, e)
	}
	if !ProjectRegressed(p, NeedDeficit, false) {
		t.Fatal("deficit did not regress")
	}
	if _, e = ReviewProject(p, scope, 15, NeedDeficit, false); e == nil {
		t.Fatal("regressed project reviewed in place")
	}
	next, e := NewProject("project-0011223344556677-cook-1", p.Kind, p.Priority, scope, 15)
	if e != nil || next.Status != ProjectOpen || next.ID == p.ID || p.Status != ProjectFinished {
		t.Fatal("new row must open beside the finished record", next, e)
	}
}

func TestProjectInvalidatedOnWorldChange(t *testing.T) {
	_, scope := fixture(t)
	p, _ := NewProject("project-0011223344556677-cook-0", "cook", 2, scope, 10)
	if g, e := ReviewProject(p, scope, 9, NeedDeficit, false); e != nil || g.Status != ProjectInvalidated {
		t.Fatal("tick rewind did not invalidate", g, e)
	}
	c, _ := ReviewProject(p, scope, 9, NeedDeficit, false)
	if g, e := ReviewProject(c, scope, 11, NeedRecovered, false); e != nil || g.Status != ProjectInvalidated {
		t.Fatal("invalidated project reviewed back to life", g, e)
	}
	bad := p
	bad.Status, bad.Need = ProjectFinished, NeedDeficit
	if bad.Validate() == nil {
		t.Fatal("finished project without recovery validated")
	}
}
