# Verify native trades

[Documentation](../../README.md)

Build the observation project with `-p:TradeFixture=true` against the private Linux
references and copy its output plus the identity assembly into a fresh private mod
snapshot. This adds test-only incident setup, ordinary trade/dismiss jobs and exact
stock observations; it never edits pawn statistics, inventory or saves. Production
builds exclude the fixture. With the [Docker input variables](docker-worker.md) configured and
`RIMGOVERNOR_DISPLAY=headless`, run a fresh worker:

```powershell
docker compose -f containers/compose.yaml -p rimgovernor-trade build worker
docker compose -f containers/compose.yaml -p rimgovernor-trade run --rm worker -- python /app/scripts/trade_acceptance.py
```

The probe waits for normal supply-pod landing, designates a native stockpile,
allows one observed wood stack and verifies a pawn hauls it into storage without
changing total wood. It requests an ordinary bulk-goods caravan incident and small tribal visitor incidents for
the lower trader-budget case. The negotiator must reach the
trader through `TradeWithPawn`. It compares real ground and caravan inventory counts
for sales and purchases, including exact silver changes at an unchanged tick.
It checks native affordability refusals, stale session/preview/load refusal,
loss of stock eligibility after ordinary stockpile and Home-area edits, an
injected lost acceptance reply, ordinary dismissal and departure. Reports remain in
`run/trade-result.json`, with intermediate evidence and staged binary hashes beside
them. Failed fixtures remain failed; selecting another fresh run does not erase them.

An affordable policy purchase supplies the fixture's marketable commodity; its
actual delivery supplies the later surplus sale. Stored wood is also checked as
an unavailable-demand case rather than assumed to be marketable.
The sale and lost-reply purchase use the economic selector with explicit stock,
quantity and price targets. The probe verifies protected-item and reserve-based
selection refusals, then stages prohibited sales directly to verify the atomic
native reserve/protected-export guards. Every refusal must leave actual goods
and silver unchanged. Successful exchanges compare both sides at an unchanged
tick; the purchase remains present after the trader departs.

`test_trading.py` and `test_trade_policy.py` separately verify controller budgets, missing/nonfinite evidence
and persisted uncertain-write refusal after plan restoration. These fixture tests
do not establish native delivery. Orbital input is audited against the installed
`Building_CommsConsole` menu and `UseCommsConsole` job; direct `home/trade` opening
refuses orbital traders. This probe does not claim an orbital exchange or hauling
into final storage.

