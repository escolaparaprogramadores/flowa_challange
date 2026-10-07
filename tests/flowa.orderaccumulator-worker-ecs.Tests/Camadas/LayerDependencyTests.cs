using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Flowa.Commons.Responses;
using Flowa.OrderAccumulator.Domain.Orders.Entities;
using NetArchTest.Rules;

namespace Flowa.OrderAccumulator.Tests;

// Each layer is a folder and a namespace inside one .csproj, so the compiler does not stop a wrong
// dependency: this test does, written as the list of what MAY come in (dotnet-clean-architecture.md).
// The Commons is its own project (src/flowa.commons), shared by the apps: the same rules apply to its assembly.
public sealed class LayerDependencyTests
{
    private const string RootNamespace = "Flowa.OrderAccumulator";
    private const string ProjectFolderName = "flowa.orderaccumulator-worker-ecs";
    private const string CommonsRootNamespace = "Flowa.Commons";
    private const string CommonsProjectFolderName = "flowa.commons";
    private const string BaseClassLibraryNamespace = "System";
    private static readonly string[] CommonsTechnicalLibraryNamespaces = ["Microsoft.Extensions", "Dapper", "Npgsql", "StatsdClient"];

    private static readonly Assembly OrderAccumulatorAssembly = typeof(Order).Assembly;
    private static readonly Assembly CommonsAssembly = typeof(DataMessage<>).Assembly;
    private static readonly string[] LayerNames = ["Entrypoint", "Application", "Domain", "Infrastructure"];

    [Fact]
    public void Domain_depends_only_on_the_base_class_library_the_commons_shared_kernel_and_itself()
    {
        AssertLayerOnlyDependsOn(
            "Domain", BaseClassLibraryNamespace, CommonsNamespace("Entities"), CommonsNamespace("Exceptions"), CommonsNamespace("ValueObjects"),
            LayerNamespace("Domain"));
    }

    [Fact]
    public void Commons_and_each_commons_folder_depend_only_on_the_base_class_library_their_technical_library_and_themselves()
    {
        AssertCommonsFolderOnlyDependsOn(CommonsRootNamespace, [BaseClassLibraryNamespace, .. CommonsTechnicalLibraryNamespaces, CommonsRootNamespace]);
        AssertCommonsFolderOnlyDependsOn(CommonsNamespace("Entities"), BaseClassLibraryNamespace, CommonsNamespace("Entities"));
        AssertCommonsFolderOnlyDependsOn(CommonsNamespace("Responses"), BaseClassLibraryNamespace, CommonsNamespace("Responses"));
        AssertCommonsFolderOnlyDependsOn(CommonsNamespace("Database"), BaseClassLibraryNamespace, "Dapper", "Npgsql", CommonsNamespace("Database"));
        AssertCommonsFolderOnlyDependsOn(CommonsNamespace("Observability"), BaseClassLibraryNamespace, "StatsdClient", CommonsNamespace("Observability"));
        AssertCommonsFolderOnlyDependsOn(CommonsNamespace("Logging"), BaseClassLibraryNamespace, "Microsoft.Extensions", CommonsNamespace("Logging"));
        AssertCommonsFolderOnlyDependsOn(
            CommonsNamespace("DependencyInjection"), BaseClassLibraryNamespace, "Microsoft.Extensions", CommonsNamespace("Database"), CommonsNamespace("Logging"),
            CommonsNamespace("Observability"),
            CommonsNamespace("DependencyInjection"));
    }

    [Fact]
    public void Application_depends_only_on_the_base_class_library_domain_commons_and_itself()
    {
        AssertLayerOnlyDependsOn(
            "Application", BaseClassLibraryNamespace, LayerNamespace("Domain"), CommonsRootNamespace, LayerNamespace("Application"));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_entrypoint()
    {
        var infrastructureTypes = TypesOfLayer("Infrastructure");
        var infrastructureDependencyResult = infrastructureTypes.ShouldNot().HaveDependencyOn(LayerNamespace("Entrypoint")).GetResult();

        Assert.NotEmpty(infrastructureTypes.GetTypes());
        Assert.True(infrastructureDependencyResult.IsSuccessful, DescribeFailingTypes(infrastructureDependencyResult));
    }

    // P-01-10 (rule 39 of the owner): the Infrastructure reaches the database and the Datadog agent only through the
    // Commons; QuickFIX/n stays as the written exception of decision 13 and is not on this list.
    [Fact]
    public void Infrastructure_does_not_use_a_technical_library_directly()
    {
        var infrastructureTypes = TypesOfLayer("Infrastructure");
        var infrastructureTechnicalLibraryResult = infrastructureTypes.ShouldNot()
            .HaveDependencyOnAny("Dapper", "Npgsql", "StatsdClient", "Polly", "Amazon").GetResult();

        Assert.NotEmpty(infrastructureTypes.GetTypes());
        Assert.True(infrastructureTechnicalLibraryResult.IsSuccessful, DescribeFailingTypes(infrastructureTechnicalLibraryResult));
    }

    // P-01-10: SQL belongs to the Infrastructure repositories; the Application orchestrates through the unit of work only.
    [Fact]
    public void Application_does_not_use_the_database_directly()
    {
        var applicationTypes = TypesOfLayer("Application");
        var applicationDatabaseResult = applicationTypes.ShouldNot()
            .HaveDependencyOnAny(CommonsNamespace("Database.IDatabase"), CommonsNamespace("Database.DapperDatabase")).GetResult();

        Assert.NotEmpty(applicationTypes.GetTypes());
        Assert.True(applicationDatabaseResult.IsSuccessful, DescribeFailingTypes(applicationDatabaseResult));
    }

    [Fact]
    public void Every_type_lives_in_one_of_the_five_layer_namespaces()
    {
        var typesOutsideTheLayers = OrderAccumulatorAssembly.GetTypes()
            .Where(declaredType => declaredType.DeclaringType is null && !declaredType.IsDefined(typeof(CompilerGeneratedAttribute)))
            .Where(declaredType => !(declaredType.Namespace is null && declaredType.Name == "Program"))
            .Where(declaredType => !LayerNames.Any(layerName =>
                declaredType.Namespace == LayerNamespace(layerName) || declaredType.Namespace?.StartsWith(LayerNamespace(layerName) + ".", StringComparison.Ordinal) == true))
            .Select(declaredType => declaredType.FullName)
            .ToList();

        var commonsTypesOutsideTheCommonsNamespace = CommonsAssembly.GetTypes()
            .Where(declaredType => declaredType.DeclaringType is null && !declaredType.IsDefined(typeof(CompilerGeneratedAttribute)))
            .Where(declaredType => declaredType.Namespace?.StartsWith(CommonsRootNamespace + ".", StringComparison.Ordinal) != true)
            .Select(declaredType => declaredType.FullName)
            .ToList();

        Assert.Empty(typesOutsideTheLayers);
        Assert.All(LayerNames, layerName => Assert.NotEmpty(TypesOfLayer(layerName).GetTypes()));
        Assert.Empty(commonsTypesOutsideTheCommonsNamespace);
        Assert.NotEmpty(TypesOfCommonsFolder(CommonsRootNamespace).GetTypes());
    }

    [Fact]
    public void Every_source_file_namespace_matches_its_folder()
    {
        AssertSourceFileNamespacesMatchTheirFolders(ProjectFolderName, RootNamespace);
        AssertSourceFileNamespacesMatchTheirFolders(CommonsProjectFolderName, CommonsRootNamespace);
    }

    // The Datadog client (StatsdClient) lives only in the Commons; the Entrypoint never talks to it directly.
    [Fact]
    public void Entrypoint_does_not_use_the_datadog_client_directly()
    {
        var entrypointTypes = TypesOfLayer("Entrypoint");
        var entrypointDatadogResult = entrypointTypes.ShouldNot().HaveDependencyOn("StatsdClient").GetResult();

        Assert.NotEmpty(entrypointTypes.GetTypes());
        Assert.True(entrypointDatadogResult.IsSuccessful, DescribeFailingTypes(entrypointDatadogResult));
    }

    [Fact]
    public void No_accumulator_type_sends_metrics_through_the_datadog_client()
    {
        var accumulatorTypes = Types.InAssembly(OrderAccumulatorAssembly);
        var metricsClientResult = accumulatorTypes.ShouldNot()
            .HaveDependencyOnAny(
                "StatsdClient",
                typeof(Flowa.Commons.Observability.IMetricsClient).FullName,
                typeof(Flowa.Commons.Observability.DogStatsdMetricsClient).FullName)
            .GetResult();

        Assert.NotEmpty(accumulatorTypes.GetTypes());
        Assert.True(metricsClientResult.IsSuccessful, DescribeFailingTypes(metricsClientResult));
    }

    // Proves the allow list really refuses: the Domain list applied to the Infrastructure, whose FIX session log
    // uses QuickFIX/n, has to fail and name the class.
    [Fact]
    public void Domain_allow_list_rejects_the_infrastructure_that_uses_an_external_library()
    {
        var infrastructureCheckedAgainstTheDomainList = TypesOfLayer("Infrastructure").Should()
            .OnlyHaveDependenciesOn(BaseClassLibraryNamespace, LayerNamespace("Domain")).GetResult();

        Assert.False(infrastructureCheckedAgainstTheDomainList.IsSuccessful);
        Assert.Contains("Flowa.OrderAccumulator.Infrastructure.Fix.FixSessionLog", infrastructureCheckedAgainstTheDomainList.FailingTypeNames ?? []);
    }

    // CA-7: the application logs only through IApplicationLogger<T> (Commons); the logging SDK of the framework
    // is used only by its implementation in Commons.Logging, never by the other layers.
    [Fact]
    public void Only_the_commons_logging_uses_the_logging_sdk()
    {
        var appTypesUsingTheLoggingSdk = Types.InAssembly(OrderAccumulatorAssembly).That().HaveDependencyOn("Microsoft.Extensions.Logging").GetTypes()
            // The generated Program (and its closures) is the composition root: it only calls AddApplicationLogging.
            .Where(declaredType => declaredType.Namespace is not null)
            .ToList();
        var commonsTypesUsingTheLoggingSdk = TypesOfCommonsFolder(CommonsRootNamespace).And().HaveDependencyOn("Microsoft.Extensions.Logging").GetTypes().ToList();

        Assert.Empty(appTypesUsingTheLoggingSdk);
        Assert.Contains(commonsTypesUsingTheLoggingSdk, declaredType => declaredType.Namespace == CommonsNamespace("Logging"));
        Assert.All(commonsTypesUsingTheLoggingSdk, declaredType => Assert.Equal(CommonsNamespace("Logging"), declaredType.Namespace));
    }

    private static void AssertLayerOnlyDependsOn(string layerName, params string[] allowedNamespaces)
    {
        var layerTypes = TypesOfLayer(layerName);
        var layerDependencyResult = layerTypes.Should().OnlyHaveDependenciesOn(allowedNamespaces).GetResult();

        Assert.NotEmpty(layerTypes.GetTypes());
        Assert.True(layerDependencyResult.IsSuccessful, DescribeFailingTypes(layerDependencyResult));
    }

    private static void AssertCommonsFolderOnlyDependsOn(string commonsFolderNamespace, params string[] allowedNamespaces)
    {
        var commonsFolderTypes = TypesOfCommonsFolder(commonsFolderNamespace);
        var commonsFolderDependencyResult = commonsFolderTypes.Should().OnlyHaveDependenciesOn(allowedNamespaces).GetResult();

        Assert.NotEmpty(commonsFolderTypes.GetTypes());
        Assert.True(commonsFolderDependencyResult.IsSuccessful, DescribeFailingTypes(commonsFolderDependencyResult));
    }

    private static void AssertSourceFileNamespacesMatchTheirFolders(string projectFolderName, string projectRootNamespace)
    {
        var projectFolder = Path.Combine(FindRepositoryRoot(), "src", projectFolderName);
        var sourceFiles = Directory.EnumerateFiles(projectFolder, "*.cs", SearchOption.AllDirectories)
            .Where(sourceFile => !IsBuildOutput(Path.GetRelativePath(projectFolder, sourceFile)))
            .ToList();

        Assert.NotEmpty(sourceFiles);
        foreach (var sourceFile in sourceFiles)
        {
            var relativeFolder = Path.GetDirectoryName(Path.GetRelativePath(projectFolder, sourceFile))!;
            var declaredNamespace = Regex.Match(File.ReadAllText(sourceFile), @"^namespace\s+([\w.]+);", RegexOptions.Multiline).Groups[1].Value;
            if (Path.GetFileName(sourceFile) == "Program.cs")
            {
                Assert.Equal("Entrypoint", relativeFolder);
                Assert.Equal(string.Empty, declaredNamespace);
                continue;
            }

            var expectedNamespace = string.Join('.', [projectRootNamespace, .. relativeFolder.Split(Path.DirectorySeparatorChar)]);
            Assert.True(expectedNamespace == declaredNamespace, $"{sourceFile}: expected {expectedNamespace}, declared {declaredNamespace}");
        }
    }

    private static PredicateList TypesOfLayer(string layerName) =>
        Types.InAssembly(OrderAccumulatorAssembly).That().ResideInNamespace(LayerNamespace(layerName));

    private static PredicateList TypesOfCommonsFolder(string commonsFolderNamespace) =>
        Types.InAssembly(CommonsAssembly).That().ResideInNamespace(commonsFolderNamespace);

    private static string LayerNamespace(string layerName) => $"{RootNamespace}.{layerName}";

    private static string CommonsNamespace(string commonsFolderName) => $"{CommonsRootNamespace}.{commonsFolderName}";

    private static string DescribeFailingTypes(TestResult layerDependencyResult) =>
        string.Join(", ", layerDependencyResult.FailingTypeNames ?? []);

    private static bool IsBuildOutput(string relativeSourcePath) =>
        relativeSourcePath.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || relativeSourcePath.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var searchedFolder = new DirectoryInfo(AppContext.BaseDirectory);
        while (searchedFolder is not null && !File.Exists(Path.Combine(searchedFolder.FullName, "Flowa.slnx")))
            searchedFolder = searchedFolder.Parent;
        return searchedFolder?.FullName ?? throw new InvalidOperationException("Flowa.slnx not found above the test output folder.");
    }
}
