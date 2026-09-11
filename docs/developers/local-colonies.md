# Browse local development colonies

[Documentation](../README.md)

Start the standalone Outpost directory from the checkout root:

```powershell
docker compose -p rimgovernor-colonies -f containers/colonies.compose.yaml up --build -d
```

Open [Local colonies](http://127.0.0.1:8790/colonies). This container serves only the
dashboard directory; it does not create a controller, database or game session.
It refreshes the running Docker workers every ten seconds. Open each colony in a
separate tab to preserve the original dashboard's drafts and view. Each worker serves its own dashboard.

The directory mounts the local Docker socket and uses only container list and
inspection GET requests. A read-only socket mount does not restrict Docker API
permissions; keep this developer-only service bound to loopback. It does not
start, stop, attach to or modify workers. Stop this directory alone with:

```powershell
docker compose -p rimgovernor-colonies -f containers/colonies.compose.yaml down
```

Set `RIMGOVERNOR_COLONIES_PORT` before launch to change the directory's host port. To run
without a container after building the dashboard, use `python -m rimgovernor --colonies
--port 8790`. The host variant reads Docker through its CLI, including standard
Docker Desktop installation locations on Windows. Use a local Docker engine;
the generated browser links point to this computer.

Workers using `containers/compose.yaml` publish their dashboard automatically.
Set `RIMGOVERNOR_COLONY_NAME` for a friendly name; otherwise the container name appears.
Use the [standard scenario launcher](testing/scenario-launcher.md) for script-based native
tests. It publishes an automatic loopback port, and the worker enables a passive
dashboard on the script's existing `BridgeRuntime`. The specialized population,
husbandry, player-action and visual launchers use the same port contract.
Ad hoc `docker run` commands must use `dashboard_options` from the standard launcher;
direct bridge-only scripts with no `BridgeRuntime` have no controller state to serve.
Existing containers cannot acquire a published port without recreation; leave
active scenarios running and configure their next launch.

Normal rendered workers offer game images; headless workers offer controller data only.
Scenario dashboards show retained frames only and never request captures or take
control. They expose no chat, video leases, time controls or other mutations.
Running means Docker reports a live container, not that the game has finished
loading or is making progress. Unpublished workers remain visible without a link.
Discovery failures retain the last directory with an explicit stale-data message;
a successful refresh removes stopped workers.

The normal host dashboard also has a **Local colonies** menu. Game containers do
not mount Docker's socket, so use the standalone directory for cross-colony
discovery when browsing a container dashboard.
