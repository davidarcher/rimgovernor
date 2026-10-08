package domain

import (
	"encoding/json"
	"errors"
	"slices"
)

type ShuttleLoading string

const (
	ShuttleLaunchOnly    ShuttleLoading = "none"
	ShuttleAutoload      ShuttleLoading = "autoload"
	ShuttleExplicitPawns ShuttleLoading = "explicit_pawns"
)

// QuestShuttle requests loading orders or send, not completed boarding or departure.
type QuestShuttle struct {
	quest            QuestID
	loading          ShuttleLoading
	autoload, launch bool
	pawns            string
}

const QuestShuttleAction ActionKind = "quest_shuttle"

func NewQuestShuttle(quest QuestID, loading ShuttleLoading, autoload bool, pawns []PawnID, launch bool) (QuestShuttle, error) {
	if !validID(string(quest)) {
		return QuestShuttle{}, errors.New("invalid shuttle quest")
	}
	rows := slices.Clone(pawns)
	slices.Sort(rows)
	switch loading {
	case ShuttleLaunchOnly:
		if !launch || autoload || len(rows) > 0 {
			return QuestShuttle{}, errors.New("launch-only shuttle requires launch without loading")
		}
	case ShuttleAutoload:
		if len(rows) > 0 {
			return QuestShuttle{}, errors.New("autoload cannot carry explicit pawns")
		}
	case ShuttleExplicitPawns:
		if autoload || len(rows) == 0 || len(rows) > 256 {
			return QuestShuttle{}, errors.New("explicit shuttle loading requires 1 to 256 pawns")
		}
	default:
		return QuestShuttle{}, errors.New("invalid shuttle loading mode")
	}
	for i, pawn := range rows {
		if !validID(string(pawn)) || i > 0 && rows[i-1] == pawn {
			return QuestShuttle{}, errors.New("invalid or duplicate shuttle pawn")
		}
	}
	if rows == nil {
		rows = []PawnID{}
	}
	data, err := json.Marshal(rows)
	if err != nil || len(data) > 8000 {
		return QuestShuttle{}, errors.New("shuttle pawn list exceeds storage bound")
	}
	return QuestShuttle{quest, loading, autoload, launch, string(data)}, nil
}
func (s QuestShuttle) Quest() QuestID          { return s.quest }
func (s QuestShuttle) Loading() ShuttleLoading { return s.loading }
func (s QuestShuttle) Autoload() bool          { return s.autoload }
func (s QuestShuttle) Launch() bool            { return s.launch }
func (s QuestShuttle) Pawns() []PawnID {
	var rows []PawnID
	_ = json.Unmarshal([]byte(s.pawns), &rows)
	return rows
}
func NewQuestShuttleAction(id ActionID, s QuestShuttle) (Action, error) {
	canonical, err := NewQuestShuttle(s.quest, s.loading, s.autoload, s.Pawns(), s.launch)
	if !validID(string(id)) || err != nil || canonical != s {
		return Action{}, errors.New("invalid quest shuttle action")
	}
	return Action{id: id, kind: QuestShuttleAction, questShuttle: s}, nil
}
func (a Action) QuestShuttle() (QuestShuttle, bool) {
	return a.questShuttle, a.kind == QuestShuttleAction
}
