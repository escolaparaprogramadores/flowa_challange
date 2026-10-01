# Build a partir da raiz do repositório: docker build -f src/OrderGenerator/Dockerfile .
# A tela é construída com o Vite e servida pelo próprio OrderGenerator (wwwroot).
FROM node:22-alpine AS front
WORKDIR /repo/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

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
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderGenerator.dll"]
