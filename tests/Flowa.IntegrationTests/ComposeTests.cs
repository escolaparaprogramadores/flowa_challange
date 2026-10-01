using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flowa.IntegrationTests;

// Os dois apps em containers separados, falando FIX de verdade pela rede do compose.
[Collection(ComposeCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ComposeTests(ComposeFixture compose)
{
    [Fact]
    public async Task Os_dois_apps_rodam_em_containers_separados_com_os_papeis_fix_do_contrato()
    {
        var orderGenerator = await compose.InspectServiceContainerAsync("ordergenerator");
        var orderAccumulator = await compose.InspectServiceContainerAsync("orderaccumulator");

        Assert.Equal("running", orderGenerator.Status);
        Assert.Equal("running", orderAccumulator.Status);
        Assert.Equal("dotnet OrderGenerator.dll", orderGenerator.Entrypoint);
        Assert.Equal("dotnet OrderAccumulator.dll", orderAccumulator.Entrypoint);

        // O initiator manda o primeiro Logon, saindo do log do OrderGenerator.
        var generatorFixMessages = FixLog.ParseFixMessages(await compose.ReadServiceLogAsync("ordergenerator"));
        var initiatorLogon = generatorFixMessages.First(fixMessage => fixMessage.TagValue(35) == "A" && fixMessage.TagValue(49) == "ORDERGENERATOR");
        Assert.Equal(initiatorLogon, generatorFixMessages.First(fixMessage => fixMessage.TagValue(35) == "A"));
        Assert.Equal("FIX.4.4", initiatorLogon.TagValue(8));
        Assert.Equal("ORDERACCUMULATOR", initiatorLogon.TagValue(56));
        Assert.Equal("30", initiatorLogon.TagValue(108));

        // O acceptor responde o Logon com os CompIDs trocados, saindo do log do OrderAccumulator.
        var accumulatorFixMessages = FixLog.ParseFixMessages(await compose.ReadServiceLogAsync("orderaccumulator"));
        var acceptorLogon = accumulatorFixMessages.First(fixMessage => fixMessage.TagValue(35) == "A" && fixMessage.TagValue(49) == "ORDERACCUMULATOR");
        Assert.Equal("FIX.4.4", acceptorLogon.TagValue(8));
        Assert.Equal("ORDERGENERATOR", acceptorLogon.TagValue(56));
    }

    [Fact]
    public async Task So_a_porta_da_pagina_fica_aberta_no_host_e_os_apps_nao_rodam_como_root()
    {
        var orderGenerator = await compose.InspectServiceContainerAsync("ordergenerator");
        var orderAccumulator = await compose.InspectServiceContainerAsync("orderaccumulator");
        var postgres = await compose.InspectServiceContainerAsync("postgres");

        // O acceptor FIX só confere SenderCompID/TargetCompID: a 9876 não pode sair da rede do compose.
        Assert.Equal(new[] { $"8080/tcp->127.0.0.1:{ComposeFixture.OrderGeneratorHostPort}" }, orderGenerator.PublishedPortBindings);
        Assert.Empty(orderAccumulator.PublishedPortBindings);
        Assert.Empty(postgres.PublishedPortBindings);

        // 1654 é o usuário "app" que a imagem aspnet do .NET já traz (APP_UID).
        Assert.Equal("1654", await compose.ReadContainerProcessUserIdAsync("ordergenerator"));
        Assert.Equal("1654", await compose.ReadContainerProcessUserIdAsync("orderaccumulator"));
    }

    [Fact]
    public async Task OrderAccumulator_so_sobe_depois_do_postgres_ficar_saudavel()
    {
        using var resolvedConfig = await compose.ReadResolvedComposeConfigAsync();
        var services = resolvedConfig.RootElement.GetProperty("services");

        var accumulatorDependsOnPostgres = services.GetProperty("orderaccumulator").GetProperty("depends_on").GetProperty("postgres");
        Assert.Equal("service_healthy", accumulatorDependsOnPostgres.GetProperty("condition").GetString());

        var postgresHealthcheck = services.GetProperty("postgres").GetProperty("healthcheck").GetProperty("test")
            .EnumerateArray().Select(healthcheckPart => healthcheckPart.GetString()).ToArray();
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
        Assert.Equal("PETR4", newOrderSingle.TagValue(55));
        Assert.Equal("1", newOrderSingle.TagValue(54));
        Assert.Equal("100", newOrderSingle.TagValue(38));
        Assert.Equal("2", newOrderSingle.TagValue(40));

        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("0", executionReport.TagValue(150));
        Assert.Equal("0", executionReport.TagValue(39));
        Assert.Equal(acceptedOrder.GetProperty("orderId").GetString(), executionReport.TagValue(37));
    }

    [Fact]
    public async Task Ordem_que_passa_do_limite_volta_rejeitada_com_150_8_e_o_texto_do_contrato()
    {
        // Cada venda vale 99.998.000,01; a segunda passaria de 100 milhões no mesmo símbolo.
        var firstSale = await PostOrderAsync("VIIA4", "sell", 99999, 999.99m);
        Assert.Equal("accepted", firstSale.GetProperty("status").GetString());

        var secondSale = await PostOrderAsync("VIIA4", "sell", 99999, 999.99m);
        const string limitRejection = "Ordem rejeitada: a exposição de VIIA4 passaria do limite de 100.000.000,00.";
        Assert.Equal("rejected", secondSale.GetProperty("status").GetString());
        Assert.Equal(limitRejection, secondSale.GetProperty("message").GetString());

        var clOrdId = secondSale.GetProperty("clOrdId").GetString()!;
        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("8", executionReport.TagValue(150));
        Assert.Equal("8", executionReport.TagValue(39));
        Assert.Equal(limitRejection, executionReport.TagValue(58));
    }

    [Fact]
    public async Task Pagina_e_exposicao_respondem_pelo_OrderGenerator()
    {
        using var pageResponse = await compose.OrderGeneratorHttp.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Equal("text/html", pageResponse.Content.Headers.ContentType?.MediaType);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync();
        Assert.Contains("<title>Base investimentos — Boleta de ordens</title>", pageHtml);
        Assert.Contains("<div id=\"raiz\"></div>", pageHtml);

        using var exposuresResponse = await compose.OrderGeneratorHttp.GetAsync("/api/exposures");
        Assert.Equal(HttpStatusCode.OK, exposuresResponse.StatusCode);
        var exposures = await exposuresResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100000000.00m, exposures.GetProperty("limit").GetDecimal());
        var exposureSymbols = exposures.GetProperty("exposures").EnumerateArray()
            .Select(symbolExposure => symbolExposure.GetProperty("symbol").GetString()!)
            .ToArray();
        Assert.Equal(new[] { "PETR4", "VALE3", "VIIA4" }, exposureSymbols);
    }

    [Fact]
    public async Task Depois_de_recriar_o_OrderAccumulator_o_OrderGenerator_reloga_sozinho_e_aceita_a_proxima_ordem()
    {
        var generatorBefore = await compose.InspectServiceContainerAsync("ordergenerator");
        var accumulatorBefore = await compose.InspectServiceContainerAsync("orderaccumulator");
        var logonsBeforeRecreate = await compose.CountFixLogonsFromAcceptorAsync();

        // Só este teste usa VALE3: a exposição dele parte de zero no compose de teste.
        var orderBeforeRecreate = await PostOrderAsync("VALE3", "buy", 1, 1.00m);
        Assert.Equal("accepted", orderBeforeRecreate.GetProperty("status").GetString());
        Assert.Equal(1.00m, await ReadSymbolExposureAsync("VALE3"));

        await compose.RunComposeCommandAsync(TimeSpan.FromMinutes(3), "up", "-d", "--no-deps", "--force-recreate", "--wait", "orderaccumulator");

        var accumulatorAfter = await compose.InspectServiceContainerAsync("orderaccumulator");
        Assert.NotEqual(accumulatorBefore.ContainerId, accumulatorAfter.ContainerId);

        await compose.WaitForNewFixLogonAsync(logonsBeforeRecreate);

        var generatorAfter = await compose.InspectServiceContainerAsync("ordergenerator");
        Assert.Equal(generatorBefore.ContainerId, generatorAfter.ContainerId);
        Assert.Equal(generatorBefore.StartedAt, generatorAfter.StartedAt);

        // O OrderAccumulator novo não guarda nada em memória: lê do banco a exposição de antes.
        Assert.Equal(1.00m, await ReadSymbolExposureAsync("VALE3"));

        var orderAfterRelogon = await PostOrderAsync("VALE3", "buy", 10, 50.00m);
        Assert.Equal("accepted", orderAfterRelogon.GetProperty("status").GetString());
        var clOrdId = orderAfterRelogon.GetProperty("clOrdId").GetString()!;
        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("0", executionReport.TagValue(150));
        Assert.Equal(501.00m, await ReadSymbolExposureAsync("VALE3"));
    }

    [Fact]
    public async Task Os_dados_do_postgres_ficam_num_volume_nomeado()
    {
        var postgres = await compose.InspectServiceContainerAsync("postgres");

        Assert.Equal(new[] { $"volume:{ComposeFixture.ComposeProjectName}_pgdata->/var/lib/postgresql/data" }, postgres.VolumeMounts);
    }

    private async Task<decimal> ReadSymbolExposureAsync(string symbol)
    {
        var exposures = await compose.OrderGeneratorHttp.GetFromJsonAsync<JsonElement>("/api/exposures");
        var symbolExposure = Assert.Single(exposures.GetProperty("exposures").EnumerateArray(),
            exposureRow => exposureRow.GetProperty("symbol").GetString() == symbol);
        return symbolExposure.GetProperty("exposure").GetDecimal();
    }

    private async Task<JsonElement> PostOrderAsync(string symbol, string side, int quantity, decimal price)
    {
        using var orderResponse = await compose.OrderGeneratorHttp.PostAsJsonAsync("/api/orders", new { symbol, side, quantity, price });
        Assert.Equal(HttpStatusCode.OK, orderResponse.StatusCode);
        return await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
    }

    // A mensagem do ClOrdID no log de quem a enviou; tem de existir uma só.
    private async Task<FixMessage> FindSingleFixMessageAsync(string serviceName, string msgType, string clOrdId, string senderCompId)
    {
        var matchingMessages = FixLog.ParseFixMessages(await compose.ReadServiceLogAsync(serviceName))
            .Where(fixMessage => fixMessage.TagValue(35) == msgType && fixMessage.TagValue(11) == clOrdId && fixMessage.TagValue(49) == senderCompId)
            .ToList();
        return Assert.Single(matchingMessages);
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
    public void A_guarda_acha_o_limite_em_cada_grafia(string limitWritten) =>
        Assert.Matches(ExposureLimitPattern, limitWritten);

    [Theory]
    [InlineData("99999999")]
    [InlineData("10000000")]
    [InlineData("1.5e8")]
    [InlineData("Port=5432")]
    public void A_guarda_nao_confunde_outros_numeros_com_o_limite(string otherNumber) =>
        Assert.DoesNotMatch(ExposureLimitPattern, otherNumber);

    [Fact]
    public void Limite_de_exposicao_nao_aparece_no_compose_nos_Dockerfiles_nem_em_appsettings()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var packagingFiles = new[] { "docker-compose.yml", "src/OrderGenerator/Dockerfile", "src/OrderAccumulator/Dockerfile" }
            .Select(relativePath => Path.Combine(repoRoot, relativePath))
            .ToList();
        var appSettingsFiles = Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "appsettings*.json", SearchOption.AllDirectories)
            .Where(settingsPath => !settingsPath.Split(Path.DirectorySeparatorChar).Any(folder => folder is "bin" or "obj"))
            .ToList();

        Assert.All(packagingFiles, packagingPath => Assert.True(File.Exists(packagingPath), $"{packagingPath} não existe"));
        Assert.Contains(appSettingsFiles, settingsPath => settingsPath.Contains("OrderAccumulator"));

        var filesWithLimit = packagingFiles.Concat(appSettingsFiles)
            .Where(configPath => ExposureLimitPattern.IsMatch(File.ReadAllText(configPath)))
            .Select(configPath => Path.GetRelativePath(repoRoot, configPath))
            .ToList();
        Assert.Empty(filesWithLimit);
    }
}

// Roda sem Docker: o contexto de build deixa de fora só lixo de build e arquivos locais,
// e mantém o .git (o build lê o commit dele) e tudo o que os Dockerfiles copiam.
public sealed class DockerBuildContextTests
{
    [Fact]
    public void O_dockerignore_tira_so_saidas_de_build_e_arquivos_locais()
    {
        var ignoredPatterns = File.ReadAllLines(Path.Combine(RepoPaths.FindRepoRoot(), ".dockerignore"))
            .Select(dockerignoreLine => dockerignoreLine.Trim())
            .Where(dockerignoreLine => dockerignoreLine.Length > 0 && !dockerignoreLine.StartsWith('#'))
            .ToArray();

        Assert.Equal(
            new[]
            {
                ".vs/", ".vscode/", ".idea/",
                "**/bin/", "**/obj/", "**/node_modules/", "**/dist/",
                "**/TestResults/", "**/playwright-report/", "**/test-results/",
                "src/OrderGenerator/wwwroot/",
                ".env", ".env.*", "*.log",
            },
            ignoredPatterns);
    }
}
