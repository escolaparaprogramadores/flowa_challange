using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Base.IntegrationTests;

// Sobe o docker-compose.yml da raiz num projeto próprio (nome, porta e volume isolados),
// para não brigar com um compose que já esteja de pé na máquina. Derruba tudo no fim.
public sealed class ComposeFixture : IAsyncLifetime
{
    public const string ComposeProjectName = "flowa-it";
    public const int OrderGeneratorHostPort = 18080;

    private static readonly TimeSpan ImageBuildTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AppStartTimeout = TimeSpan.FromMinutes(2);

    // Os apps exigem o commit gravado no build. Numa worktree o .git é só um arquivo e o
    // Docker não consegue lê-lo, então o SHA vai pronto como argumento do build.
    private string? sourceRevisionId;

    public string RepoRoot { get; } = RepoPaths.FindRepoRoot();
    public HttpClient OrderGeneratorHttp { get; } = new()
    {
        BaseAddress = new Uri($"http://localhost:{OrderGeneratorHostPort}"),
        Timeout = TimeSpan.FromSeconds(15),
    };

    public async Task InitializeAsync()
    {
        sourceRevisionId = (await CaptureComposeTestCommandOutputAsync("git", TimeSpan.FromSeconds(30), "-C", RepoRoot, "rev-parse", "HEAD")).Trim();
        await RunComposeCommandAsync(TimeSpan.FromMinutes(2), "down", "-v", "--remove-orphans");
        await RunComposeCommandAsync(ImageBuildTimeout, "up", "-d", "--build", "--wait", "--wait-timeout", "180");
        await WaitForOrderGeneratorHealthAsync();
        await WaitForNewFixLogonAsync(logonsAlreadySeen: 0);
    }

    public async Task DisposeAsync()
    {
        // O down fica no finally: falhar ao gravar o log FIX da prova não pode deixar o compose de pé.
        try
        {
            var fixLogFolder = Environment.GetEnvironmentVariable("FLOWA_IT_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(fixLogFolder))
            {
                Directory.CreateDirectory(fixLogFolder);
                foreach (var serviceName in new[] { "ordergenerator", "orderaccumulator" })
                {
                    var serviceLog = await ReadServiceLogAsync(serviceName);
                    await File.WriteAllTextAsync(Path.Combine(fixLogFolder, $"fix-{serviceName}.log"), serviceLog.Replace('\u0001', '|'));
                }
            }
        }
        finally
        {
            await RunComposeCommandAsync(TimeSpan.FromMinutes(2), "down", "-v", "--remove-orphans");
            OrderGeneratorHttp.Dispose();
        }
    }

    public Task<string> ReadServiceLogAsync(string serviceName) =>
        RunComposeCommandAsync(TimeSpan.FromSeconds(30), "logs", "--no-color", "--no-log-prefix", serviceName);

    // Only what the app writes to stdout (the log lines); docker logs sends the container stderr to its own stderr,
    // where the native runtime writes notices such as a missing libgssapi.
    public async Task<string> ReadServiceStdoutAsync(string serviceName)
    {
        var containerId = (await RunComposeCommandAsync(TimeSpan.FromSeconds(30), "ps", "-q", serviceName)).Trim();
        return await CaptureComposeTestCommandOutputAsync("docker", TimeSpan.FromSeconds(30), "logs", containerId);
    }

    public async Task<ServiceContainerState> InspectServiceContainerAsync(string serviceName)
    {
        var containerId = (await RunComposeCommandAsync(TimeSpan.FromSeconds(30), "ps", "-q", serviceName)).Trim();
        Assert.False(string.IsNullOrEmpty(containerId), $"o serviço {serviceName} não tem container");

        var containerInspectJson = await CaptureComposeTestCommandOutputAsync("docker", TimeSpan.FromSeconds(30), "inspect", containerId);
        using var containerInspectDocument = JsonDocument.Parse(containerInspectJson);
        var serviceContainer = containerInspectDocument.RootElement[0];
        var containerState = serviceContainer.GetProperty("State");

        // Uma entrada por ligação no host: uma segunda ligação da mesma porta não pode sumir da comparação.
        var publishedPortBindings = new List<string>();
        foreach (var exposedContainerPort in serviceContainer.GetProperty("NetworkSettings").GetProperty("Ports").EnumerateObject())
        {
            if (exposedContainerPort.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var hostPortBinding in exposedContainerPort.Value.EnumerateArray())
                publishedPortBindings.Add(
                    $"{exposedContainerPort.Name}->{hostPortBinding.GetProperty("HostIp").GetString()}:{hostPortBinding.GetProperty("HostPort").GetString()}");
        }

        return new ServiceContainerState(
            serviceContainer.GetProperty("Id").GetString()!,
            containerState.GetProperty("Status").GetString()!,
            containerState.GetProperty("StartedAt").GetString()!,
            string.Join(' ', serviceContainer.GetProperty("Config").GetProperty("Entrypoint").EnumerateArray().Select(entrypointPart => entrypointPart.GetString())),
            publishedPortBindings.Order().ToList(),
            serviceContainer.GetProperty("Mounts").EnumerateArray()
                .Select(containerMount => $"{containerMount.GetProperty("Type").GetString()}:{containerMount.GetProperty("Name").GetString()}->{containerMount.GetProperty("Destination").GetString()}")
                .Order()
                .ToList());
    }

    public async Task<string> ReadContainerProcessUserIdAsync(string serviceName) =>
        (await RunComposeCommandAsync(TimeSpan.FromSeconds(30), "exec", "-T", serviceName, "id", "-u")).Trim();

    public async Task<string> ReadContainerEnvironmentVariableAsync(string serviceName, string variableName) =>
        (await RunComposeCommandAsync(TimeSpan.FromSeconds(30), "exec", "-T", serviceName, "printenv", variableName)).Trim();

    public async Task<JsonDocument> ReadResolvedComposeConfigAsync() =>
        JsonDocument.Parse(await RunComposeCommandAsync(TimeSpan.FromSeconds(30), "config", "--format", "json"));

    // Conta os Logon de resposta do acceptor no log do OrderGenerator. Contamos em vez de
    // filtrar por hora porque o relógio do Docker pode não bater com o da máquina.
    public async Task<int> CountFixLogonsFromAcceptorAsync() =>
        FixLog.ParseFixMessages(await ReadServiceLogAsync("ordergenerator"))
            .Count(fixMessage => fixMessage.ReadFixTagValue(35) == "A" && fixMessage.ReadFixTagValue(49) == "ORDERACCUMULATOR");

    public async Task WaitForNewFixLogonAsync(int logonsAlreadySeen)
    {
        var fixLogonDeadline = DateTime.UtcNow + AppStartTimeout;
        while (DateTime.UtcNow < fixLogonDeadline)
        {
            if (await CountFixLogonsFromAcceptorAsync() > logonsAlreadySeen) return;
            await Task.Delay(500);
        }
        Assert.Fail($"o OrderGenerator não recebeu logon novo do OrderAccumulator em {AppStartTimeout}");
    }

    // The OrderAccumulator has no healthcheck, so `up --force-recreate --wait` only waits for the new container to run.
    // Ready means the new container logged the OrderGenerator logon and its HTTP answers through the OrderGenerator.
    // The bound is the same as the first start of the compose (AppStartTimeout). Nothing is retried once ready: a
    // container that restarted fails here, with the logs of both services, instead of breaking the next test.
    public async Task WaitForRecreatedOrderAccumulatorReadyAsync()
    {
        var orderAccumulatorReadyDeadline = DateTime.UtcNow + AppStartTimeout;
        while (DateTime.UtcNow < orderAccumulatorReadyDeadline)
        {
            if (await IsOrderGeneratorLogonLoggedByOrderAccumulatorAsync() && await IsOrderAccumulatorAnsweringThroughOrderGeneratorAsync())
            {
                var orderAccumulatorRestartCount = await ReadOrderAccumulatorRestartCountAsync();
                if (orderAccumulatorRestartCount != 0)
                    Assert.Fail(await DescribeOrderAccumulatorStateAsync($"o OrderAccumulator recriado reiniciou {orderAccumulatorRestartCount} vez(es)"));
                return;
            }
            await Task.Delay(500);
        }
        Assert.Fail(await DescribeOrderAccumulatorStateAsync($"o OrderAccumulator recriado não ficou pronto em {AppStartTimeout}"));
    }

    private async Task<bool> IsOrderGeneratorLogonLoggedByOrderAccumulatorAsync() =>
        FixLog.ParseFixMessages(await ReadServiceStdoutAsync("orderaccumulator"))
            .Any(fixMessage => fixMessage.ReadFixTagValue(35) == "A" && fixMessage.ReadFixTagValue(49) == "ORDERGENERATOR");

    private async Task<bool> IsOrderAccumulatorAnsweringThroughOrderGeneratorAsync()
    {
        using var exposuresResponse = await OrderGeneratorHttp.GetAsync("/api/exposures");
        return exposuresResponse.StatusCode == System.Net.HttpStatusCode.OK;
    }

    private async Task<int> ReadOrderAccumulatorRestartCountAsync()
    {
        var orderAccumulatorContainerId = (await RunComposeCommandAsync(TimeSpan.FromSeconds(30), "ps", "-q", "orderaccumulator")).Trim();
        return int.Parse((await CaptureComposeTestCommandOutputAsync("docker", TimeSpan.FromSeconds(30), "inspect", "-f", "{{.RestartCount}}", orderAccumulatorContainerId)).Trim());
    }

    private async Task<string> DescribeOrderAccumulatorStateAsync(string readinessFailure)
    {
        var orderAccumulatorLogTail = await RunComposeCommandAsync(TimeSpan.FromSeconds(30), "logs", "--no-color", "--tail", "40", "orderaccumulator");
        var orderGeneratorLogTail = await RunComposeCommandAsync(TimeSpan.FromSeconds(30), "logs", "--no-color", "--tail", "40", "ordergenerator");
        return $"{readinessFailure} (RestartCount {await ReadOrderAccumulatorRestartCountAsync()}).\n--- orderaccumulator ---\n{orderAccumulatorLogTail}\n--- ordergenerator ---\n{orderGeneratorLogTail}";
    }

    public Task<string> RunComposeCommandAsync(TimeSpan commandTimeout, params string[] composeArguments) =>
        CaptureComposeTestCommandOutputAsync("docker", commandTimeout,
            ["compose", "-p", ComposeProjectName, "-f", Path.Combine(RepoRoot, "docker-compose.yml"), .. composeArguments]);

    private async Task WaitForOrderGeneratorHealthAsync()
    {
        var healthCheckDeadline = DateTime.UtcNow + AppStartTimeout;
        while (DateTime.UtcNow < healthCheckDeadline)
        {
            try
            {
                using var orderGeneratorHealthResponse = await OrderGeneratorHttp.GetAsync("/health");
                if (orderGeneratorHealthResponse.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        Assert.Fail($"/health do OrderGenerator não respondeu em {AppStartTimeout}");
    }

    private Task<string> CaptureComposeTestCommandOutputAsync(string commandExecutable, TimeSpan commandTimeout, params string[] commandArguments)
    {
        var composeEnvironmentVariables = new Dictionary<string, string?> { ["FLOWA_HTTP_PORT"] = OrderGeneratorHostPort.ToString() };
        if (sourceRevisionId is not null)
            composeEnvironmentVariables["SOURCE_REVISION_ID"] = sourceRevisionId;
        return ExternalCommand.CaptureExternalCommandOutputAsync(commandExecutable, commandTimeout, composeEnvironmentVariables, commandArguments);
    }
}

public static class ExternalCommand
{
    // Variável com valor nulo é removida do ambiente do processo filho.
    public static async Task<string> CaptureExternalCommandOutputAsync(
        string commandExecutable,
        TimeSpan commandTimeout,
        IReadOnlyDictionary<string, string?> environmentVariables,
        params string[] commandArguments)
    {
        var commandStartInfo = new ProcessStartInfo(commandExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var commandArgument in commandArguments) commandStartInfo.ArgumentList.Add(commandArgument);
        foreach (var (variableName, variableValue) in environmentVariables)
        {
            if (variableValue is null) commandStartInfo.Environment.Remove(variableName);
            else commandStartInfo.Environment[variableName] = variableValue;
        }

        var commandLine = $"{commandExecutable} {string.Join(' ', commandArguments)}";
        using var commandProcess = Process.Start(commandStartInfo)!;
        var commandStandardOutput = commandProcess.StandardOutput.ReadToEndAsync();
        var commandStandardError = commandProcess.StandardError.ReadToEndAsync();
        using var commandTimeoutSource = new CancellationTokenSource(commandTimeout);
        try
        {
            await commandProcess.WaitForExitAsync(commandTimeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            commandProcess.Kill(entireProcessTree: true);
            throw new TimeoutException($"{commandLine} passou de {commandTimeout}");
        }

        if (commandProcess.ExitCode != 0)
            throw new InvalidOperationException($"{commandLine} saiu com {commandProcess.ExitCode}: {await commandStandardError}");
        return await commandStandardOutput;
    }
}

public sealed record ServiceContainerState(
    string ContainerId,
    string ContainerStatus,
    string ContainerStartedAt,
    string ContainerEntrypoint,
    IReadOnlyList<string> PublishedPortBindings,
    IReadOnlyList<string> VolumeMounts);

public static class RepoPaths
{
    public static string FindRepoRoot()
    {
        for (var repoRootCandidateDirectory = new DirectoryInfo(AppContext.BaseDirectory); repoRootCandidateDirectory is not null; repoRootCandidateDirectory = repoRootCandidateDirectory.Parent)
            if (File.Exists(Path.Combine(repoRootCandidateDirectory.FullName, "docker-compose.yml")) && File.Exists(Path.Combine(repoRootCandidateDirectory.FullName, "Flowa.slnx")))
                return repoRootCandidateDirectory.FullName;
        throw new InvalidOperationException("não achei a raiz do repositório (docker-compose.yml + Flowa.slnx)");
    }
}

// Mensagem FIX crua como o QuickFIX/n escreve no stdout. O separador é SOH (0x01);
// aceitamos também "|" para o caso de o log já vir trocado.
public sealed record FixMessage(string RawFixText, IReadOnlyDictionary<int, string> ValuesByFixTag)
{
    public string? ReadFixTagValue(int fixTag) => ValuesByFixTag.TryGetValue(fixTag, out var fixTagValue) ? fixTagValue : null;
}

public static partial class FixLog
{
    [GeneratedRegex(@"8=FIX\.4\.4[\u0001|].*?[\u0001|]10=\d{3}[\u0001|]?")]
    private static partial Regex FixMessagePattern();

    public static IReadOnlyList<FixMessage> ParseFixMessages(string serviceLog) =>
        FixMessagePattern().Matches(serviceLog).Select(fixMessageMatch => ParseFixMessage(fixMessageMatch.Value)).ToList();

    private static FixMessage ParseFixMessage(string rawFixText)
    {
        var valuesByFixTag = new Dictionary<int, string>();
        foreach (var fixTagAndValue in rawFixText.Split(['\u0001', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tagValueSeparatorIndex = fixTagAndValue.IndexOf('=');
            if (tagValueSeparatorIndex > 0 && int.TryParse(fixTagAndValue[..tagValueSeparatorIndex], out var fixTag))
                valuesByFixTag.TryAdd(fixTag, fixTagAndValue[(tagValueSeparatorIndex + 1)..]);
        }
        return new FixMessage(rawFixText.Replace('\u0001', '|'), valuesByFixTag);
    }
}

[CollectionDefinition(CollectionName)]
public sealed class ComposeCollection : ICollectionFixture<ComposeFixture>
{
    public const string CollectionName = "compose";
}
