# Proposal

## Why

The native save handshake lets a manual game save proceed after its flush
acknowledgement times out (3 s) and logs a warning, so a manual save can carry
governor intent older than the moment of saving. RG-CONTINUITY-001 did not say
whether that is allowed, and the persistence contract described the flush
without the connected timeout. [#4011](https://github.com/davidarcher/rimgovernor/issues/4011)
asked for the decision.

## What Changes

- RG-CONTINUITY-001 states that a player's save is never blocked by the
  governor: it waits a bounded time for fresh intent, then saves with the last
  flushed intent and records that it did. A new scenario covers the timed-out
  manual save and keeps the governed checkpoint's stricter completed-reply rule.
- The persistence contract and the player save guide state the same.
- Review-queue Q4 is marked decided.

## Capabilities

### Modified Capabilities

- `control-and-continuity`: RG-CONTINUITY-001 wording and one added scenario.

## Impact

Requirement and contract documents only; the code already behaves this way.
The remaining implementation work from #4011 is one native boundary assertion
for the timeout path, tracked on the issue.

## Decision provenance and review boundary

Source: the maintainer's grooming review in chat on 2026-11-08. The two
options put to the maintainer were bounded best-effort freshness with a logged
warning, or strict freshness with a player-visible failure and recovery path.
The maintainer chose best effort ("yes best effort"). The reasoning offered
was that a player pressing Save should never be blocked by the bot and the
brain re-reviews from the loaded world after load, so stale intent costs
little. Scenario wording is the assistant's formalization. No timeout value
was approved as a requirement; 3 s is the current implementation.
