package domain

import (
	"encoding/json"
	"errors"
	"slices"
)

const IdeoligionReformAction ActionKind = "ideoligion_reform"

type IdeoligionDesign struct {
	Memes    []string
	Precepts []string
	Fluid    bool
}

func CanonicalIdeoligionDesign(v IdeoligionDesign) (IdeoligionDesign, error) {
	if len(v.Memes) == 0 || len(v.Memes) > 5 || len(v.Precepts) > 256 {
		return IdeoligionDesign{}, errors.New("invalid ideoligion selection count")
	}
	v.Memes, v.Precepts = slices.Clone(v.Memes), slices.Clone(v.Precepts)
	for _, names := range [][]string{v.Memes, v.Precepts} {
		slices.Sort(names)
		for i, name := range names {
			if !validID(name) || (i > 0 && name == names[i-1]) {
				return IdeoligionDesign{}, errors.New("invalid or duplicate ideoligion selection")
			}
		}
	}
	return v, nil
}

type IdeoligionReform struct {
	ideo             string
	expected, design string
	count            int
}

func NewIdeoligionReform(ideo string, expected, design IdeoligionDesign, count int) (IdeoligionReform, error) {
	if !validID(ideo) || count < 0 || count >= 2147483647 || !expected.Fluid || !design.Fluid {
		return IdeoligionReform{}, errors.New("invalid fluid ideoligion reform")
	}
	expected, err := CanonicalIdeoligionDesign(expected)
	if err != nil {
		return IdeoligionReform{}, err
	}
	design, err = CanonicalIdeoligionDesign(design)
	if err != nil {
		return IdeoligionReform{}, err
	}
	a, _ := json.Marshal(expected)
	b, _ := json.Marshal(design)
	if string(a) == string(b) {
		return IdeoligionReform{}, errors.New("reform must change the design")
	}
	return IdeoligionReform{ideo: ideo, expected: string(a), design: string(b), count: count}, nil
}

func (v IdeoligionReform) IdeoID() string { return v.ideo }
func (v IdeoligionReform) Count() int     { return v.count }
func (v IdeoligionReform) Expected() IdeoligionDesign {
	var d IdeoligionDesign
	_ = json.Unmarshal([]byte(v.expected), &d)
	return d
}
func (v IdeoligionReform) Design() IdeoligionDesign {
	var d IdeoligionDesign
	_ = json.Unmarshal([]byte(v.design), &d)
	return d
}

func NewIdeoligionReformAction(id ActionID, v IdeoligionReform) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewIdeoligionReform(v.ideo, v.Expected(), v.Design(), v.count)
	if err != nil || canonical != v {
		return Action{}, errors.New("invalid ideoligion reform")
	}
	return Action{id: id, kind: IdeoligionReformAction, ideoligionReform: v}, nil
}
func (a Action) IdeoligionReform() (IdeoligionReform, bool) {
	return a.ideoligionReform, a.kind == IdeoligionReformAction
}
