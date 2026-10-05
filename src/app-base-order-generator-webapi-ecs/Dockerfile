# Build from the repository root: docker build -f src/app-base-order-generator-webapi-ecs/Dockerfile .
# The screen is built with Vite and served by the OrderGenerator itself (wwwroot).
FROM node:22-alpine AS front
WORKDIR /repo/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# Datadog tracer (dd-trace-dotnet). Same version and same SHA-256 in both images; a package that does not
# match the SHA of its architecture breaks the build. Downloads on the builder's architecture: it is only a tar.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS tracer
ARG DD_TRACE_DOTNET_VERSION=3.54.0
ARG DD_TRACE_DOTNET_SHA256_ARM64=8fd6213cafc85a9df3bbc22f9c357450bb85e1f144dfc286e1e32b6833aeafbc
ARG DD_TRACE_DOTNET_SHA256_AMD64=8bbaa079e53a16b4cdee80e419e01c6dfd134a6670411c17eb8cebc83f69fedd
ARG TARGETARCH
RUN case "$TARGETARCH" in \
        arm64) datadogTracerPackage="datadog-dotnet-apm-${DD_TRACE_DOTNET_VERSION}.arm64.tar.gz"; datadogTracerPackageSha256="$DD_TRACE_DOTNET_SHA256_ARM64" ;; \
        amd64) datadogTracerPackage="datadog-dotnet-apm-${DD_TRACE_DOTNET_VERSION}.tar.gz"; datadogTracerPackageSha256="$DD_TRACE_DOTNET_SHA256_AMD64" ;; \
        *) echo "No tracer package for architecture '$TARGETARCH'" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o /tmp/tracer.tar.gz "https://github.com/DataDog/dd-trace-dotnet/releases/download/v${DD_TRACE_DOTNET_VERSION}/${datadogTracerPackage}" \
    && echo "${datadogTracerPackageSha256}  /tmp/tracer.tar.gz" | sha256sum -c - \
    && mkdir -p /opt/datadog /var/log/datadog/dotnet \
    && tar -C /opt/datadog -xzf /tmp/tracer.tar.gz \
    && chmod a+rwx /var/log/datadog/dotnet

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props ./
COPY src/app-base-order-generator-webapi-ecs/OrderGenerator.csproj src/app-base-order-generator-webapi-ecs/
RUN dotnet restore src/app-base-order-generator-webapi-ecs/OrderGenerator.csproj
COPY src/app-base-order-generator-webapi-ecs/ src/app-base-order-generator-webapi-ecs/
COPY --from=front /repo/src/app-base-order-generator-webapi-ecs/wwwroot/ src/app-base-order-generator-webapi-ecs/wwwroot/
# The apps only start with the commit recorded at build time (route /version). The build needs .git in the context:
# in a clone the SHA comes from it; in a worktree, where .git is only a file, the caller passes SOURCE_REVISION_ID.
ARG SOURCE_REVISION_ID=
RUN --mount=type=bind,source=.git,target=/repo/.git \
    sha="${SOURCE_REVISION_ID:-$(git -c safe.directory='*' -C /repo rev-parse HEAD 2>/dev/null)}"; \
    if [ ${#sha} -ne 40 ]; then echo "No commit: .git did not give the SHA (worktree?); pass SOURCE_REVISION_ID=<full sha>" >&2; exit 1; fi; \
    dotnet publish src/app-base-order-generator-webapi-ecs/OrderGenerator.csproj -c Release -o /app --no-restore \
        -p:SourceRevisionId=$sha -p:EnableSourceControlManagerQueries=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=tracer /opt/datadog /opt/datadog
# The tracer writes its own log to a file in this folder, not to the app stdout (the app user is not root).
COPY --from=tracer /var/log/datadog /var/log/datadog
ENV CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER={846F5F1C-F9AE-4B07-969E-05C26BC060D8} \
    CORECLR_PROFILER_PATH=/opt/datadog/Datadog.Trace.ClrProfiler.Native.so \
    DD_DOTNET_TRACER_HOME=/opt/datadog \
    DD_TRACE_OTEL_ENABLED=true \
    DD_TRACE_PROPAGATION_STYLE_EXTRACT=none \
    DD_TRACE_SAMPLE_RATE=1.0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderGenerator.dll"]
