package policy

// throneSlot names the throne in the throne room's plan (throneWants).
const throneSlot = "throne"

// RequiredPiece is one piece a title's room requirement asks for besides the
// throne, planned in the slot (ThroneNeed.requiredPieces names them).
type RequiredPiece struct {
	Slot  string
	Piece InteriorPieceDef
}
