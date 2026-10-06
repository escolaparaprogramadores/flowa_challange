using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Flowa.OrderAccumulator.Domain.Orders.Entities;
using NetArchTest.Rules;

namespace Flowa.OrderAccumulator.Tests;

// Each layer is a folder and a namespace inside one .csproj, so the compiler does not stop a wrong
// dependency: this test does, written as the list of what MAY come in (dotnet-clean-architecture.md).
public sealed class LayerDependencyTests
{
    private const string RootNamespace = "Flowa.OrderAccumulator";
    private const string ProjectFolderName = "flowa.orderaccumulator-worker-ecs";
    private const string BaseClassLibraryNamespace = "System";
    private static readonly string[] CommonsTechnicalLibraryNamespaces = ["Microsoft.Extensions", "Dapper", "Npgsql", "StatsdClient"];

    private static readonly Assembly OrderAccumulatorAssembly = typeof(Order).Assembly;
    private static readonly string[] LayerNames = ["Entrypoint", "Application", "Domain", "Infrastructure", "Commons"];

    [Fact]
    public void Domain_depends_only_on_the_base_class_library_the_commons_shared_kernel_and_itself()
    {
        AssertLayerOnlyDependsOn(
            "Domain", BaseClassLibraryNamespace, LayerNamespace("Commons.Entities"), LayerNamespace("Commons.Exceptions"), LayerNamespace("Commons.ValueObjects"),
            LayerNamespace("Domain"));
    }

    [Fact]
    public void Commons_and_each_commons_folder_depend_only_on_the_base_class_library_their_technical_library_and_themselves()
    {
        AssertLayerOnlyDependsOn("Commons", [BaseClassLibraryNamespace, .. CommonsTechnicalLibraryNamespaces, LayerNamespace("Commons")]);
        AssertLayerOnlyDependsOn("Commons.Entities", BaseClassLibraryNamespace, LayerNamespace("Commons.Entities"));
        AssertLayerOnlyDependsOn("Commons.Responses", BaseClassLibraryNamespace, LayerNamespace("Commons.Responses"));
        AssertLayerOnlyDependsOn("Commons.Database", BaseClassLibraryNamespace, "Dapper", "Npgsql", LayerNamespace("Commons.Database"));
        AssertLayerOnlyDependsOn("Commons.Observability", BaseClassLibraryNamespace, "StatsdClient", LayerNamespace("Commons.Observability"));
        AssertLayerOnlyDependsOn("Commons.Logging", BaseClassLibraryNamespace, "Microsoft.Extensions", LayerNamespace("Commons.Logging"));
        AssertLayerOnlyDependsOn(
            "Commons.DependencyInjection", BaseClassLibraryNamespace, "Microsoft.Extensions", LayerNamespace("Commons.Database"), LayerNamespace("Commons.Logging"),
            LayerNamespace("Commons.Observability"),
            LayerNamespace("Commons.DependencyInjection"));
    }

    [Fact]
    public void Application_depends_only_on_the_base_class_library_domain_commons_and_itself()
    {
        AssertLayerOnlyDependsOn(
            "Application", BaseClassLibraryNamespace, LayerNamespace("Domain"), LayerNamespace("Commons"), LayerNamespace("Application"));
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
            .HaveDependencyOnAny(LayerNamespace("Commons.Database.IDatabase"), LayerNamespace("Commons.Database.DapperDatabase")).GetResult();

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

        Assert.Empty(typesOutsideTheLayers);
        Assert.All(LayerNames, layerName => Assert.NotEmpty(TypesOfLayer(layerName).GetTypes()));
    }

    [Fact]
    public void Every_source_file_namespace_matches_its_folder()
    {
        var projectFolder = Path.Combine(FindRepositoryRoot(), "src", ProjectFolderName);
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

            var expectedNamespace = string.Join('.', [RootNamespace, .. relativeFolder.Split(Path.DirectorySeparatorChar)]);
            Assert.True(expectedNamespace == declaredNamespace, $"{sourceFile}: expected {expectedNamespace}, declared {declaredNamespace}");
        }
    }

    // The Datadog client (StatsdClient) is wrapped by the Infrastructure adapter behind IOrderMetricsPort;
    // the Entrypoint never talks to it directly (reviewer r1, F-01).
    [Fact]
    public void Entrypoint_does_not_use_the_datadog_client_directly()
    {
        var entrypointTypes = TypesOfLayer("Entrypoint");
        var entrypointDatadogResult = entrypointTypes.ShouldNot().HaveDependencyOn("StatsdClient").GetResult();

        Assert.NotEmpty(entrypointTypes.GetTypes());
        Assert.True(entrypointDatadogResult.IsSuccessful, DescribeFailingTypes(entrypointDatadogResult));
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
        var typesUsingTheLoggingSdk = Types.InAssembly(OrderAccumulatorAssembly).That().HaveDependencyOn("Microsoft.Extensions.Logging").GetTypes()
            // The generated Program (and its closures) is the composition root: it only calls AddApplicationLogging.
            .Where(declaredType => declaredType.Namespace is not null)
            .ToList();

        Assert.Contains(typesUsingTheLoggingSdk, declaredType => declaredType.Namespace == LayerNamespace("Commons.Logging"));
        Assert.All(typesUsingTheLoggingSdk, declaredType => Assert.Equal(LayerNamespace("Commons.Logging"), declaredType.Namespace));
    }

    private static void AssertLayerOnlyDependsOn(string layerName, params string[] allowedNamespaces)
    {
        var layerTypes = TypesOfLayer(layerName);
        var layerDependencyResult = layerTypes.Should().OnlyHaveDependenciesOn(allowedNamespaces).GetResult();

        Assert.NotEmpty(layerTypes.GetTypes());
        Assert.True(layerDependencyResult.IsSuccessful, DescribeFailingTypes(layerDependencyResult));
    }

    private static PredicateList TypesOfLayer(string layerName) =>
        Types.InAssembly(OrderAccumulatorAssembly).That().ResideInNamespace(LayerNamespace(layerName));

    private static string LayerNamespace(string layerName) => $"{RootNamespace}.{layerName}";

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
