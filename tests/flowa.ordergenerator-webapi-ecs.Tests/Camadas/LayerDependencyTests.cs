using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Flowa.Commons.Responses;
using Flowa.OrderGenerator.Domain.Orders.ValueObjects;
using NetArchTest.Rules;

namespace Flowa.OrderGenerator.Tests;

// Each layer is a folder and a namespace inside one .csproj, so the compiler does not stop a wrong
// dependency: this test does, written as the list of what MAY come in (dotnet-clean-architecture.md).
// The Commons is its own project (src/flowa.commons), shared by the apps: the same rules apply to its assembly.
public sealed class LayerDependencyTests
{
    private const string RootNamespace = "Flowa.OrderGenerator";
    private const string ProjectFolderName = "flowa.ordergenerator-webapi-ecs";
    private const string CommonsRootNamespace = "Flowa.Commons";
    private const string CommonsProjectFolderName = "flowa.commons";
    private const string BaseClassLibraryNamespace = "System";

    private static readonly Assembly OrderGeneratorAssembly = typeof(SentOrderResult).Assembly;
    private static readonly Assembly CommonsAssembly = typeof(DataMessage<>).Assembly;
    private static readonly string[] LayerNames = ["Entrypoint", "Application", "Domain", "Infrastructure"];

    // The Domain may also use the shared core of the Commons; the Commons may use the technical libraries
    // (Microsoft.Extensions: HTTP client and logging; Dapper and Npgsql: database; StatsdClient: Datadog agent).
    private static readonly string[] CommonsTechnicalLibraryNamespaces = ["Microsoft.Extensions", "Dapper", "Npgsql", "StatsdClient"];
    private static readonly string[] TechnicalLibraryNamespacesOnlyForTheCommons = ["Dapper", "Npgsql", "StatsdClient", "Polly", "Amazon"];
    private static readonly string[] DomainAllowedNamespaces =
    [
        BaseClassLibraryNamespace, LayerNamespace("Domain"),
        CommonsNamespace("Entities"), CommonsNamespace("Exceptions"), CommonsNamespace("ValueObjects")
    ];

    [Fact]
    public void Domain_depends_only_on_the_base_class_library_the_commons_shared_core_and_itself()
    {
        AssertLayerOnlyDependsOn("Domain", DomainAllowedNamespaces);
    }

    [Fact]
    public void Commons_depends_only_on_the_base_class_library_its_technical_libraries_and_itself()
    {
        var commonsTypes = TypesOfCommons();
        var commonsDependencyResult = commonsTypes.Should()
            .OnlyHaveDependenciesOn([BaseClassLibraryNamespace, .. CommonsTechnicalLibraryNamespaces, CommonsRootNamespace]).GetResult();

        Assert.NotEmpty(commonsTypes.GetTypes());
        Assert.True(commonsDependencyResult.IsSuccessful, DescribeFailingTypes(commonsDependencyResult));
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

    // Rule 39 of the owner: the shared Commons brings Dapper, Npgsql and StatsdClient to the OrderGenerator by
    // ProjectReference, so the compiler no longer stops them; the Infrastructure and the Entrypoint reach them only
    // through the Commons. QuickFIX/n stays as the written exception of decision 13 and is not on this list.
    [Fact]
    public void Infrastructure_does_not_use_a_technical_library_directly()
    {
        var infrastructureTypes = TypesOfLayer("Infrastructure");
        var infrastructureTechnicalLibraryResult = infrastructureTypes.ShouldNot()
            .HaveDependencyOnAny(TechnicalLibraryNamespacesOnlyForTheCommons).GetResult();

        Assert.NotEmpty(infrastructureTypes.GetTypes());
        Assert.True(infrastructureTechnicalLibraryResult.IsSuccessful, DescribeFailingTypes(infrastructureTechnicalLibraryResult));
    }

    [Fact]
    public void Entrypoint_does_not_use_a_technical_library_directly()
    {
        var entrypointTypes = TypesOfLayer("Entrypoint");
        var entrypointTechnicalLibraryResult = entrypointTypes.ShouldNot()
            .HaveDependencyOnAny(TechnicalLibraryNamespacesOnlyForTheCommons).GetResult();

        Assert.NotEmpty(entrypointTypes.GetTypes());
        Assert.True(entrypointTechnicalLibraryResult.IsSuccessful, DescribeFailingTypes(entrypointTechnicalLibraryResult));
    }

    [Fact]
    public void Every_type_lives_in_one_of_the_five_layer_namespaces()
    {
        var typesOutsideTheLayers = OrderGeneratorAssembly.GetTypes()
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
        Assert.NotEmpty(TypesOfCommons().GetTypes());
    }

    [Fact]
    public void Every_source_file_namespace_matches_its_folder()
    {
        AssertSourceFileNamespacesMatchTheirFolders(ProjectFolderName, RootNamespace);
        AssertSourceFileNamespacesMatchTheirFolders(CommonsProjectFolderName, CommonsRootNamespace);
    }

    // Proves the allow list really refuses: the Domain list applied to the Infrastructure, which uses
    // QuickFIX, has to fail and name the class.
    [Fact]
    public void Domain_allow_list_rejects_the_infrastructure_that_uses_an_external_library()
    {
        var infrastructureCheckedAgainstTheDomainList = TypesOfLayer("Infrastructure").Should()
            .OnlyHaveDependenciesOn(DomainAllowedNamespaces).GetResult();

        Assert.False(infrastructureCheckedAgainstTheDomainList.IsSuccessful);
        Assert.Contains("Flowa.OrderGenerator.Infrastructure.Fix.FixOrderClient", infrastructureCheckedAgainstTheDomainList.FailingTypeNames ?? []);
    }

    // CA-7: the application logs only through IApplicationLogger<T> (Commons); the logging SDK of the framework
    // is used only by the Commons.Logging implementation, never by the other layers.
    [Fact]
    public void Only_the_commons_logging_uses_the_logging_sdk()
    {
        var appTypesUsingTheLoggingSdk = Types.InAssembly(OrderGeneratorAssembly).That().HaveDependencyOn("Microsoft.Extensions.Logging").GetTypes()
            // The generated Program (and its closures) is the composition root: it only calls AddJsonLogsWithTraceId.
            .Where(declaredType => declaredType.Namespace is not null)
            .ToList();
        var commonsTypesUsingTheLoggingSdk = TypesOfCommons().And().HaveDependencyOn("Microsoft.Extensions.Logging").GetTypes().ToList();

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
        Types.InAssembly(OrderGeneratorAssembly).That().ResideInNamespace(LayerNamespace(layerName));

    private static PredicateList TypesOfCommons() =>
        Types.InAssembly(CommonsAssembly).That().ResideInNamespace(CommonsRootNamespace);

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
