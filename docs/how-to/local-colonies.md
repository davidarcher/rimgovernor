# Browse local development colonies

[Documentation](../README.md)

Start the standalone Outpost directory from the checkout root:

```powershell
docker compose -p rimbot-colonies -f containers/colonies.compose.yaml up --build -d
```

Open [Local colonies](http://127.0.0.1:8790/colonies). This container serves only the
dashboard directory; it does not create a controller, database or game session.
It refreshes the running Docker workers every ten seconds. Open each colony in a
separate tab to preserve the original dashboard's drafts and view. Per-worker
dashboards continue to serve the agents and tests as before.

The directory mounts the local Docker socket and uses only container list and
inspection GET requests. A read-only socket mount does not restrict Docker API
permissions; keep this developer-only service bound to loopback. It does not
start, stop, attach to or modify workers. Stop this directory alone with:

```powershell
docker compose -p rimbot-colonies -f containers/colonies.compose.yaml down
```

Set `RIMBOT_COLONIES_PORT` before launch to change the directory's host port. To run
without a container after building the dashboard, use `python -m rimbot --colonies
--port 8790`. The host variant reads Docker through its CLI, including standard
Docker Desktop installation locations on Windows. Use a local Docker engine;
the generated browser links point to this computer.

Workers using `containers/compose.yaml` publish their dashboard automatically.
Set `RIMBOT_COLONY_NAME` for a friendly name; otherwise the container name appears.
Custom `docker run` workers must publish `127.0.0.1::8787` and run the controller's
HTTP server to offer a dashboard. Script-only probes with no HTTP server remain
inspection-only entries. Publishing a port alone does not create a server.
Existing containers cannot acquire a published port without recreation; leave
active scenarios running and configure their next launch.

Rendered workers offer game images; headless workers offer controller data only.
Running means Docker reports a live container, not that the game has finished
loading or is making progress. Unpublished workers remain visible without a link.
Discovery failures retain the last directory with an explicit stale-data message;
a successful refresh removes stopped workers.

The normal host dashboard also has a **Local colonies** menu. Game containers do
not mount Docker's socket, so use the standalone directory for cross-colony
discovery when browsing a container dashboard.
