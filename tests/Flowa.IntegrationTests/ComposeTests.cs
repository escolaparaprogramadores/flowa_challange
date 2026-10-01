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

        // O initiator é quem manda o primeiro Logon; o acceptor só responde.
        var generatorFixMessages = FixLog.ParseFixMessages(await compose.ReadServiceLogAsync("ordergenerator"));
        var initiatorLogon = generatorFixMessages.First(fixMessage => fixMessage.TagValue(35) == "A");
        Assert.Equal("FIX.4.4", initiatorLogon.TagValue(8));
        Assert.Equal("ORDERGENERATOR", initiatorLogon.TagValue(49));
        Assert.Equal("ORDERACCUMULATOR", initiatorLogon.TagValue(56));
        Assert.Equal("30", initiatorLogon.TagValue(108));
    }

    [Fact]
    public async Task So_a_porta_da_pagina_fica_aberta_no_host_e_os_apps_nao_rodam_como_root()
    {
        var orderGenerator = await compose.InspectServiceContainerAsync("ordergenerator");
        var orderAccumulator = await compose.InspectServiceContainerAsync("orderaccumulator");
        var postgres = await compose.InspectServiceContainerAsync("postgres");

        // O acceptor FIX só confere SenderCompID/TargetCompID: a 9876 não pode sair da rede do compose.
        Assert.Equal(
            new Dictionary<string, string> { ["8080/tcp"] = $"127.0.0.1:{ComposeFixture.OrderGeneratorHostPort}" },
            orderGenerator.HostPortsByContainerPort);
        Assert.Empty(orderAccumulator.HostPortsByContainerPort);
        Assert.Empty(postgres.HostPortsByContainerPort);

        Assert.DoesNotContain(orderGenerator.RunAsUser, new[] { "", "0", "root" });
        Assert.DoesNotContain(orderAccumulator.RunAsUser, new[] { "", "0", "root" });
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

        await compose.RunComposeCommandAsync(TimeSpan.FromMinutes(3), "up", "-d", "--no-deps", "--force-recreate", "--wait", "orderaccumulator");

        var accumulatorAfter = await compose.InspectServiceContainerAsync("orderaccumulator");
        Assert.NotEqual(accumulatorBefore.ContainerId, accumulatorAfter.ContainerId);

        await compose.WaitForNewFixLogonAsync(logonsBeforeRecreate);

        var generatorAfter = await compose.InspectServiceContainerAsync("ordergenerator");
        Assert.Equal(generatorBefore.ContainerId, generatorAfter.ContainerId);
        Assert.Equal(generatorBefore.StartedAt, generatorAfter.StartedAt);

        var orderAfterRelogon = await PostOrderAsync("VALE3", "buy", 10, 50.00m);
        Assert.Equal("accepted", orderAfterRelogon.GetProperty("status").GetString());
        var clOrdId = orderAfterRelogon.GetProperty("clOrdId").GetString()!;
        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("0", executionReport.TagValue(150));
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

        var exposureLimitPattern = new Regex(@"100[.,_ ]?000[.,_ ]?000|\b1(\.0+)?e\+?0*8\b", RegexOptions.IgnoreCase);
        var filesWithLimit = packagingFiles.Concat(appSettingsFiles)
            .Where(configPath => exposureLimitPattern.IsMatch(File.ReadAllText(configPath)))
            .Select(configPath => Path.GetRelativePath(repoRoot, configPath))
            .ToList();
        Assert.Empty(filesWithLimit);
    }
}
