# Build a partir da raiz do repositório: docker build -f src/OrderGenerator/Dockerfile .
# A tela é construída com o Vite e servida pelo próprio OrderGenerator (wwwroot).
FROM node:22-alpine AS front
WORKDIR /repo/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# Tracer do Datadog (dd-trace-dotnet). Mesma versão e mesmos SHA-256 nas duas imagens; pacote que não
# bate com o SHA da arquitetura derruba o build. Baixa na arquitetura de quem constrói: é só um tar.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS tracer
ARG DD_TRACE_DOTNET_VERSION=3.54.0
ARG DD_TRACE_DOTNET_SHA256_ARM64=8fd6213cafc85a9df3bbc22f9c357450bb85e1f144dfc286e1e32b6833aeafbc
ARG DD_TRACE_DOTNET_SHA256_AMD64=8bbaa079e53a16b4cdee80e419e01c6dfd134a6670411c17eb8cebc83f69fedd
ARG TARGETARCH
RUN case "$TARGETARCH" in \
        arm64) pacoteDoTracerDatadog="datadog-dotnet-apm-${DD_TRACE_DOTNET_VERSION}.arm64.tar.gz"; sha256DoPacoteDoTracerDatadog="$DD_TRACE_DOTNET_SHA256_ARM64" ;; \
        amd64) pacoteDoTracerDatadog="datadog-dotnet-apm-${DD_TRACE_DOTNET_VERSION}.tar.gz"; sha256DoPacoteDoTracerDatadog="$DD_TRACE_DOTNET_SHA256_AMD64" ;; \
        *) echo "Sem pacote do tracer para a arquitetura '$TARGETARCH'" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o /tmp/tracer.tar.gz "https://github.com/DataDog/dd-trace-dotnet/releases/download/v${DD_TRACE_DOTNET_VERSION}/${pacoteDoTracerDatadog}" \
    && echo "${sha256DoPacoteDoTracerDatadog}  /tmp/tracer.tar.gz" | sha256sum -c - \
    && mkdir -p /opt/datadog /var/log/datadog/dotnet \
    && tar -C /opt/datadog -xzf /tmp/tracer.tar.gz \
    && chmod a+rwx /var/log/datadog/dotnet

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props ./
COPY src/Flowa.Shared/Flowa.Shared.csproj src/Flowa.Shared/
COPY src/OrderGenerator/OrderGenerator.csproj src/OrderGenerator/
RUN dotnet restore src/OrderGenerator/OrderGenerator.csproj
COPY src/Flowa.Shared/ src/Flowa.Shared/
COPY src/OrderGenerator/ src/OrderGenerator/
COPY --from=front /repo/src/OrderGenerator/wwwroot/ src/OrderGenerator/wwwroot/
# Os apps só sobem com o commit gravado no build (rota /version). O build precisa do .git no contexto:
# num clone o SHA sai dele; numa worktree, onde .git é só um arquivo, quem chama passa SOURCE_REVISION_ID.
ARG SOURCE_REVISION_ID=
RUN --mount=type=bind,source=.git,target=/repo/.git \
    sha="${SOURCE_REVISION_ID:-$(git -c safe.directory='*' -C /repo rev-parse HEAD 2>/dev/null)}"; \
    if [ ${#sha} -ne 40 ]; then echo "Sem o commit: o .git não deu o SHA (worktree?); passe SOURCE_REVISION_ID=<sha completo>" >&2; exit 1; fi; \
    dotnet publish src/OrderGenerator/OrderGenerator.csproj -c Release -o /app --no-restore \
        -p:SourceRevisionId=$sha -p:EnableSourceControlManagerQueries=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=tracer /opt/datadog /opt/datadog
# O tracer grava o próprio log em arquivo nesta pasta, não no stdout do app (o usuário do app não é root).
COPY --from=tracer /var/log/datadog /var/log/datadog
ENV CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER={846F5F1C-F9AE-4B07-969E-05C26BC060D8} \
    CORECLR_PROFILER_PATH=/opt/datadog/Datadog.Trace.ClrProfiler.Native.so \
    DD_DOTNET_TRACER_HOME=/opt/datadog \
    DD_TRACE_OTEL_ENABLED=true \
    DD_TRACE_SAMPLE_RATE=1.0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderGenerator.dll"]
