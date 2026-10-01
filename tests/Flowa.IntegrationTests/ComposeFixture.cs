using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flowa.IntegrationTests;

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
        sourceRevisionId = (await CaptureCommandOutputAsync("git", TimeSpan.FromSeconds(30), "-C", RepoRoot, "rev-parse", "HEAD")).Trim();
        await RunComposeCommandAsync(TimeSpan.FromMinutes(2), "down", "-v", "--remove-orphans");
        await RunComposeCommandAsync(ImageBuildTimeout, "up", "-d", "--build", "--wait", "--wait-timeout", "180");
        await WaitForOrderGeneratorHealthAsync();
        await WaitForNewFixLogonAsync(logonsAlreadySeen: 0);
    }

    public async Task DisposeAsync()
    {
        // Guarda o log FIX dos dois lados para a prova antes de apagar os containers.
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

        await RunComposeCommandAsync(TimeSpan.FromMinutes(2), "down", "-v", "--remove-orphans");
        OrderGeneratorHttp.Dispose();
    }

    public Task<string> ReadServiceLogAsync(string serviceName) =>
        RunComposeCommandAsync(TimeSpan.FromSeconds(30), "logs", "--no-color", "--no-log-prefix", serviceName);

    public async Task<ServiceContainerState> InspectServiceContainerAsync(string serviceName)
    {
        var containerId = (await RunComposeCommandAsync(TimeSpan.FromSeconds(30), "ps", "-q", serviceName)).Trim();
        Assert.False(string.IsNullOrEmpty(containerId), $"o serviço {serviceName} não tem container");

        var inspectJson = await CaptureCommandOutputAsync("docker", TimeSpan.FromSeconds(30), "inspect", containerId);
        using var inspectDocument = JsonDocument.Parse(inspectJson);
        var container = inspectDocument.RootElement[0];
        var containerState = container.GetProperty("State");

        // Só as portas com ligação no host contam como publicadas; as outras ficam na rede do compose.
        var hostPortsByContainerPort = new Dictionary<string, string>();
        foreach (var exposedPort in container.GetProperty("NetworkSettings").GetProperty("Ports").EnumerateObject())
        {
            if (exposedPort.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var hostBinding in exposedPort.Value.EnumerateArray())
                hostPortsByContainerPort[exposedPort.Name] = hostBinding.GetProperty("HostPort").GetString()!;
        }

        return new ServiceContainerState(
            container.GetProperty("Id").GetString()!,
            containerState.GetProperty("Status").GetString()!,
            containerState.GetProperty("StartedAt").GetString()!,
            container.GetProperty("Config").GetProperty("User").GetString() ?? "",
            hostPortsByContainerPort);
    }

    // Conta os Logon de resposta do acceptor no log do OrderGenerator. Contamos em vez de
    // filtrar por hora porque o relógio do Docker pode não bater com o da máquina.
    public async Task<int> CountFixLogonsFromAcceptorAsync() =>
        FixLog.ParseFixMessages(await ReadServiceLogAsync("ordergenerator"))
            .Count(fixMessage => fixMessage.TagValue(35) == "A" && fixMessage.TagValue(49) == "ORDERACCUMULATOR");

    public async Task WaitForNewFixLogonAsync(int logonsAlreadySeen)
    {
        var deadline = DateTime.UtcNow + AppStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await CountFixLogonsFromAcceptorAsync() > logonsAlreadySeen) return;
            await Task.Delay(500);
        }
        Assert.Fail($"o OrderGenerator não recebeu logon novo do OrderAccumulator em {AppStartTimeout}");
    }

    public Task<string> RunComposeCommandAsync(TimeSpan timeout, params string[] composeArguments) =>
        CaptureCommandOutputAsync("docker", timeout,
            ["compose", "-p", ComposeProjectName, "-f", Path.Combine(RepoRoot, "docker-compose.yml"), .. composeArguments]);

    private async Task WaitForOrderGeneratorHealthAsync()
    {
        var deadline = DateTime.UtcNow + AppStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var healthResponse = await OrderGeneratorHttp.GetAsync("/health");
                if (healthResponse.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        Assert.Fail($"/health do OrderGenerator não respondeu em {AppStartTimeout}");
    }

    private async Task<string> CaptureCommandOutputAsync(string executable, TimeSpan timeout, params string[] commandArguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var commandArgument in commandArguments) startInfo.ArgumentList.Add(commandArgument);
        startInfo.Environment["FLOWA_HTTP_PORT"] = OrderGeneratorHostPort.ToString();
        if (sourceRevisionId is not null)
            startInfo.Environment["SOURCE_REVISION_ID"] = sourceRevisionId;

        var commandLine = $"{executable} {string.Join(' ', commandArguments)}";
        using var commandProcess = Process.Start(startInfo)!;
        var standardOutput = commandProcess.StandardOutput.ReadToEndAsync();
        var standardError = commandProcess.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await commandProcess.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            commandProcess.Kill(entireProcessTree: true);
            throw new TimeoutException($"{commandLine} passou de {timeout}");
        }

        if (commandProcess.ExitCode != 0)
            throw new InvalidOperationException($"{commandLine} saiu com {commandProcess.ExitCode}: {await standardError}");
        return await standardOutput;
    }
}

public sealed record ServiceContainerState(
    string ContainerId,
    string Status,
    string StartedAt,
    string RunAsUser,
    IReadOnlyDictionary<string, string> HostPortsByContainerPort);

public static class RepoPaths
{
    public static string FindRepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "docker-compose.yml")) && File.Exists(Path.Combine(folder.FullName, "Flowa.sln")))
                return folder.FullName;
        throw new InvalidOperationException("não achei a raiz do repositório (docker-compose.yml + Flowa.sln)");
    }
}

// Mensagem FIX crua como o QuickFIX/n escreve no stdout. O separador é SOH (0x01);
// aceitamos também "|" para o caso de o log já vir trocado.
public sealed record FixMessage(string RawText, IReadOnlyDictionary<int, string> ValuesByTag)
{
    public string? TagValue(int tag) => ValuesByTag.TryGetValue(tag, out var tagValue) ? tagValue : null;
}

public static partial class FixLog
{
    [GeneratedRegex(@"8=FIX\.4\.4[\u0001|].*?[\u0001|]10=\d{3}[\u0001|]?")]
    private static partial Regex FixMessagePattern();

    public static IReadOnlyList<FixMessage> ParseFixMessages(string serviceLog) =>
        FixMessagePattern().Matches(serviceLog).Select(match => ParseFixMessage(match.Value)).ToList();

    private static FixMessage ParseFixMessage(string rawFixText)
    {
        var valuesByTag = new Dictionary<int, string>();
        foreach (var tagAndValue in rawFixText.Split(['\u0001', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = tagAndValue.IndexOf('=');
            if (equalsIndex > 0 && int.TryParse(tagAndValue[..equalsIndex], out var tag))
                valuesByTag.TryAdd(tag, tagAndValue[(equalsIndex + 1)..]);
        }
        return new FixMessage(rawFixText.Replace('\u0001', '|'), valuesByTag);
    }
}

[CollectionDefinition(Name)]
public sealed class ComposeCollection : ICollectionFixture<ComposeFixture>
{
    public const string Name = "compose";
}
