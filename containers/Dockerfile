FROM node:22-bookworm-slim AS dashboard
WORKDIR /src/dashboard
COPY dashboard/package.json dashboard/pnpm-lock.yaml ./
RUN corepack enable && corepack prepare pnpm@10.15.1 --activate && pnpm install --frozen-lockfile
COPY dashboard/ ./
RUN pnpm run typecheck && pnpm run test && pnpm run build

FROM python:3.12-slim-bookworm AS controller-tests
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY pyproject.toml THIRD_PARTY.md ./
# Resolve dependencies before copying frequently edited source and tests.
RUN python -c "import subprocess,sys,tomllib; p=tomllib.load(open('pyproject.toml','rb')); subprocess.check_call([sys.executable,'-m','pip','install','--no-cache-dir',*p['build-system']['requires'],*p['project']['dependencies'],*p['project']['optional-dependencies']['test'],*p['project']['optional-dependencies']['video']])"
COPY third_party/ third_party/
COPY controller/ controller/
COPY controller_tests/ controller_tests/
COPY scripts/ scripts/
COPY integrations/ integrations/
RUN pip install --no-cache-dir --no-deps --no-build-isolation .
CMD ["python", "-m", "pytest", "-q"]

FROM controller-tests AS tests
COPY --from=dashboard /src/controller/rimbot/static/ controller/rimbot/static/
RUN pip install --no-cache-dir --no-deps --no-build-isolation .

FROM python:3.12-slim-bookworm AS directory
WORKDIR /app
COPY pyproject.toml ./
COPY controller/ controller/
COPY --from=dashboard /src/controller/rimbot/static/ controller/rimbot/static/
RUN pip install --no-cache-dir .
CMD ["python", "-m", "rimbot", "--colonies", "--host", "0.0.0.0"]

FROM tests AS worker
LABEL io.rimbot.scenario-dashboard="1"
RUN apt-get update && apt-get install -y --no-install-recommends libglib2.0-0 libglu1-mesa libgl1 libxcursor1 libxrandr2 libxinerama1 libxi6 libasound2 libgtk-3-0 libnss3 libxss1 libxtst6 xvfb x11-utils mesa-utils libgl1-mesa-dri && rm -rf /var/lib/apt/lists/*
ENV PYTHONUNBUFFERED=1
ENV RIMBOT_CONTAINER_SOURCE=1
ENTRYPOINT ["python", "-m", "rimbot.container_worker"]
CMD []
