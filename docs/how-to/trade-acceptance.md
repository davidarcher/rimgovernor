# Verify native trades

[Documentation](../README.md)

Build the observation project with `-p:TradeFixture=true` against the private Linux
references and copy its output plus the identity assembly into a fresh private mod
snapshot. This adds test-only incident setup, ordinary trade/dismiss jobs and exact
stock observations; it never edits pawn statistics, inventory or saves. Production
builds exclude the fixture. With the [Docker input variables](docker-worker.md) configured and
`RIMBOT_DISPLAY=headless`, run a fresh worker:

```powershell
docker compose -f containers/compose.yaml -p rimbot-trade build worker
docker compose -f containers/compose.yaml -p rimbot-trade run --rm worker -- python /app/scripts/trade_acceptance.py
```

The probe waits for normal supply-pod landing, designates a native stockpile and
requests an ordinary bulk-goods caravan incident and small tribal visitor incidents for
the lower trader-budget case. The negotiator must reach the
trader through `TradeWithPawn`. It compares real ground and caravan inventory counts
for sales and purchases, including exact silver changes at an unchanged tick.
It checks native affordability refusals, stale session/preview/load refusal,
loss of stock eligibility after ordinary stockpile and Home-area edits, an
injected lost acceptance reply, ordinary dismissal and departure. Reports remain in
`run/trade-result.json`, with intermediate evidence and staged binary hashes beside
them. Failed fixtures remain failed; selecting another fresh run does not erase them.

`test_trading.py` separately verifies controller budgets, missing/nonfinite evidence
and persisted uncertain-write refusal after plan restoration. These fixture tests
do not establish native delivery. Orbital input is audited against the installed
`Building_CommsConsole` menu and `UseCommsConsole` job; direct `home/trade` opening
refuses orbital traders. This probe does not claim an orbital exchange or hauling
into final storage.

