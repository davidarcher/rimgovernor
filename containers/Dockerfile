FROM node:22-bookworm-slim AS dashboard
WORKDIR /src/dashboard
COPY dashboard/package.json dashboard/pnpm-lock.yaml ./
RUN corepack enable && corepack prepare pnpm@10.15.1 --activate && pnpm install --frozen-lockfile
COPY dashboard/ ./
COPY docs/players/ /src/docs/players/
RUN pnpm run typecheck && pnpm run test && pnpm run build

# Go controller path, the sole production entry point since G01.13
# (davidarcher/rimgovernor#33 removed the Python production server, the
# Python `worker`/`controller-tests`/`directory` Docker targets and the
# multi-colony `directory` mode; native scenario/acceptance tooling is
# being rebuilt in Go, tracked separately). Also the last stage, so a
# plain `docker build` with no --target builds this. Builds and packages
# the Go binary and the dashboard static assets with no Python runtime or
# dependencies. The binary itself still starts the native GABS bridge as a
# subprocess (see go/internal/bridge and go/cmd/rimgovernor/serve.go), so a
# real serve session needs a Linux GABS build, a prepared profile/state
# path and, because --listen only accepts a loopback address, host-style
# networking (`docker run --network host` or compose `network_mode: host`)
# rather than bridge port publishing. `version`/`help`/flag-validation
# exercise the image without any of that: they are compilation/protocol
# checks, not gameplay evidence.
FROM golang:1.27.1-bookworm AS go-builder
WORKDIR /src
COPY contracts/generated/protobuf/go/ contracts/generated/protobuf/go/
COPY go/ go/
WORKDIR /src/go
RUN go mod download
ENV CGO_ENABLED=0
RUN go build -o /out/rimgovernor ./cmd/rimgovernor

# Docker acceptance worker (go/internal/dockeraccept): the same rimgovernor
# binary as go-controller, plus the shared-library set a headless (or Xvfb-
# rendered) Unity/RimWorld process needs, which go-controller has no reason to
# carry. Game, mods and GABS binary are bind-mounted read-only at run time
# (rimgovernor-dockeraccept/#38); nothing license-bearing is baked into this
# image. Kept before go-controller so a plain `docker build` with no --target
# still builds go-controller by default.
#
# dockeraccept.StartWorker builds /worker/game entirely out of individual
# read-only `-v` bind mounts -- one per game top-level entry, plus one for
# Mods -- rather than one mount for the whole game directory. RimWorld's
# Unity engine resolves its install directory from the *real* path of its
# running executable, so a single game mount plus a Mods symlink pointing
# elsewhere doesn't work: Unity follows the symlink straight back to the
# game mount, where no Mods directory exists. Real per-entry mounts under
# the writable /worker parent give it a normal-looking tree instead, without
# copying game data or ever writing to the shared, read-only input store.
FROM debian:bookworm-slim AS worker
# curl: Docker Desktop's --network host (Windows/macOS) runs containers
# inside its own VM and does not bridge to the true host loopback, so
# go/internal/dockeraccept.Worker's HTTP health/state checks run through
# `docker exec ... curl` instead of a direct host-side connection -- that
# works uniformly there and under Linux's real host networking.
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl \
    libglib2.0-0 libglu1-mesa libgl1 libxcursor1 libxrandr2 libxinerama1 libxi6 \
    libasound2 libgtk-3-0 libnss3 libxss1 libxtst6 xvfb x11-utils mesa-utils \
    libgl1-mesa-dri && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=go-builder /out/rimgovernor ./rimgovernor
COPY --from=dashboard /src/dashboard/dist/ ./dashboard/
COPY containers/worker-merge-game.sh ./worker-merge-game.sh
RUN chmod +x ./worker-merge-game.sh
ENV RIMGOVERNOR_CONTAINER_SOURCE=1
ENTRYPOINT ["./worker-merge-game.sh"]
CMD ["version"]

FROM debian:bookworm-slim AS go-controller
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=go-builder /out/rimgovernor ./rimgovernor
COPY --from=dashboard /src/dashboard/dist/ ./dashboard/
ENV RIMGOVERNOR_CONTAINER_SOURCE=1
ENTRYPOINT ["./rimgovernor"]
CMD ["version"]
