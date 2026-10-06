using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Flowa.OrderGenerator.Domain.Orders.ValueObjects;
using NetArchTest.Rules;

namespace Flowa.OrderGenerator.Tests;

// Each layer is a folder and a namespace inside one .csproj, so the compiler does not stop a wrong
// dependency: this test does, written as the list of what MAY come in (dotnet-clean-architecture.md).
public sealed class LayerDependencyTests
{
    private const string RootNamespace = "Flowa.OrderGenerator";
    private const string ProjectFolderName = "flowa.ordergenerator-webapi-ecs";
    private const string BaseClassLibraryNamespace = "System";

    private static readonly Assembly OrderGeneratorAssembly = typeof(SentOrderResult).Assembly;
    private static readonly string[] LayerNames = ["Entrypoint", "Application", "Domain", "Infrastructure", "Commons"];

    // The Domain may also use the shared core of the Commons; the Commons may use the technical libraries
    // (Microsoft.Extensions here: HTTP client and logging).
    private const string MicrosoftExtensionsNamespace = "Microsoft.Extensions";
    private static readonly string[] DomainAllowedNamespaces =
    [
        BaseClassLibraryNamespace, LayerNamespace("Domain"),
        LayerNamespace("Commons.Entities"), LayerNamespace("Commons.Exceptions"), LayerNamespace("Commons.ValueObjects")
    ];

    [Fact]
    public void Domain_depends_only_on_the_base_class_library_the_commons_shared_core_and_itself()
    {
        AssertLayerOnlyDependsOn("Domain", DomainAllowedNamespaces);
    }

    [Fact]
    public void Commons_depends_only_on_the_base_class_library_microsoft_extensions_and_itself()
    {
        AssertLayerOnlyDependsOn("Commons", BaseClassLibraryNamespace, MicrosoftExtensionsNamespace, LayerNamespace("Commons"));
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
        var typesUsingTheLoggingSdk = Types.InAssembly(OrderGeneratorAssembly).That().HaveDependencyOn("Microsoft.Extensions.Logging").GetTypes()
            // The generated Program (and its closures) is the composition root: it only calls AddJsonLogsWithTraceId.
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
        Types.InAssembly(OrderGeneratorAssembly).That().ResideInNamespace(LayerNamespace(layerName));

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
