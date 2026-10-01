# Build a partir da raiz do repositório: docker build -f src/OrderAccumulator/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props ./
COPY src/Flowa.Shared/Flowa.Shared.csproj src/Flowa.Shared/
COPY src/OrderAccumulator/OrderAccumulator.csproj src/OrderAccumulator/
RUN dotnet restore src/OrderAccumulator/OrderAccumulator.csproj
COPY src/Flowa.Shared/ src/Flowa.Shared/
COPY src/OrderAccumulator/ src/OrderAccumulator/
# Os apps só sobem com o commit gravado no build (rota /version). No clone, o SHA sai do .git;
# numa worktree, onde .git é só um arquivo, quem chama passa SOURCE_REVISION_ID.
ARG SOURCE_REVISION_ID=
RUN --mount=type=bind,source=.git,target=/repo/.git \
    sha="${SOURCE_REVISION_ID:-$(git -c safe.directory='*' -C /repo rev-parse HEAD 2>/dev/null)}"; \
    if [ ${#sha} -ne 40 ]; then echo "Sem o commit: rode dentro de um clone git ou passe SOURCE_REVISION_ID" >&2; exit 1; fi; \
    dotnet publish src/OrderAccumulator/OrderAccumulator.csproj -c Release -o /app --no-restore \
        -p:SourceRevisionId=$sha -p:EnableSourceControlManagerQueries=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8081 9876
ENTRYPOINT ["dotnet", "OrderAccumulator.dll"]
