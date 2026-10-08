package domain

import "testing"

func TestIdeoligionReformOwnsCanonicalSelections(t *testing.T) {
	old := IdeoligionDesign{Memes: []string{"Structure", "Normal"}, Precepts: []string{"Old"}, Fluid: true}
	next := IdeoligionDesign{Memes: []string{"Normal", "Structure"}, Precepts: []string{"New"}, Fluid: true}
	v, err := NewIdeoligionReform("Ideo_1", old, next, 2)
	if err != nil {
		t.Fatal(err)
	}
	old.Memes[0], next.Precepts[0] = "Changed", "Changed"
	if v.Expected().Memes[0] != "Normal" || v.Design().Precepts[0] != "New" {
		t.Fatal("caller mutated immutable intent")
	}
	a, err := NewIdeoligionReformAction("reform-0", v)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := NewPlan("reform", 1, []Action{a}); err != nil {
		t.Fatal(err)
	}
	copy := v.Design()
	copy.Precepts[0] = "Changed"
	if v.Design().Precepts[0] != "New" {
		t.Fatal("getter leaked mutable intent")
	}
}

func TestIdeoligionReformRefusesNoopFixedAndDuplicates(t *testing.T) {
	old := IdeoligionDesign{Memes: []string{"Normal", "Structure"}, Precepts: []string{"Old"}, Fluid: true}
	for _, next := range []IdeoligionDesign{old, {Memes: old.Memes, Precepts: []string{"New"}}, {Memes: []string{"Normal", "Normal"}, Fluid: true}, {Memes: old.Memes, Precepts: []string{"New", "New"}, Fluid: true}} {
		if _, err := NewIdeoligionReform("Ideo_1", old, next, 0); err == nil {
			t.Fatal("invalid reform accepted")
		}
	}
}
