using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Base.IntegrationTests;

[Collection(ComposeCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class ComposeTests(ComposeFixture composeUnderTest)
{
    [Fact]
    public async Task Os_dois_apps_rodam_em_containers_separados_com_os_papeis_fix_do_contrato()
    {
        var orderGeneratorContainer = await composeUnderTest.InspectServiceContainerAsync("ordergenerator");
        var orderAccumulatorContainer = await composeUnderTest.InspectServiceContainerAsync("orderaccumulator");

        Assert.Equal("running", orderGeneratorContainer.ContainerStatus);
        Assert.Equal("running", orderAccumulatorContainer.ContainerStatus);
        Assert.Equal("dotnet OrderGenerator.dll", orderGeneratorContainer.ContainerEntrypoint);
        Assert.Equal("dotnet OrderAccumulator.dll", orderAccumulatorContainer.ContainerEntrypoint);

        var generatorFixMessages = FixLog.ParseFixMessages(await composeUnderTest.ReadServiceLogAsync("ordergenerator"));
        var initiatorLogon = generatorFixMessages.First(fixMessage => fixMessage.ReadFixTagValue(35) == "A" && fixMessage.ReadFixTagValue(49) == "ORDERGENERATOR");
        Assert.Equal(initiatorLogon, generatorFixMessages.First(fixMessage => fixMessage.ReadFixTagValue(35) == "A"));
        Assert.Equal("FIX.4.4", initiatorLogon.ReadFixTagValue(8));
        Assert.Equal("ORDERACCUMULATOR", initiatorLogon.ReadFixTagValue(56));
        Assert.Equal("30", initiatorLogon.ReadFixTagValue(108));

        var accumulatorFixMessages = FixLog.ParseFixMessages(await composeUnderTest.ReadServiceLogAsync("orderaccumulator"));
        var acceptorLogon = accumulatorFixMessages.First(fixMessage => fixMessage.ReadFixTagValue(35) == "A" && fixMessage.ReadFixTagValue(49) == "ORDERACCUMULATOR");
        Assert.Equal("FIX.4.4", acceptorLogon.ReadFixTagValue(8));
        Assert.Equal("ORDERGENERATOR", acceptorLogon.ReadFixTagValue(56));
    }

    [Fact]
    public async Task So_a_porta_da_pagina_fica_aberta_no_host_e_os_apps_nao_rodam_como_root()
    {
        var orderGeneratorContainer = await composeUnderTest.InspectServiceContainerAsync("ordergenerator");
        var orderAccumulatorContainer = await composeUnderTest.InspectServiceContainerAsync("orderaccumulator");
        var postgresContainer = await composeUnderTest.InspectServiceContainerAsync("postgres");

        // O acceptor FIX só confere SenderCompID/TargetCompID: a 9876 não pode sair da rede do compose.
        Assert.Equal(new[] { $"8080/tcp->127.0.0.1:{ComposeFixture.OrderGeneratorHostPort}" }, orderGeneratorContainer.PublishedPortBindings);
        Assert.Empty(orderAccumulatorContainer.PublishedPortBindings);
        Assert.Empty(postgresContainer.PublishedPortBindings);

        // 1654 é o usuário "app" que a imagem aspnet do .NET já traz (APP_UID).
        Assert.Equal("1654", await composeUnderTest.ReadContainerProcessUserIdAsync("ordergenerator"));
        Assert.Equal("1654", await composeUnderTest.ReadContainerProcessUserIdAsync("orderaccumulator"));
    }

    [Fact]
    public async Task OrderAccumulator_so_sobe_depois_do_postgres_ficar_saudavel()
    {
        using var resolvedComposeConfig = await composeUnderTest.ReadResolvedComposeConfigAsync();
        var composeServices = resolvedComposeConfig.RootElement.GetProperty("services");

        var accumulatorDependsOnPostgres = composeServices.GetProperty("orderaccumulator").GetProperty("depends_on").GetProperty("postgres");
        Assert.Equal("service_healthy", accumulatorDependsOnPostgres.GetProperty("condition").GetString());

        var postgresHealthcheck = composeServices.GetProperty("postgres").GetProperty("healthcheck").GetProperty("test")
            .EnumerateArray().Select(healthcheckCommandPart => healthcheckCommandPart.GetString()).ToArray();
        Assert.Equal(new[] { "CMD-SHELL", "pg_isready -U flowa -d flowa" }, postgresHealthcheck);
    }

    [Fact]
    public async Task Ordem_aceita_sai_como_35_D_e_volta_como_35_8_com_o_mesmo_ClOrdID()
    {
        var acceptedOrder = await PostOrderAsync("PETR4", "buy", 100, 10.50m);

        Assert.Equal("accepted", acceptedOrder.GetProperty("status").GetString());
        Assert.Equal("Ordem aceita.", acceptedOrder.GetProperty("message").GetString());
        var clOrdId = acceptedOrder.GetProperty("clOrdId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", clOrdId);

        var newOrderSingle = await FindSingleFixMessageAsync("ordergenerator", "D", clOrdId, senderCompId: "ORDERGENERATOR");
        Assert.Equal("PETR4", newOrderSingle.ReadFixTagValue(55));
        Assert.Equal("1", newOrderSingle.ReadFixTagValue(54));
        Assert.Equal("100", newOrderSingle.ReadFixTagValue(38));
        Assert.Equal("2", newOrderSingle.ReadFixTagValue(40));

        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("0", executionReport.ReadFixTagValue(150));
        Assert.Equal("0", executionReport.ReadFixTagValue(39));
        Assert.Equal(acceptedOrder.GetProperty("orderId").GetString(), executionReport.ReadFixTagValue(37));
    }

    // CA-O4 da onda 3: o tracer do Datadog vem nas duas imagens, com a amostragem padrão em 1.0.
    [Theory]
    [InlineData("ordergenerator")]
    [InlineData("orderaccumulator")]
    public async Task A_imagem_carrega_o_tracer_do_Datadog_com_amostragem_padrao_1(string serviceName)
    {
        Assert.Equal("1", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "CORECLR_ENABLE_PROFILING"));
        Assert.Equal("{846F5F1C-F9AE-4B07-969E-05C26BC060D8}", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "CORECLR_PROFILER"));
        Assert.Equal("/opt/datadog/Datadog.Trace.ClrProfiler.Native.so", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "CORECLR_PROFILER_PATH"));
        Assert.Equal("/opt/datadog", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "DD_DOTNET_TRACER_HOME"));
        Assert.Equal("true", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "DD_TRACE_OTEL_ENABLED"));
        Assert.Equal("1.0", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "DD_TRACE_SAMPLE_RATE"));
    }

    // CA-O5 da onda 3: com o tracer carregado (sem agente), a ordem sai com a 5100, e o log das duas
    // pontas mostra a tag sem o valor: o trace id não vai ao log (decisão 17).
    [Fact]
    public async Task Ordem_sai_com_a_tag_5100_e_o_log_das_duas_pontas_nao_mostra_o_trace_id()
    {
        var acceptedOrder = await PostOrderAsync("PETR4", "buy", 7, 10.10m);
        var clOrdId = acceptedOrder.GetProperty("clOrdId").GetString()!;

        var sentNewOrderSingle = await FindSingleFixMessageAsync("ordergenerator", "D", clOrdId, senderCompId: "ORDERGENERATOR");
        var receivedNewOrderSingle = await FindSingleFixMessageAsync("orderaccumulator", "D", clOrdId, senderCompId: "ORDERGENERATOR");
        Assert.Equal("***", sentNewOrderSingle.ReadFixTagValue(5100));
        Assert.Equal("***", receivedNewOrderSingle.ReadFixTagValue(5100));
        Assert.DoesNotMatch(TraceParentW3C, await composeUnderTest.ReadServiceLogAsync("ordergenerator"));
        Assert.DoesNotMatch(TraceParentW3C, await composeUnderTest.ReadServiceLogAsync("orderaccumulator"));
    }

    private static readonly Regex TraceParentW3C = new("[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}");

    [Fact]
    public async Task Ordem_que_passa_do_limite_volta_rejeitada_com_150_8_e_o_texto_do_contrato()
    {
        // Cada venda vale 99.998.000,01; a segunda passaria de 100 milhões no mesmo símbolo.
        var firstVIIA4Sale = await PostOrderAsync("VIIA4", "sell", 99999, 999.99m);
        Assert.Equal("accepted", firstVIIA4Sale.GetProperty("status").GetString());

        var secondVIIA4Sale = await PostOrderAsync("VIIA4", "sell", 99999, 999.99m);
        const string expectedLimitRejectionText = "Ordem rejeitada: a exposição de VIIA4 passaria do limite de 100.000.000,00.";
        Assert.Equal("rejected", secondVIIA4Sale.GetProperty("status").GetString());
        Assert.Equal(expectedLimitRejectionText, secondVIIA4Sale.GetProperty("message").GetString());

        var clOrdId = secondVIIA4Sale.GetProperty("clOrdId").GetString()!;
        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("8", executionReport.ReadFixTagValue(150));
        Assert.Equal("8", executionReport.ReadFixTagValue(39));
        Assert.Equal(expectedLimitRejectionText, executionReport.ReadFixTagValue(58));
    }

    [Fact]
    public async Task Pagina_e_exposicao_respondem_pelo_OrderGenerator()
    {
        using var pageResponse = await composeUnderTest.OrderGeneratorHttp.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Equal("text/html", pageResponse.Content.Headers.ContentType?.MediaType);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync();
        Assert.Contains("<title>Base investimentos — Boleta de ordens</title>", pageHtml);
        Assert.Contains("<div id=\"raiz\"></div>", pageHtml);

        using var exposuresResponse = await composeUnderTest.OrderGeneratorHttp.GetAsync("/api/exposures");
        Assert.Equal(HttpStatusCode.OK, exposuresResponse.StatusCode);
        var exposuresBody = await exposuresResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100000000.00m, exposuresBody.GetProperty("limit").GetDecimal());
        var exposureSymbols = exposuresBody.GetProperty("exposures").EnumerateArray()
            .Select(symbolExposureRow => symbolExposureRow.GetProperty("symbol").GetString()!)
            .ToArray();
        Assert.Equal(new[] { "PETR4", "VALE3", "VIIA4" }, exposureSymbols);
    }

    [Fact]
    public async Task Depois_de_recriar_o_OrderAccumulator_o_OrderGenerator_reloga_sozinho_e_aceita_a_proxima_ordem()
    {
        var orderGeneratorContainerBeforeRecreate = await composeUnderTest.InspectServiceContainerAsync("ordergenerator");
        var orderAccumulatorContainerBeforeRecreate = await composeUnderTest.InspectServiceContainerAsync("orderaccumulator");
        var logonsBeforeRecreate = await composeUnderTest.CountFixLogonsFromAcceptorAsync();

        // Só este teste usa VALE3: a exposição dele parte de zero no compose de teste.
        var orderBeforeRecreate = await PostOrderAsync("VALE3", "buy", 1, 1.00m);
        Assert.Equal("accepted", orderBeforeRecreate.GetProperty("status").GetString());
        Assert.Equal(1.00m, await ReadSymbolExposureAsync("VALE3"));

        await composeUnderTest.RunComposeCommandAsync(TimeSpan.FromMinutes(3), "up", "-d", "--no-deps", "--force-recreate", "--wait", "orderaccumulator");

        var orderAccumulatorContainerAfterRecreate = await composeUnderTest.InspectServiceContainerAsync("orderaccumulator");
        Assert.NotEqual(orderAccumulatorContainerBeforeRecreate.ContainerId, orderAccumulatorContainerAfterRecreate.ContainerId);

        await composeUnderTest.WaitForNewFixLogonAsync(logonsBeforeRecreate);

        var orderGeneratorContainerAfterRecreate = await composeUnderTest.InspectServiceContainerAsync("ordergenerator");
        Assert.Equal(orderGeneratorContainerBeforeRecreate.ContainerId, orderGeneratorContainerAfterRecreate.ContainerId);
        Assert.Equal(orderGeneratorContainerBeforeRecreate.ContainerStartedAt, orderGeneratorContainerAfterRecreate.ContainerStartedAt);

        // O OrderAccumulator novo não guarda nada em memória: lê do banco a exposição de antes.
        Assert.Equal(1.00m, await ReadSymbolExposureAsync("VALE3"));

        var orderAfterRelogon = await PostOrderAsync("VALE3", "buy", 10, 50.00m);
        Assert.Equal("accepted", orderAfterRelogon.GetProperty("status").GetString());
        var clOrdId = orderAfterRelogon.GetProperty("clOrdId").GetString()!;
        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("0", executionReport.ReadFixTagValue(150));
        Assert.Equal(501.00m, await ReadSymbolExposureAsync("VALE3"));
    }

    [Fact]
    public async Task Os_dados_do_postgres_ficam_num_volume_nomeado()
    {
        var postgresContainer = await composeUnderTest.InspectServiceContainerAsync("postgres");

        Assert.Equal(new[] { $"volume:{ComposeFixture.ComposeProjectName}_pgdata->/var/lib/postgresql/data" }, postgresContainer.VolumeMounts);
    }

    private async Task<decimal> ReadSymbolExposureAsync(string symbol)
    {
        var exposuresBody = await composeUnderTest.OrderGeneratorHttp.GetFromJsonAsync<JsonElement>("/api/exposures");
        var symbolExposureRow = Assert.Single(exposuresBody.GetProperty("exposures").EnumerateArray(),
            exposureRowOfSymbol => exposureRowOfSymbol.GetProperty("symbol").GetString() == symbol);
        return symbolExposureRow.GetProperty("exposure").GetDecimal();
    }

    private async Task<JsonElement> PostOrderAsync(string symbol, string side, int quantity, decimal price)
    {
        using var createOrderResponse = await composeUnderTest.OrderGeneratorHttp.PostAsJsonAsync("/api/orders", new { symbol, side, quantity, price });
        Assert.Equal(HttpStatusCode.OK, createOrderResponse.StatusCode);
        return await createOrderResponse.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<FixMessage> FindSingleFixMessageAsync(string serviceName, string fixMsgType, string clOrdId, string senderCompId)
    {
        var matchingFixMessages = FixLog.ParseFixMessages(await composeUnderTest.ReadServiceLogAsync(serviceName))
            .Where(fixMessage => fixMessage.ReadFixTagValue(35) == fixMsgType && fixMessage.ReadFixTagValue(11) == clOrdId && fixMessage.ReadFixTagValue(49) == senderCompId)
            .ToList();
        return Assert.Single(matchingFixMessages);
    }
}

// Roda sem Docker: o limite é constante do OrderAccumulator e não pode vazar para configuração (CA-21).
public sealed class ExposureLimitOutsideConfigTests
{
    private static readonly Regex ExposureLimitPattern = new(@"100[.,_ ]?000[.,_ ]?000|\b1(\.0+)?e\+?0*8\b", RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("100000000")]
    [InlineData("100.000.000")]
    [InlineData("100,000,000")]
    [InlineData("100_000_000")]
    [InlineData("100 000 000")]
    [InlineData("100000000.00")]
    [InlineData("1e8")]
    [InlineData("1E+08")]
    [InlineData("1.0e8")]
    [InlineData("Exposure__Limit: \"1.0E8\"")]
    public void A_guarda_acha_o_limite_em_cada_grafia(string limitSpelling) =>
        Assert.Matches(ExposureLimitPattern, limitSpelling);

    [Theory]
    [InlineData("99999999")]
    [InlineData("10000000")]
    [InlineData("1.5e8")]
    [InlineData("Port=5432")]
    public void A_guarda_nao_confunde_outros_numeros_com_o_limite(string numberThatIsNotTheLimit) =>
        Assert.DoesNotMatch(ExposureLimitPattern, numberThatIsNotTheLimit);

    [Fact]
    public void Limite_de_exposicao_nao_aparece_no_compose_nos_Dockerfiles_nem_em_appsettings()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var packagingFiles = new[] { "docker-compose.yml", "src/app-base-order-generator-webapi-ecs/Dockerfile", "src/app-base-order-accumulator-webapi-ecs/Dockerfile" }
            .Select(packagingRelativePath => Path.Combine(repoRoot, packagingRelativePath))
            .ToList();
        var appSettingsFiles = Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "appsettings*.json", SearchOption.AllDirectories)
            .Where(appSettingsPath => !appSettingsPath.Split(Path.DirectorySeparatorChar).Any(pathSegment => pathSegment is "bin" or "obj"))
            .ToList();

        Assert.All(packagingFiles, packagingPath => Assert.True(File.Exists(packagingPath), $"{packagingPath} não existe"));
        Assert.Contains(appSettingsFiles, appSettingsPath => appSettingsPath.Contains("app-base-order-accumulator-webapi-ecs"));

        var configFilesWithLimit = packagingFiles.Concat(appSettingsFiles)
            .Where(checkedConfigPath => ExposureLimitPattern.IsMatch(File.ReadAllText(checkedConfigPath)))
            .Select(checkedConfigPath => Path.GetRelativePath(repoRoot, checkedConfigPath))
            .ToList();
        Assert.Empty(configFilesWithLimit);
    }
}

// Roda sem Docker. O .git fica no contexto de propósito: o build lê o commit dele.
public sealed class DockerBuildContextTests
{
    [Fact]
    public void O_dockerignore_tira_so_saidas_de_build_e_arquivos_locais()
    {
        var dockerignorePatterns = File.ReadAllLines(Path.Combine(RepoPaths.FindRepoRoot(), ".dockerignore"))
            .Select(dockerignoreLine => dockerignoreLine.Trim())
            .Where(dockerignoreLine => dockerignoreLine.Length > 0 && !dockerignoreLine.StartsWith('#'))
            .ToArray();

        Assert.Equal(
            new[]
            {
                ".vs/", ".vscode/", ".idea/",
                "**/bin/", "**/obj/", "**/node_modules/", "**/dist/",
                "**/TestResults/", "**/playwright-report/", "**/test-results/",
                "src/app-base-order-generator-webapi-ecs/wwwroot/",
                ".env", ".env.*", "*.log",
            },
            dockerignorePatterns);
    }
}

// Achado do aceite: um clone de um commit novo, subido com o mesmo nome de projeto, reaproveitava
// a imagem do commit anterior e o /version mentia. O compose agora reconstrói sempre no up.
[Collection(ComposeCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class CleanCloneImageCommitTests
{
    private const string CloneComposeProjectName = "flowa-it-commit";
    private const int CloneOrderGeneratorHostPort = 18093;
    private static readonly TimeSpan CloneComposeUpTimeout = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task Docker_compose_up_de_um_clone_serve_o_commit_atual_mesmo_com_a_imagem_do_commit_anterior()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var cloneDirectory = Path.Combine(Path.GetTempPath(), $"flowa-it-commit-{Guid.NewGuid():N}");
        var cloneComposeFile = Path.Combine(cloneDirectory, "docker-compose.yml");
        // Sem SOURCE_REVISION_ID: o clone tem .git de verdade, como o do avaliador.
        var cloneEnvironmentVariables = new Dictionary<string, string?>
        {
            ["FLOWA_HTTP_PORT"] = CloneOrderGeneratorHostPort.ToString(), ["SOURCE_REVISION_ID"] = null,
            ["GIT_AUTHOR_NAME"] = "flowa-it", ["GIT_AUTHOR_EMAIL"] = "flowa-it@localhost",
            ["GIT_COMMITTER_NAME"] = "flowa-it", ["GIT_COMMITTER_EMAIL"] = "flowa-it@localhost",
        };
        Task<string> CaptureCloneExternalCommandOutputAsync(TimeSpan commandTimeout, string commandExecutable, params string[] commandArguments) =>
            ExternalCommand.CaptureExternalCommandOutputAsync(commandExecutable, commandTimeout, cloneEnvironmentVariables, commandArguments);
        Task<string> RunCloneComposeCommandAsync(TimeSpan commandTimeout, params string[] composeArguments) =>
            CaptureCloneExternalCommandOutputAsync(commandTimeout, "docker", ["compose", "-p", CloneComposeProjectName, "-f", cloneComposeFile, .. composeArguments]);

        // O GET /version sai de dentro de cada container, pelo /dev/tcp do bash: a imagem aspnet não tem
        // curl e a 8081 do OrderAccumulator nem sai da rede do compose.
        async Task<string> ReadCloneServiceCommitAsync(string serviceName, int containerHttpPort)
        {
            var lastVersionFailure = "nenhuma resposta";
            var versionDeadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < versionDeadline)
            {
                try
                {
                    var versionHttpResponse = await RunCloneComposeCommandAsync(TimeSpan.FromSeconds(20), "exec", "-T", serviceName, "bash", "-c",
                        $"exec 3<>/dev/tcp/127.0.0.1/{containerHttpPort} && printf 'GET /version HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && cat <&3");
                    var versionJsonStart = versionHttpResponse.IndexOf('{');
                    if (versionJsonStart >= 0)
                    {
                        using var versionDocument = JsonDocument.Parse(versionHttpResponse[versionJsonStart..]);
                        return versionDocument.RootElement.GetProperty("commit").GetString()!;
                    }
                    lastVersionFailure = versionHttpResponse;
                }
                catch (Exception versionFailure) when (versionFailure is InvalidOperationException or TimeoutException)
                {
                    lastVersionFailure = versionFailure.Message;
                }
                await Task.Delay(500);
            }
            throw new TimeoutException($"o /version de {serviceName} não respondeu em 2 minutos; última falha: {lastVersionFailure}");
        }

        var repoHeadCommit = (await CaptureCloneExternalCommandOutputAsync(TimeSpan.FromSeconds(30), "git", "-C", repoRoot, "rev-parse", "HEAD")).Trim();
        var cloneTestFailed = false;
        try
        {
            await CaptureCloneExternalCommandOutputAsync(TimeSpan.FromMinutes(2), "git", "clone", "--quiet", "--no-local", repoRoot, cloneDirectory);
            await CaptureCloneExternalCommandOutputAsync(TimeSpan.FromSeconds(30), "git", "-C", cloneDirectory, "checkout", "--quiet", repoHeadCommit);

            await RunCloneComposeCommandAsync(CloneComposeUpTimeout, "up", "-d", "--wait", "--wait-timeout", "300");
            Assert.Equal(repoHeadCommit, await ReadCloneServiceCommitAsync("ordergenerator", 8080));
            Assert.Equal(repoHeadCommit, await ReadCloneServiceCommitAsync("orderaccumulator", 8081));
            await RunCloneComposeCommandAsync(TimeSpan.FromMinutes(2), "down", "-v");

            await CaptureCloneExternalCommandOutputAsync(TimeSpan.FromSeconds(30), "git", "-C", cloneDirectory, "commit", "--allow-empty", "--quiet", "-m", "commit novo do teste");
            var newCloneCommit = (await CaptureCloneExternalCommandOutputAsync(TimeSpan.FromSeconds(30), "git", "-C", cloneDirectory, "rev-parse", "HEAD")).Trim();
            Assert.NotEqual(repoHeadCommit, newCloneCommit);

            await RunCloneComposeCommandAsync(CloneComposeUpTimeout, "up", "-d", "--wait", "--wait-timeout", "300");
            Assert.Equal(newCloneCommit, await ReadCloneServiceCommitAsync("ordergenerator", 8080));
            Assert.Equal(newCloneCommit, await ReadCloneServiceCommitAsync("orderaccumulator", 8081));
        }
        catch
        {
            cloneTestFailed = true;
            throw;
        }
        finally
        {
            // Todo passo da limpeza roda; com o teste já reprovado, falha de limpeza só vai para o log.
            Func<Task>[] cloneCleanupSteps =
            [
                async () => { if (File.Exists(cloneComposeFile)) await RunCloneComposeCommandAsync(TimeSpan.FromMinutes(2), "down", "-v", "--rmi", "local"); },
                () => { DeleteCloneDirectory(cloneDirectory); return Task.CompletedTask; },
            ];
            var cloneCleanupFailures = new List<Exception>();
            foreach (var cloneCleanupStep in cloneCleanupSteps)
            {
                try { await cloneCleanupStep(); }
                catch (Exception cloneCleanupFailure) { cloneCleanupFailures.Add(cloneCleanupFailure); }
            }
            if (cloneCleanupFailures.Count > 0 && !cloneTestFailed) throw new AggregateException("a limpeza do clone falhou", cloneCleanupFailures);
            foreach (var cloneCleanupFailure in cloneCleanupFailures)
                Console.Error.WriteLine($"limpeza do clone falhou depois da reprovação: {cloneCleanupFailure.Message}");
        }
    }

    // Os objetos do git ficam só leitura no Windows; sem limpar o atributo, o Delete falha.
    private static void DeleteCloneDirectory(string cloneDirectory)
    {
        if (!Directory.Exists(cloneDirectory)) return;
        foreach (var cloneFile in Directory.EnumerateFiles(cloneDirectory, "*", SearchOption.AllDirectories))
            File.SetAttributes(cloneFile, FileAttributes.Normal);
        Directory.Delete(cloneDirectory, recursive: true);
    }
}
