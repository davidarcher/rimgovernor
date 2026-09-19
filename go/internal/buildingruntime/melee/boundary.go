package melee

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type MeleeNative interface {
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	PreviewAttack(context.Context, *c.Identity, *o.AttackTarget) (*o.PreviewReply, bridge.Result, error)
	LookupAttackAttempt(context.Context, bridge.AttackAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveAttackProgress(context.Context, bridge.AttackAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type MeleeWriter interface {
	AttackTarget(context.Context, *a.WritePrecondition, *o.AttackTarget) (*o.ExecuteReply, bridge.Result, error)
}
type MeleeCapabilities struct {
	Native MeleeNative
	Writer MeleeWriter
}
type MeleeBoundary struct {
	native  MeleeNative
	writer  MeleeWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewMeleeBoundary(native MeleeNative, writer MeleeWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*MeleeBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid melee boundary dependencies")
	}
	return &MeleeBoundary{native, writer, leases, clock, session}, nil
}

func meleeCommand(pawn, target, pawnToken, targetToken string) *o.AttackTarget {
	return &o.AttackTarget{Pawn: &o.EntityPrecondition{EntityId: proto.String(pawn), ExpectedSnapshotToken: proto.String(pawnToken)}, Target: &o.EntityPrecondition{EntityId: proto.String(target), ExpectedSnapshotToken: proto.String(targetToken)}, Mode: o.AttackMode_ATTACK_MODE_MELEE.Enum(), RequireHostile: proto.Bool(true), RequireStanding: proto.Bool(true), RequireCombatHealth: proto.Bool(true)}
}
func meleeClaim(action domain.Action, snapshot domain.GenerationSnapshot, claim domain.DraftClaim, session string) error {
	m, ok := action.MeleeAttack()
	if !ok || snapshot.Validate() != nil || snapshot.Native == 0 || snapshot.Revision == 0 || claim.Action != m.DraftAction() || claim.Pawn != m.Pawn() || claim.Origin != snapshot || claim.Attempt == 0 || string(claim.Session) != session || !boundary.ValidID(string(claim.Claim)) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *MeleeBoundary) InspectMelee(ctx context.Context, target executor.Target, claim domain.DraftClaim) (executor.MeleeInspection, error) {
	out := executor.MeleeInspection{StartedAt: b.clock.Now()}
	if err := meleeClaim(target.Action, target.Snapshot, claim, b.session); err != nil {
		return out, err
	}
	m, _ := target.Action.MeleeAttack()
	reply, _, err := b.native.ReadCombatPawns(ctx, boundary.Identity(target.Snapshot), []string{string(m.Pawn()), string(m.Target())})
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return out, executor.ErrHeld
	}
	current, err := boundary.Context(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	// The target is a hostile pawn (two rows) or a hostile building the
	// threat census lists under its own token (the attacker's row alone).
	counts := observed.Completeness
	rows := uint64(len(observed.Pawns))
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || rows < 1 || rows > 2 || counts.GetMatched() != rows || counts.GetReturned() != rows {
		return out, executor.ErrHeld
	}
	var pawn, opponent *n.PawnState
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil {
			return out, executor.ErrEvidence
		}
		switch row.Pawn.GetId() {
		case string(m.Pawn()):
			if pawn != nil {
				return out, executor.ErrEvidence
			}
			pawn = row
		case string(m.Target()):
			if opponent != nil {
				return out, executor.ErrEvidence
			}
			opponent = row
		default:
			return out, executor.ErrEvidence
		}
	}
	if pawn == nil {
		return out, executor.ErrHeld
	}
	pawnToken, err := boundary.PawnToken(pawn, observed.Context)
	if err != nil {
		return out, err
	}
	var targetToken string
	var building *policy.EmergencyThreat
	if opponent != nil {
		if targetToken, err = boundary.PawnToken(opponent, observed.Context); err != nil {
			return out, err
		}
	} else {
		// The building's token comes from the census; the emergency is
		// read again after the preview, as the policy binds it by tick.
		census, _, err := b.native.ReadEmergency(ctx, boundary.Identity(current))
		if err != nil {
			return out, err
		}
		if _, err = boundary.Context(census.Context, current); err != nil {
			return out, err
		}
		building = hostileBuilding(census.Facts.Threats, m.Target())
		if building == nil {
			return out, executor.ErrHeld
		}
		targetToken = building.SnapshotToken
	}
	preview, _, err := b.native.PreviewAttack(ctx, boundary.Identity(current), meleeCommand(string(m.Pawn()), string(m.Target()), pawnToken, targetToken))
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(evaluated.Context, current); err != nil {
		return out, err
	}
	job := evaluated.GetProjected().GetJob()
	if evaluated.Context.GetTick() < observed.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(m.Pawn()) || job.GetTargetA().GetThingId() != string(m.Target()) || job.GetJobDef() != "AttackMelee" || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	emergency, _, err := b.native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	// The emergency read may be served from the step's fact cache, up to
	// PlanningTickTolerance behind this inspection's first read; it must
	// cover that read, not the preview taken after it (#244).
	if !domain.Tick(emergency.Context.GetTick()).Covers(domain.Tick(observed.Context.GetTick())) {
		return out, executor.ErrEvidence
	}
	facts := policy.MeleeDefenseFacts{Snapshot: current, PawnTick: domain.Tick(observed.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: boundary.FactBool(job.CanTry)}
	facts.Emergency, err = policy.NewEmergencySnapshot(current, domain.Tick(evaluated.Context.GetTick()), emergency.Facts)
	if err != nil {
		return out, err
	}
	facts.Pawn = policy.MeleePawnFacts{Pawn: m.Pawn(), SnapshotToken: pawnToken, Dead: boundary.FactBool(pawn.Dead), Downed: boundary.FactBool(pawn.Downed), FreeColonist: boundary.FactBool(pawn.FreeColonist), Drafted: boundary.FactBool(pawn.Drafted)}
	if pawn.Health != nil {
		facts.Pawn.Bleeding, facts.Pawn.NeedsTend = boundary.FactBool(pawn.Health.Bleeding), boundary.FactBool(pawn.Health.NeedsTend)
		if pawn.Health.SummaryFraction != nil {
			facts.Pawn.HealthFraction = domain.Known(pawn.Health.GetSummaryFraction())
		}
	}
	// A claim is either held by the single bot process or it is not: native
	// no longer reports a distinct session/direction for it (see
	// observations.proto's OwnedDraftClaim), so an observed claim is by
	// construction ours in the current epoch.
	if owned := pawn.GetDraftClaim().GetOwned(); owned != nil && boundary.ValidID(owned.GetClaimId()) && owned.PawnSnapshot != nil && proto.Equal(owned.PawnSnapshot, pawn.Pawn.Snapshot) {
		facts.Pawn.Owner = domain.Known(policy.MeleeDraftOwner{Claim: domain.DraftClaimID(owned.GetClaimId()), Session: domain.ControllerSessionID(b.session)})
	}
	if biography := pawn.Biography; biography != nil && !boundary.IssueField(biography.Issues, "disabled_work_tags") {
		capable := true
		for _, tag := range biography.DisabledWorkTags {
			if tag == "Violent" {
				capable = false
			}
		}
		facts.Pawn.ViolenceCapable = domain.Known(capable)
	}
	if equipment := pawn.Equipment; equipment != nil && equipment.Armed != nil && !boundary.IssueField(equipment.Issues, "equipped") && !boundary.IssueField(equipment.Issues, "armed") {
		known := !equipment.GetArmed()
		if equipment.GetArmed() && equipment.PrimaryId != nil {
			for _, item := range equipment.Equipped {
				if item.GetThing().GetId() == equipment.GetPrimaryId() {
					known = true
				}
			}
		}
		facts.Pawn.EquipmentKnown = domain.Known(known)
	}
	if opponent != nil {
		facts.Target = policy.MeleeTargetFacts{Pawn: m.Target(), SnapshotToken: targetToken, Dead: boundary.FactBool(opponent.Dead), Downed: boundary.FactBool(opponent.Downed), Hostile: boundary.FactBool(opponent.Hostile)}
	} else {
		// A building the census still lists is standing and hostile; one
		// it no longer lists is gone, and the policy refuses the target.
		facts.Target = policy.MeleeTargetFacts{Pawn: m.Target(), SnapshotToken: targetToken, Dead: domain.Known(true), Downed: domain.Known(false), Hostile: domain.Known(false)}
		if current := hostileBuilding(emergency.Facts.Threats, m.Target()); current != nil {
			facts.Target.Dead, facts.Target.Hostile = current.Dead, domain.Known(true)
		}
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

// hostileBuilding finds the census row of a hostile building target.
func hostileBuilding(threats []policy.EmergencyThreat, id domain.PawnID) *policy.EmergencyThreat {
	for i := range threats {
		if threats[i].Building() && domain.PawnID(threats[i].ID) == id {
			return &threats[i]
		}
	}
	return nil
}

func (b *MeleeBoundary) attempt(dispatch executor.MeleeDispatch) (bridge.AttackAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	if err := meleeClaim(p.Action, p.Snapshot, admission.DraftClaim, b.session); err != nil {
		return bridge.AttackAttempt{}, err
	}
	m, _ := p.Action.MeleeAttack()
	if p.Attempt == 0 || p.Tick < 0 || admission.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != m.Pawn() || admission.Target != m.Target() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) || !boundary.ValidID(admission.TargetSnapshotToken) {
		return bridge.AttackAttempt{}, executor.ErrEvidence
	}
	return bridge.AttackAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), PawnID: string(m.Pawn()), TargetID: string(m.Target()), Mode: o.AttackMode_ATTACK_MODE_MELEE, RequireHostile: true, RequireStanding: true, RequireCombatHealth: true}, nil
}

func meleeJob(job *r.JobEffect, dispatch executor.MeleeDispatch) error {
	if job == nil {
		return nil
	}
	m, _ := dispatch.Attempt.Action.MeleeAttack()
	if job.GetPawnId() != string(m.Pawn()) || job.GetTargetA().GetThingId() != string(m.Target()) || job.GetJobDef() != "AttackMelee" || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if job.DraftClaimId != nil && job.GetDraftClaimId() != string(dispatch.Admission.DraftClaim.Claim) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *MeleeBoundary) AttackMelee(ctx context.Context, dispatch executor.MeleeDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !boundary.ValidID(lease) {
		return out, executor.ErrAuthority
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.NativeGeneration)}
	reply, _, err := b.writer.AttackTarget(ctx, pre, meleeCommand(attempt.PawnID, attempt.TargetID, dispatch.Admission.PawnSnapshotToken, dispatch.Admission.TargetSnapshotToken))
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	receipt := reply.GetReceipt()
	if err = b.checkReceipt(receipt, dispatch); err != nil {
		return out, err
	}
	switch receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
	default:
		return out, executor.ErrEvidence
	}
	return out, ctx.Err()
}
