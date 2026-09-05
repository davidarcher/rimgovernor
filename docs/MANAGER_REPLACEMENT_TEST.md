# Manager replacement regression

Run `python scripts/live_manager_replacements.py --execute` on a disposable,
paused quicktest with no player buildings. It uses the configured real local
model and normal native placement; save the player colony first and restore it
afterward. The script does not restore saves automatically.

The test checks nearby room samples and manager-selected sleeping spot locations,
then simulates player replacements between proposal and execution. Native
revision checks must reject every old draft without placing anything. A fresh
manager review must see all three replacements and propose no additional spots.

Latest pass: `.rimbot/replacement-tests/20260905-194341/report.json`, 55.11 seconds
for both model reviews and arbitration, 15 model calls. Stale orders rejected;
three replacement spots preserved. Earlier runs exposed bedroll/sleeping-spot
confusion and confusion between endpoint searches and game-definition searches.
The final test is evidence for this bounded scenario, not general colony skill.
Manager summaries can still incorrectly use past tense for proposals; the final
administrator response in the passing run correctly described future placement.

64 controller tests and 18 live read-only HTTP contract checks passed. Native
compilation, generated schema checks and Roslyn route/signature checks passed.
The fixed native DLL was installed while RimWorld was closed. The player's saved
colony was reloaded afterward and the controller restarted in Manual mode.

Construction revisions conservatively include all visible player construction,
so unrelated building changes may defer an order for a new review. Instant allow
orders and work-priority changes have integration-owned completion checks;
completion coverage for other non-construction actions remains incomplete.
