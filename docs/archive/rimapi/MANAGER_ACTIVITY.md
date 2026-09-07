# Colony activity

The dashboard shows outcomes by default: verified work, issued-order summaries,
strategic decisions, reserved layouts, cancelled projects and terminal blockers.
Orders sent to the game remain labelled as awaiting verification. Later observed
completion produces a separate verified outcome; model claims are not completion.

Inspections, proposed orders and validation corrections are collapsed beneath a
recent-activity count. Expand it for readable request summaries and optional raw
technical details. Counts distinguish commands actually issued from tool calls;
submit calls are excluded, and matching diagnostic copies of failed tool calls
are not counted as additional corrections. These counts describe the retained
recent events, not lifetime totals or a claim that every correction was retried.

Source filtering uses a compact selector with the actors present in the feed.
Construction and other executors are labelled as specialists; Architect,
Administrator and the departments keep their own names.

The endpoint retains the latest 300 detailed events plus 80 recent outcome events,
deduplicated by event ID. A long tool loop therefore does not immediately evict
useful outcomes. Full history remains available through the export link.
