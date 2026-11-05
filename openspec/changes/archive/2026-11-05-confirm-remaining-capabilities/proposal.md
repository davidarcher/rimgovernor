# Proposal

## Why

Five requirement pages recovered from developer contracts were never
maintainer-confirmed: protection and recovery, colonist wellbeing, start and
control, continuity and explanations. The maintainer reviewed them in
conversation on 2026-11-04 and 2026-11-05 against the capabilities confirmed
earlier that week. This change records those decisions, promotes the surviving
requirements into standing specifications, retires one identity whose content
habitat already owns, and promotes one inferred candidate the maintainer
decided on.

## What Changes

- Promote RG-PROTECT-001 as the single protection requirement: a recognised
  threat is answered without a comparison for everyone within its reach, and
  everything around that response is priced. Protection owns readiness demand
  and withdrawal is a named option.
- Retire RG-PROTECT-002. Service restoration is already compared as measured
  work under RG-HABITAT-003, which distinguishes causes of service failure.
  One scenario is added there for a service lost to two causes at once.
- Promote RG-HEALTH-001 with the same override pattern as protection, for
  every patient the colony holds, and RG-HEALTH-002 with wellbeing valued by
  the consequences it changes.
- Promote RG-CONTROL-001–002, RG-CONTINUITY-001–002 and RG-EXPLAIN-001–002
  as written, restated in requirement form.
- Promote the review queue's candidate Q5 in its narrow form as
  RG-EXPLAIN-003: explain the comparison behind a choice.
- Record in the acquisition purpose that research is a prerequisite costed
  into the option that needs it and never a goal of its own; the collection
  README now says the excluded goals are player-specified ones.
- Evidence pages for the four 2026-11-03 capabilities stay deferred until
  code is first inspected against one of their identities.

## Capabilities

### New Capabilities

- `protection`: RG-PROTECT-001.
- `colonist-wellbeing`: RG-HEALTH-001–002.
- `control-and-continuity`: RG-CONTROL-001–002, RG-CONTINUITY-001–002.
- `explanations`: RG-EXPLAIN-001–003.

### Modified Capabilities

- `habitat`: RG-HABITAT-003 gains the two-cause outage scenario.
- `resource-acquisition`: purpose paragraph gains the research sentence; this
  is a direct edit of the standing specification because the archive keeps
  an existing purpose as authoritative.

## Impact

Requirement documents and navigation only. No planner, operation or contract
changes. The five pages keep their evidence sections and point their
identities at the standing specifications. No implementation-completeness or
pass claim follows; the existing P, W, C, R and E evidence and their limits
stand as they were.

## Decision provenance and review boundary

Source: the maintainer's confirmation review in this chat on 2026-11-04 and
2026-11-05. Each row records the maintainer's direction and the words that
gave it; requirement wording and scenarios are the assistant's formalization.

| Confirmed decision | Maintainer direction |
| --- | --- |
| Evidence pages | Deferred for acquisition, growing, hunting and herds until first inspection ("defer i guess"). |
| Research | The README's excluded research goals are player-specified ones; need-driven research as a costed prerequisite is in scope ("PLAYER specified research goals"). |
| RG-PROTECT-001 response | The immediate response is an override, everything around it is priced ("your rec on 1"). |
| Readiness | Protection owns readiness demand, priced by forecast risk ("protection owns readiness"). |
| Who is protected | Everyone the colony holds, each loss priced by its owner ("yes, everyone is protected"). |
| Withdrawal | A named option: running and kiting until the raid leaves, sealing a room and building doors behind ("withdrawal is an option but probably more like running away and kiting until the raid leaves? maybe locking yourself in a room and building doors behind you?"). |
| RG-PROTECT-002 | Retired into habitat; the cause of an outage does not matter and repair during a flare is not wasted ("does it matter if it was a flare or just running out of power?", "repair during a flare isn't necessarily wasted work", "retire"). |
| RG-HEALTH-001 | Override for urgent care, doctor and abandoned work priced per pawn, every patient the colony holds ("1 confirmed"). |
| Mood | Never ignored; valued by the consequences it changes, no weight for happiness by itself ("mood is like one of the most important systems in the game. It definitely shouldn't be ignored", "priced by consequence sounds right"). |
| Risky procedures | Expected value against the chance of losing the patient, sunk effort at zero; implants are investments ("3 confirmed"). |
| Wellbeing demand | Medicine through acquisition, clothing through equipment management, beds and recreation through habitat ("4 confirmed"). |
| Control, continuity, explanations | RG-CONTROL-001–002, RG-CONTINUITY-001–002 and RG-EXPLAIN-001–002 confirmed as written ("confirm all six"). |
| Q5 | Promoted in its narrow form as RG-EXPLAIN-003 ("Q5 promote"). |

No exposure threshold, mood threshold, retreat trigger, happiness weight or
readiness level was approved. The defense pricing gap accepted on 2026-11-03
still applies: until defense prices a participant's absence per pawn, that cost
is absent from the comparison. Retired identity RG-PROTECT-002 is never reused.
