# Review evidence retention

Conversation compaction can discard entire earlier tool-call groups. The
strategist now has a separate review-local store of exact returned native
inspection and knowledge results. A compact recent index occupies a persistent
initial-context message, and `review_evidence` searches by tool/arguments or reads
an evidence ID. Native calls are not cached or intercepted: requesting fresh
state still calls the game.

Stored evidence carries its capture time, arguments and exact result. Read-back
explicitly marks it historical. This is not strategist memory, a source of new
facts, a plan queue, or permission to execute an action. Player direction and
colony/load guards still govern commits. A new review starts with an empty store.

The store has a two-megabyte bound with oldest-first eviction and an explicit
eviction count. Oversized individual results are not retained. Search returns
twelve recent matching entries with an omitted count. Neither reads nor index
results expose mutable references to stored observations.

Tests verify exact data preservation, eviction, isolation between reviews,
index survival after actual request compaction, and a discover/inspect/read-back/
commit flow that makes only one native query. Model use and colony progress must
still be measured through the live probe.

The 90-second live probe on 2026-09-08 issued zero orders and had four rejected
calls. The model did not call evidence recall. Retention is tested, but reduced
query repetition or improved gameplay is not demonstrated by this run.
Local evidence: `.rimbot/review-evidence-live.json`.
