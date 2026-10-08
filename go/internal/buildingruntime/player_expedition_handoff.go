package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"slices"
	"strings"
)

type playerMapFocus interface {
	FocusMap(context.Context, domain.GenerationSnapshot, domain.GenerationSnapshot) (domain.NativeGeneration, error)
}
type playerMapFocusConfirmation interface {
	ConfirmMapFocus(context.Context, domain.GenerationSnapshot, domain.GenerationSnapshot, domain.NativeGeneration) error
}

func (p *Player) expeditionIncomingHandoff(call, epoch context.Context, loaded *o.BundleSnapshot) (bool, error) {
	state := p.session.State()
	if !state.ObservationKnown || loaded == nil || loaded.Context == nil || loaded.WorldProgression == nil || loaded.Context.Identity == nil {
		return false, nil
	}
	identity := loaded.Context.Identity
	if identity.GetColonyId() != string(state.Snapshot.Colony) || identity.GetLoadToken() != string(state.Snapshot.Load) || domain.MapID(identity.GetMapId()) == state.Snapshot.Map {
		return false, nil
	}
	prior, err := p.journal.CurrentControl(call)
	if err != nil {
		return false, err
	}
	if prior.Phase != store.RunningControl || prior.Request.World != playerWorld(state.Snapshot) {
		return false, nil
	}
	world, err := bridge.DecodeWorldProgressionSnapshot(loaded.WorldProgression, identity)
	if err != nil {
		return false, err
	}
	plans, err := p.journal.PlanHistoryWithMethods(call, 256, "quest-expedition-*", "quest-site-*-return-*")
	if err != nil {
		return false, err
	}
	target, found := expeditionHandoffTarget(state.Snapshot, &world, plans)
	if !found || target.Map != domain.MapID(identity.GetMapId()) {
		return false, nil
	}
	confirm, ok := p.session.(playerMapFocusConfirmation)
	if !ok {
		return false, nil
	}
	generation := domain.NativeGeneration(loaded.Context.GetNativeGeneration())
	if err = p.session.Disable(); err != nil {
		return false, err
	}
	if err = confirm.ConfirmMapFocus(call, state.Snapshot, target, generation); err != nil {
		return false, err
	}
	call = context.WithValue(call, handoffGenerationKey{}, generation)
	return p.handoffHeld(call, epoch, state, target)
}

// expeditionHandoff runs under the caller's player gate. Durable departure
// evidence authorizes only its exact physical destination and observed crew.
func (p *Player) expeditionHandoff(call, epoch context.Context, state ControlState, read observation.RoundsReading) (bool, error) {
	if !state.Enabled || !state.ObservationKnown || read.Frame.Quests == nil {
		return false, nil
	}
	prior, err := p.journal.CurrentControl(call)
	if err != nil {
		return false, err
	}
	if prior.Phase != store.RunningControl || prior.Request.World != playerWorld(state.Snapshot) {
		return false, nil
	}
	plans, err := p.journal.PlanHistoryWithMethods(call, 256, "quest-expedition-*", "quest-site-*-return-*")
	if err != nil {
		return false, err
	}
	target, found := expeditionHandoffTarget(state.Snapshot, read.Frame.Quests, plans)
	if !found {
		return false, nil
	}
	return p.handoffHeld(call, epoch, state, target)
}

func (p *Player) handoffHeld(call, epoch context.Context, state ControlState, target domain.GenerationSnapshot) (bool, error) {
	focus, ok := p.session.(playerMapFocus)
	if !ok {
		return false, nil
	}
	if err := p.current(call, epoch); err != nil {
		return false, err
	}
	request := store.ControlRequest{RequestID: fmt.Sprintf("expedition-focus-%s-%s-%d-%d", state.Snapshot.Colony, state.Snapshot.Load, target.Map, state.Snapshot.Native), Kind: store.ResumeControl, World: playerWorld(target)}
	record, created, err := p.journal.BeginControl(call, request)
	if err != nil || !created {
		return false, err
	}
	if err = p.session.Disable(); err != nil {
		_, err = p.uncertain(record, err)
		return true, err
	}
	if _, err = p.stopRounds(call); err != nil {
		_, err = p.uncertain(record, err)
		return true, err
	}
	generation, alreadyFocused := call.Value(handoffGenerationKey{}).(domain.NativeGeneration)
	if err = p.current(call, epoch); err == nil {
		if !alreadyFocused {
			generation, err = focus.FocusMap(call, state.Snapshot, target)
		}
	}
	if err != nil {
		_, err = p.uncertain(record, err)
		return true, err
	}
	if err = p.current(call, epoch); err == nil {
		err = p.world(call, request.World)
	}
	if err != nil {
		_, err = p.uncertain(record, err)
		return true, err
	}
	call = context.WithValue(call, handoffGenerationKey{}, generation)
	_, err = p.completeResumeHeld(call, epoch, record)
	return true, err
}

func expeditionHandoffTarget(current domain.GenerationSnapshot, world *bridge.WorldProgressionRead, plans []store.PlanState) (domain.GenerationSnapshot, bool) {
	for _, plan := range plans {
		for _, action := range plan.Spec.Actions() {
			departure, ok := action.CaravanDeparture()
			if !ok {
				continue
			}
			var proof domain.ProgressView
			for _, progress := range plan.Progress {
				if progress.View().Action == action.ID() {
					proof = progress.View()
					break
				}
			}
			receipt, rk := proof.Receipt.Value()
			if !rk || receipt != domain.ReceiptAccepted || proof.Snapshot.Colony != current.Colony || proof.Snapshot.Load != current.Load || proof.Snapshot.Map != current.Map {
				continue
			}
			for _, m := range world.Maps {
				if domain.MapID(m.ID) == current.Map || m.Tile != departure.DestinationTile() {
					continue
				}
				crew := departure.Crew()
				if len(crew) == 0 || !allExpeditionCrewPresent(crew, m.PawnIDs) {
					continue
				}
				if !m.Home {
					linked := false
					for _, site := range world.Sites {
						if site.GetMapId() == m.ID && site.MapId != nil && site.GetTile() == m.Tile {
							for _, quest := range site.QuestIds {
								if strings.HasPrefix(string(plan.Method), "quest-expedition-"+quest+"-"+site.GetId()+"-") {
									linked = true
									break
								}
							}
						}
					}
					if !linked {
						continue
					}
				}
				target := current
				target.Map = domain.MapID(m.ID)
				return target, true
			}
		}
	}
	return domain.GenerationSnapshot{}, false
}

func allExpeditionCrewPresent(crew []domain.PawnID, present []string) bool {
	for _, pawn := range crew {
		if !slices.Contains(present, string(pawn)) {
			return false
		}
	}
	return true
}
