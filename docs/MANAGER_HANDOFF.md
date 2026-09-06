# Manager handoff and native work settings

Specialists receive their own assignment and fresh observations, not every
manager's assignment list. The strategy prompt assigns all construction,
including defensive structures, to Infrastructure. Security owns threat
assessment and combat; Workforce owns ordinary work configuration.

Rejected draft calls are recorded automatically as not issued. They do not
prevent a specialist from submitting its remaining valid drafts. Player-facing
proposal summaries are generated from retained action titles, so a prose claim
cannot turn an absent order into completed work. Raw assessments remain in the
diagnostic proposal event.

Four identical read calls returning identical evidence finish that specialist
review while preserving drafts. Different queries or changed results remain
unbounded; this is not a total tool-call quota. Other managers can proceed.

Administrator context includes uncached native work priorities and disability
flags for affected pawns. Injury text and tendable_now=false do not establish
permanence or incapability. Arbitrary inferred injury restrictions are not native
prerequisites.

## Native work-tab checkbox

RimWorld Pawn_WorkSettings.GetPriority returns 3 for enabled humanlike work while
Manual priorities is off, even after SetPriority stores a different number.
This caused accepted API calls to have no effective priority change.

GET/POST /api/v1/work/settings expose use_work_priorities. The setter changes the
same PlaySettings field and notifies the same player pawns as the native Work
tab. The DTO and strict wire schema are generated from authored OpenAPI. Numeric
priority drafts require this mode already enabled or an earlier retained order
to enable it. The integration verifies both the mode and effective priority.

## Validation

Run python scripts/live_workforce_handoff.py --execute with the dashboard in
Manual and a disposable colony loaded. The real local model proposes native
work-tab actions, the administrator arbitrates, and the executor verifies the
result. The test restores the observed priority, work-tab mode and pause state.
It briefly unpauses if needed; it does not restart or save the game.

Latest pass: .rimbot/workforce-tests/20260905-203552/report.json.
16.89 seconds, five model calls, two verified actions. Original priority and mode
restored. The test establishes this bounded work-tab flow, not complete colony
management quality. Earlier failures exposed the missing native checkbox.

70 controller tests pass. Native compilation, generated-file checks and Roslyn
route coverage pass. Invalid native setting requests (missing boolean, string
instead of boolean, unknown field) return 400 without changing the setting.
Intermittent incomplete LM Studio streams remain a separate transport failure;
partial responses are not treated as executable decisions.
