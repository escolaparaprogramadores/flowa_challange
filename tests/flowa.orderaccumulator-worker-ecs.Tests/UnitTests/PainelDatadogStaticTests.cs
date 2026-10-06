using System.Text.RegularExpressions;
using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Flowa.OrderAccumulator.Tests;

// O painel e a esteira dele só rodam no GitHub, depois do merge. Estes testes leem os arquivos e
// barram antes disso o que quebraria a regra: pull_request com as chaves, state no lugar errado,
// painel apagável, chave ou endereço da org no repositório público.
public sealed class PainelDatadogStaticTests
{
    private const string ExposureMetric = "flowa.exposicao";
    private const string AcceptedOrdersMetric = "flowa.ordens.aceitas";
    private const string RejectedOrdersMetric = "flowa.ordens.rejeitadas";

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PainelWorkflow = ReadText(Path.Combine(RepoRoot, ".github", "workflows", "2-develop-painel-datadog.yml"));
    private static readonly string DeployWorkflow = ReadText(Path.Combine(RepoRoot, ".github", "workflows", "2-develop.yml"));
    private static readonly string PainelDirectory = Path.Combine(RepoRoot, "observability", "datadog");
    private static readonly string MainTf = ReadText(Path.Combine(PainelDirectory, "main.tf"));
    private static readonly string PainelTf = ReadText(Path.Combine(PainelDirectory, "painel.tf"));
    private static readonly string LockFile = ReadText(Path.Combine(PainelDirectory, ".terraform.lock.hcl"));

    [Fact]
    public void Workflow_runs_on_push_to_develop_filtered_by_the_painel_paths_and_by_hand()
    {
        var onBlock = Regex.Match(PainelWorkflow, @"^on:\n(?<bloco>(?:[ #].*\n|\n)*?)(?=^\S)", RegexOptions.Multiline).Groups["bloco"].Value;
        var triggerKeys = Regex.Matches(onBlock, @"^  (?<gatilho>[a-z_]+):", RegexOptions.Multiline).Select(gatilho => gatilho.Groups["gatilho"].Value);

        Assert.Equal(["push", "workflow_dispatch"], triggerKeys);
        Assert.Contains("    branches: [develop]\n", onBlock);
        var pathFilters = Regex.Matches(onBlock, @"^      - ""(?<caminho>[^""]+)""", RegexOptions.Multiline).Select(caminho => caminho.Groups["caminho"].Value);
        Assert.Equal(["observability/datadog/**", ".github/workflows/2-develop-painel-datadog.yml"], pathFilters);
    }

    [Fact]
    public void Workflow_never_reacts_to_pull_request()
    {
        Assert.DoesNotMatch(new Regex(@"^\s*-?\s*pull_request(_target)?\s*:", RegexOptions.Multiline), PainelWorkflow);
    }

    [Fact]
    public void Workflow_has_its_own_concurrency_that_never_cancels_a_running_apply()
    {
        Assert.Contains("\nconcurrency:\n  group: painel-datadog-dev\n  cancel-in-progress: false\n", PainelWorkflow);
    }

    [Fact]
    public void Workflow_has_a_single_job_guarded_by_develop_in_the_dev_environment_with_the_keys_only_in_the_apply_step()
    {
        var jobsBlock = PainelWorkflow[PainelWorkflow.IndexOf("\njobs:\n", StringComparison.Ordinal)..];
        var jobNames = Regex.Matches(jobsBlock, @"^  (?<job>[A-Za-z0-9_-]+):", RegexOptions.Multiline).Select(job => job.Groups["job"].Value);
        var applyStep = jobsBlock[jobsBlock.IndexOf("      - name: terraform plan e apply\n", StringComparison.Ordinal)..];

        Assert.Equal(["painel"], jobNames);
        Assert.Contains(
            "\n  painel:\n    # O disparo manual também só aplica o que está em develop.\n    if: github.ref == 'refs/heads/develop'\n    runs-on: ubuntu-24.04\n    environment: dev\n",
            jobsBlock);
        Assert.Contains("        env:\n          DD_API_KEY: ${{ secrets.DATADOG_API_KEY }}\n          DD_APP_KEY: ${{ secrets.DATADOG_APP_KEY }}\n", applyStep);
        Assert.Single(Regex.Matches(PainelWorkflow, @"secrets\.DATADOG_API_KEY"));
        Assert.Single(Regex.Matches(PainelWorkflow, @"secrets\.DATADOG_APP_KEY"));
    }

    [Fact]
    public void Workflow_keeps_the_painel_state_in_its_own_key_with_lock()
    {
        Assert.Contains("working-directory: observability/datadog", PainelWorkflow);
        Assert.Contains(@"-backend-config=""bucket=$TF_STATE_BUCKET""", PainelWorkflow);
        Assert.Contains(@"-backend-config=""key=flowa/datadog-dev.tfstate""", PainelWorkflow);
        Assert.Contains(@"-backend-config=""use_lockfile=true""", PainelWorkflow);
        Assert.DoesNotContain("key=flowa/dev.tfstate", PainelWorkflow);
    }

    [Fact]
    public void Workflow_runs_every_terraform_command_through_terraform_quieto_without_the_wrapper()
    {
        Assert.Contains("terraform_wrapper: false", PainelWorkflow);
        Assert.Contains("terraform_quieto init", PainelWorkflow);
        Assert.Contains("terraform_quieto plan -out=tfplan", PainelWorkflow);
        Assert.Contains("terraform_quieto apply tfplan", PainelWorkflow);
        Assert.DoesNotMatch(new Regex(@"^\s*terraform (init|plan|apply|output|show)\b", RegexOptions.Multiline), PainelWorkflow);
        Assert.Contains(QuietTerraformFunction(DeployWorkflow), PainelWorkflow);
    }

    [Fact]
    public void Workflow_pins_the_same_actions_by_sha_as_the_deploy_workflow()
    {
        var painelActions = Regex.Matches(PainelWorkflow, @"uses: (?<acao>\S+)").Select(acao => acao.Groups["acao"].Value).ToList();
        var deployActions = Regex.Matches(DeployWorkflow, @"uses: (?<acao>\S+)").Select(acao => acao.Groups["acao"].Value).ToHashSet();

        Assert.Equal(
            ["actions/checkout", "aws-actions/configure-aws-credentials", "hashicorp/setup-terraform"],
            painelActions.Select(acao => acao.Split('@')[0]));
        Assert.All(painelActions, acao => Assert.Matches(@"@[0-9a-f]{40}$", acao));
        Assert.All(painelActions, acao => Assert.Contains(acao, deployActions));
    }

    [Fact]
    public void Dashboard_cannot_be_destroyed_by_terraform()
    {
        var dashboardBlock = Regex.Match(PainelTf, @"^resource ""datadog_dashboard"" ""ordens_e_exposicao"" \{\n(?<corpo>.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline).Groups["corpo"].Value;

        Assert.Contains("  lifecycle {\n    prevent_destroy = true\n  }\n", dashboardBlock);
        Assert.Single(Regex.Matches(PainelTf, @"^resource """, RegexOptions.Multiline));
    }

    [Fact]
    public void Dashboard_reads_the_three_contract_metrics_only_from_the_datadog_metrics_worker()
    {
        var businessMetricQueries = Regex.Matches(PainelTf, @"query\s+= ""(?<consulta>[a-z0-9]+:flowa\.[a-z.]+\{[^""]*)""")
            .Select(consulta => consulta.Groups["consulta"].Value)
            .ToList();

        Assert.Contains("  filtro_do_worker_de_metricas = \"service:datadog-metrics\"\n", PainelTf);
        Assert.Equal(10, businessMetricQueries.Count(consulta => consulta.Contains($"{ExposureMetric}{{", StringComparison.Ordinal)));
        Assert.Equal(13, businessMetricQueries.Count(consulta => consulta.Contains($"{AcceptedOrdersMetric}{{", StringComparison.Ordinal)));
        Assert.Equal(11, businessMetricQueries.Count(consulta => consulta.Contains($"{RejectedOrdersMetric}{{", StringComparison.Ordinal)));
        Assert.All(businessMetricQueries, consulta => Assert.Contains("{$env,${local.filtro_do_worker_de_metricas}", consulta));
        Assert.DoesNotContain("service:order-accumulator", PainelTf);
    }

    [Fact]
    public void Dashboard_reads_traces_and_runtime_through_the_service_variable_that_defaults_to_the_order_accumulator()
    {
        var serviceVariableDefault = Regex.Match(PainelTf, @"  template_variable \{\n    default = ""(?<padrao>[^""]+)""\n    name    = ""service""\n").Groups["padrao"].Value;
        var traceAndRuntimeQueries = Regex.Matches(PainelTf, @"query\s+= ""(?<consulta>[a-z0-9]+:(trace|runtime)\.[a-z._]+\{[^""]*)""")
            .Select(consulta => consulta.Groups["consulta"].Value)
            .ToList();

        Assert.Equal("order-accumulator", serviceVariableDefault);
        Assert.Equal(8, traceAndRuntimeQueries.Count);
        Assert.All(traceAndRuntimeQueries, consulta => Assert.Contains("{$env,$service}", consulta));
    }

    [Fact]
    public void Dashboard_keeps_every_titled_widget_of_the_live_panel_in_order()
    {
        var widgetTitles = Regex.Matches(PainelTf, @"^\s{6,}title\s+= ""(?<titulo>[^""]+)""", RegexOptions.Multiline)
            .Select(titulo => titulo.Groups["titulo"].Value);

        Assert.Equal(
            [
                "Visão geral", "Total de ordens", "Aceitas", "Rejeitadas", "Taxa de aceite", "Ritmo de ordens",
                "Latência de registro p95", "Maior exposição comprada", "Maior exposição vendida", "Exposição líquida total",
                "Pico de uso do limite", "Painel de risco por símbolo", "Mix de ordens · símbolo › lado",
                "Exposição e limites", "Exposição por símbolo", "Exposição agora", "Uso do limite por símbolo (%)",
                "Folga até o limite (menor = mais risco)",
                "Fluxo de ordens", "Ordens aceitas x rejeitadas", "Taxa de aceite (%)", "Ordens por símbolo",
                "Ordens por lado (compra x venda)", "Rejeições por símbolo e lado", "Volume por símbolo vs. 1 hora antes",
                "Resumo por símbolo e lado",
                "Saúde do processamento", "Recebimento FIX · p50 / p95 / p99 (ms)", "Postgres · p95 por query (ms)",
                "Erros no processamento",
            ],
            widgetTitles);
        Assert.Contains("  layout_type = \"ordered\"\n", PainelTf);
    }

    [Fact]
    public void Exposure_chart_marks_both_limits_of_the_exposure_rule()
    {
        var exposureChart = ReadChartDefinitionTitled("Exposição por símbolo");
        var exposureLimit = ExposureLimitPolicy.PerSymbol.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains($"value        = \"y = {exposureLimit}\"\n", exposureChart);
        Assert.Contains($"value        = \"y = -{exposureLimit}\"\n", exposureChart);
    }

    [Theory]
    [InlineData("Taxa de aceite")]
    [InlineData("Taxa de aceite (%)")]
    public void Acceptance_rate_chart_divides_accepted_orders_by_accepted_plus_rejected_orders(string acceptanceRateChartTitle)
    {
        var acceptanceRateChart = ReadChartDefinitionTitled(acceptanceRateChartTitle);

        Assert.Contains("formula_expression = \"100 * default_zero(a) / (default_zero(a) + default_zero(b))\"\n", acceptanceRateChart);
        Assert.Matches(@"name\s+= ""a""\n\s+query\s+= ""sum:flowa\.ordens\.aceitas\{", acceptanceRateChart);
        Assert.Matches(@"name\s+= ""b""\n\s+query\s+= ""sum:flowa\.ordens\.rejeitadas\{", acceptanceRateChart);
    }

    private static string ReadChartDefinitionTitled(string chartTitle) =>
        Regex.Match(PainelTf, $@"title\s+= ""{Regex.Escape(chartTitle)}""\n(?<grafico>.*?)\n      widget \{{\n", RegexOptions.Singleline).Groups["grafico"].Value;

    [Fact]
    public void Every_output_of_the_painel_is_sensitive()
    {
        var outputBlocks = Regex.Matches(PainelTf + MainTf, @"^output ""[^""]+"" \{\n(?<corpo>.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline);

        Assert.NotEmpty(outputBlocks);
        Assert.All(outputBlocks, outputBlock => Assert.Contains("  sensitive   = true\n", outputBlock.Groups["corpo"].Value));
    }

    [Fact]
    public void Provider_is_pinned_and_the_lock_file_matches_it()
    {
        Assert.Contains("      source  = \"DataDog/datadog\"\n      version = \"4.24.0\"\n", MainTf);
        Assert.Contains("provider \"registry.terraform.io/datadog/datadog\" {\n  version     = \"4.24.0\"\n", LockFile);
        Assert.Contains("  backend \"s3\" {}\n", MainTf);
        Assert.Contains("provider \"datadog\" {}\n", MainTf);
    }

    [Fact]
    public void Painel_files_carry_no_datadog_key_site_or_org_address()
    {
        var painelFiles = Directory.GetFiles(PainelDirectory, "*.tf").Select(ReadText).Append(PainelWorkflow).ToList();

        Assert.All(painelFiles, painelFile =>
        {
            // Atributo do provider com a chave ou o site escrito no código; DD_API_KEY do ambiente não conta.
            Assert.DoesNotMatch(new Regex(@"\b(api_key|app_key|api_url|validate)\s*="), painelFile);
            Assert.DoesNotMatch(new Regex(@"datadoghq\.|ddog-gov\.|https?://", RegexOptions.IgnoreCase), painelFile);
            // Chave de API do Datadog tem 32 hex e a de app, 40; o SHA das actions é o único hex longo aceito.
            var linesWithoutActions = string.Join('\n', painelFile.Split('\n').Where(linha => !linha.TrimStart().StartsWith("- uses:") && !linha.TrimStart().StartsWith("uses:")));
            Assert.DoesNotMatch(new Regex(@"\b[0-9a-f]{32,}\b", RegexOptions.IgnoreCase), linesWithoutActions);
        });
    }

    // O checkout no Windows pode trazer CRLF; as assertivas comparam linhas terminadas em \n.
    private static string ReadText(string filePath) => File.ReadAllText(filePath).ReplaceLineEndings("\n");

    private static string QuietTerraformFunction(string workflowText) =>
        Regex.Match(workflowText, @"terraform_quieto\(\) \{\n.*?\n          \}\n", RegexOptions.Singleline).Value is { Length: > 0 } quietFunction
            ? quietFunction
            : throw new InvalidOperationException("2-develop.yml sem a função terraform_quieto");

    private static string FindRepoRoot()
    {
        for (var candidateDirectory = new DirectoryInfo(AppContext.BaseDirectory); candidateDirectory is not null; candidateDirectory = candidateDirectory.Parent)
            if (File.Exists(Path.Combine(candidateDirectory.FullName, "Flowa.slnx")))
                return candidateDirectory.FullName;
        throw new InvalidOperationException("não achei a raiz do repositório (Flowa.slnx)");
    }
}
