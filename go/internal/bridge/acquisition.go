package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// acquireAction is the AcquireIntent of an acquisition or a mine acquisition
// (designate the source), or of a stall withdraw (withdraw=true, #1046).
// Native checks the source live; a designation already in the requested
// state applies again, and the next census reads progress.
func acquireAction(action domain.Action) (*op.Action, error) {
	acquisition, withdraw, ok := action.AcquireIntent()
	if !ok {
		return nil, contract("not an acquisition action")
	}
	if _, err := domain.NewAcquisition(acquisition.Thing(), acquisition.Definition(), acquisition.Cell()); err != nil {
		return nil, contract("acquire intent requires a source, a resource and a cell")
	}
	return &op.Action{Intent: &op.Action_Acquire{Acquire: &op.AcquireIntent{
		SourceId:        proto.String(acquisition.Thing()),
		ResourceDefName: proto.String(acquisition.Definition()),
		Cell:            &c.Cell{X: proto.Int32(acquisition.Cell().X), Z: proto.Int32(acquisition.Cell().Z)},
		Withdraw:        proto.Bool(withdraw)}}}, nil
}
