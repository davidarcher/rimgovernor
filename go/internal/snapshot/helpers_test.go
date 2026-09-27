package snapshot

import (
	"encoding/json"
	"fmt"
)

// MirrorAt is every mirror section as the stream at path held it when the
// review at was recorded (seq 0: the last review at the tick).
func MirrorAt(path string, at Review) (map[string]Section, error) {
	var out map[string]Section
	err := seek(path, reviewBefore(at.Tick, at.Seq), func(from int64) (bool, error) {
		out = nil
		err := walkFrom(path, from, func(line streamLine, st *replayState) (bool, error) {
			if line.Section != nil || line.Step != nil || line.combat() || line.Tick != at.Tick || (at.Seq != 0 && line.Seq != at.Seq) {
				return true, nil
			}
			out = make(map[string]Section, len(st.sections))
			for name, s := range st.sections {
				out[name] = s.export(name)
			}
			return at.Seq == 0, nil
		})
		return out != nil, err
	})
	if err == nil && out == nil {
		err = fmt.Errorf("%s: no review %s", path, at)
	}
	return out, err
}

func (s *recSection) export(name string) Section {
	rows := make(map[string]json.RawMessage, len(s.rows))
	for k, v := range s.rows {
		rows[k] = v
	}
	return Section{Name: name, Version: s.version, AsOf: s.asOf, Scope: s.scope, Rows: rows}
}
