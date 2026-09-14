FROM node:22-bookworm-slim AS dashboard
WORKDIR /src/dashboard
COPY dashboard/package.json dashboard/pnpm-lock.yaml ./
RUN corepack enable && corepack prepare pnpm@10.15.1 --activate && pnpm install --frozen-lockfile
COPY dashboard/ ./
COPY docs/players/ /src/docs/players/
RUN pnpm run typecheck && pnpm run test && pnpm run build

FROM python:3.12-slim-bookworm AS controller-dependencies
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY pyproject.toml THIRD_PARTY.md ./
# Resolve dependencies before copying frequently edited source and tests.
RUN python -c "import subprocess,sys,tomllib; p=tomllib.load(open('pyproject.toml','rb')); subprocess.check_call([sys.executable,'-m','pip','install','--no-cache-dir',*p['build-system']['requires'],*p['project']['dependencies'],*p['project']['optional-dependencies']['test'],*p['project']['optional-dependencies']['video']])"

FROM controller-dependencies AS controller-tests
COPY third_party/ third_party/
COPY controller/ controller/
COPY controller_tests/ controller_tests/
COPY scripts/ scripts/
COPY contracts/ contracts/
COPY integrations/ integrations/
RUN pip install --no-cache-dir --no-deps --no-build-isolation .
CMD ["python", "-m", "pytest", "-q"]

FROM controller-tests AS tests
COPY --from=dashboard /src/controller/rimgovernor/static/ controller/rimgovernor/static/
RUN pip install --no-cache-dir --no-deps --no-build-isolation .

FROM python:3.12-slim-bookworm AS directory
WORKDIR /app
COPY pyproject.toml ./
COPY controller/ controller/
COPY --from=dashboard /src/controller/rimgovernor/static/ controller/rimgovernor/static/
RUN pip install --no-cache-dir .
CMD ["python", "-m", "rimgovernor", "--colonies", "--host", "0.0.0.0"]

# Builds Go native-accept probes (go/internal/nativeaccept/cmd/*) for the worker
# image, so a Docker-isolated acceptance run can exec a compiled probe directly
# instead of a Python scenario script. Kept minimal and task-scoped: only the
# probes actually used in a container run are built here.
FROM golang:1.27.1-bookworm AS go-accept-builder
WORKDIR /src
COPY contracts/generated/protobuf/go/ contracts/generated/protobuf/go/
COPY go/ go/
WORKDIR /src/go
RUN go mod download
ENV CGO_ENABLED=0
RUN go build -o /out/caravandepartureaccept ./internal/nativeaccept/cmd/caravandepartureaccept

FROM controller-dependencies AS worker-dependencies
RUN apt-get update && apt-get install -y --no-install-recommends libglib2.0-0 libglu1-mesa libgl1 libxcursor1 libxrandr2 libxinerama1 libxi6 libasound2 libgtk-3-0 libnss3 libxss1 libxtst6 xvfb x11-utils mesa-utils libgl1-mesa-dri && rm -rf /var/lib/apt/lists/*

FROM worker-dependencies AS worker
LABEL io.rimgovernor.scenario-dashboard="1"
# Reuse native libraries when only application source or probes change.
COPY --from=tests /usr/local/ /usr/local/
COPY --from=tests /app/ /app/
COPY --from=go-accept-builder /out/caravandepartureaccept /app/bin/caravandepartureaccept
ENV PYTHONUNBUFFERED=1
ENV RIMGOVERNOR_CONTAINER_SOURCE=1
ENTRYPOINT ["python", "-m", "rimgovernor.container_worker"]
CMD []

# Go-only controller path (G01.11). Builds and packages the Go binary and the
# same dashboard static assets with no Python runtime or dependencies. The
# binary itself still starts the native GABS bridge as a subprocess (see
# go/internal/bridge and go/cmd/rimgovernor/serve.go), so a real serve session
# needs a Linux GABS build, a prepared profile/state path and, because
# --listen only accepts a loopback address, host-style networking (`docker run
# --network host` or compose `network_mode: host`) rather than bridge port
# publishing. `version`/`help`/flag-validation exercise the image without any
# of that: they are compilation/protocol checks, not gameplay evidence.
FROM golang:1.27.1-bookworm AS go-builder
WORKDIR /src
COPY contracts/generated/protobuf/go/ contracts/generated/protobuf/go/
COPY go/ go/
WORKDIR /src/go
RUN go mod download
ENV CGO_ENABLED=0
RUN go build -o /out/rimgovernor ./cmd/rimgovernor

FROM debian:bookworm-slim AS go-controller
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=go-builder /out/rimgovernor ./rimgovernor
COPY --from=dashboard /src/controller/rimgovernor/static/ ./dashboard/
ENV RIMGOVERNOR_CONTAINER_SOURCE=1
ENTRYPOINT ["./rimgovernor"]
CMD ["version"]
