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

FROM debian:bookworm-slim AS go-controller
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=go-builder /out/rimgovernor ./rimgovernor
COPY --from=dashboard /src/dashboard/dist/ ./dashboard/
ENV RIMGOVERNOR_CONTAINER_SOURCE=1
ENTRYPOINT ["./rimgovernor"]
CMD ["version"]
