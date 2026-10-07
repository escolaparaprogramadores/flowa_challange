using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Flowa.DatadogMetrics.Domain.Exposures.ValueObjects;
using NetArchTest.Rules;

namespace Flowa.DatadogMetrics.Tests;

// Each layer is a folder and a namespace inside one .csproj, so the compiler does not stop a wrong
// dependency: this test does, written as the list of what MAY come in (dotnet-clean-architecture.md).
public sealed class LayerDependencyTests
{
    private const string RootNamespace = "Flowa.DatadogMetrics";
    private const string ProjectFolderName = "flowa.datadog-metrics-worker-ecs";
    private const string CommonsRootNamespace = "Flowa.Commons";
    private const string BaseClassLibraryNamespace = "System";

    private static readonly Assembly DatadogMetricsAssembly = typeof(SymbolExposure).Assembly;
    private static readonly string[] LayerNames = ["Entrypoint", "Application", "Domain", "Infrastructure"];

    [Fact]
    public void Domain_depends_only_on_the_base_class_library_the_commons_shared_kernel_and_itself()
    {
        AssertLayerOnlyDependsOn(
            "Domain", BaseClassLibraryNamespace, CommonsNamespace("Entities"), CommonsNamespace("Exceptions"), CommonsNamespace("ValueObjects"),
            LayerNamespace("Domain"));
    }

    [Fact]
    public void Application_depends_only_on_the_base_class_library_domain_commons_and_itself()
    {
        AssertLayerOnlyDependsOn(
            "Application", BaseClassLibraryNamespace, LayerNamespace("Domain"), CommonsRootNamespace, LayerNamespace("Application"));
    }

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
    public void Infrastructure_does_not_depend_on_the_entrypoint()
    {
        var infrastructureTypes = TypesOfLayer("Infrastructure");
        var infrastructureDependencyResult = infrastructureTypes.ShouldNot().HaveDependencyOn(LayerNamespace("Entrypoint")).GetResult();

        Assert.NotEmpty(infrastructureTypes.GetTypes());
        Assert.True(infrastructureDependencyResult.IsSuccessful, DescribeFailingTypes(infrastructureDependencyResult));
    }

    [Fact]
    public void No_layer_uses_a_technical_library_directly()
    {
        var workerTypes = Types.InAssembly(DatadogMetricsAssembly).That().ResideInNamespace(RootNamespace);
        var workerTechnicalLibraryResult = workerTypes.ShouldNot()
            .HaveDependencyOnAny("Dapper", "Npgsql", "StatsdClient", "Polly", "Amazon").GetResult();

        Assert.NotEmpty(workerTypes.GetTypes());
        Assert.True(workerTechnicalLibraryResult.IsSuccessful, DescribeFailingTypes(workerTechnicalLibraryResult));
    }

    [Fact]
    public void Infrastructure_types_are_internal_and_sealed_or_static()
    {
        var publicInfrastructureTypes = TypesOfLayer("Infrastructure").GetTypes()
            .Where(infrastructureType => infrastructureType.DeclaringType is null && infrastructureType.IsPublic)
            .Select(infrastructureType => infrastructureType.FullName)
            .ToList();
        var openInfrastructureClasses = TypesOfLayer("Infrastructure").GetTypes()
            .Where(infrastructureType => infrastructureType.DeclaringType is null && infrastructureType.IsClass && !infrastructureType.IsSealed)
            .Select(infrastructureType => infrastructureType.FullName)
            .ToList();

        Assert.NotEmpty(TypesOfLayer("Infrastructure").GetTypes());
        Assert.Empty(publicInfrastructureTypes);
        Assert.Empty(openInfrastructureClasses);
    }

    [Fact]
    public void Only_the_commons_logging_uses_the_logging_sdk()
    {
        var workerTypesUsingTheLoggingSdk = Types.InAssembly(DatadogMetricsAssembly).That().HaveDependencyOn("Microsoft.Extensions.Logging").GetTypes()
            .Where(declaredType => declaredType.Namespace is not null)
            .Select(declaredType => declaredType.FullName)
            .ToList();

        Assert.Empty(workerTypesUsingTheLoggingSdk);
    }

    [Fact]
    public void Every_type_lives_in_one_of_the_four_layer_namespaces()
    {
        var typesOutsideTheLayers = DatadogMetricsAssembly.GetTypes()
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

    // Proves the allow list really refuses: the Domain list applied to the Infrastructure, whose repositories
    // use the Commons database, has to fail and name a repository.
    [Fact]
    public void Domain_allow_list_rejects_the_infrastructure_that_uses_the_commons_database()
    {
        var infrastructureCheckedAgainstTheDomainList = TypesOfLayer("Infrastructure").Should()
            .OnlyHaveDependenciesOn(BaseClassLibraryNamespace, LayerNamespace("Domain")).GetResult();

        Assert.False(infrastructureCheckedAgainstTheDomainList.IsSuccessful);
        Assert.Contains("Flowa.DatadogMetrics.Infrastructure.Orders.Repositories.AnsweredOrderCountReadRepository",
            infrastructureCheckedAgainstTheDomainList.FailingTypeNames ?? []);
    }

    private static void AssertLayerOnlyDependsOn(string layerName, params string[] allowedNamespaces)
    {
        var layerTypes = TypesOfLayer(layerName);
        var layerDependencyResult = layerTypes.Should().OnlyHaveDependenciesOn(allowedNamespaces).GetResult();

        Assert.NotEmpty(layerTypes.GetTypes());
        Assert.True(layerDependencyResult.IsSuccessful, DescribeFailingTypes(layerDependencyResult));
    }

    private static PredicateList TypesOfLayer(string layerName) =>
        Types.InAssembly(DatadogMetricsAssembly).That().ResideInNamespace(LayerNamespace(layerName));

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
