using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Base.OrderGenerator.Domain.Orders;
using NetArchTest.Rules;

namespace Base.OrderGenerator.Tests;

// Each layer is a folder and a namespace inside one .csproj, so the compiler does not stop a wrong
// dependency: this test does, written as the list of what MAY come in (dotnet-clean-architecture.md).
// Flowa.Shared is allowed in Domain and Application until F2 deletes that project and moves the
// field rule into the OrderAccumulator Domain (spec base-inv-estrutura, ASSUMI-01).
public sealed class LayerDependencyTests
{
    private const string RootNamespace = "Base.OrderGenerator";
    private const string ProjectFolderName = "app-base-order-generator-webapi-ecs";
    private const string BaseClassLibraryNamespace = "System";
    private const string SharedOrderRulesNamespace = "Flowa.Shared";

    private static readonly Assembly OrderGeneratorAssembly = typeof(SentOrderResult).Assembly;
    private static readonly string[] LayerNames = ["Entrypoint", "Application", "Domain", "Infrastructure", "Commons"];

    [Fact]
    public void Domain_depends_only_on_the_base_class_library_and_itself()
    {
        AssertLayerOnlyDependsOn("Domain", BaseClassLibraryNamespace, LayerNamespace("Domain"), SharedOrderRulesNamespace);
    }

    [Fact]
    public void Commons_depends_only_on_the_base_class_library_and_itself()
    {
        AssertLayerOnlyDependsOn("Commons", BaseClassLibraryNamespace, LayerNamespace("Commons"));
    }

    [Fact]
    public void Application_depends_only_on_the_base_class_library_domain_commons_and_itself()
    {
        AssertLayerOnlyDependsOn(
            "Application", BaseClassLibraryNamespace, LayerNamespace("Domain"), LayerNamespace("Commons"), LayerNamespace("Application"),
            SharedOrderRulesNamespace);
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
            .OnlyHaveDependenciesOn(BaseClassLibraryNamespace, LayerNamespace("Domain"), SharedOrderRulesNamespace).GetResult();

        Assert.False(infrastructureCheckedAgainstTheDomainList.IsSuccessful);
        Assert.Contains("Base.OrderGenerator.Infrastructure.FixOrderClient", infrastructureCheckedAgainstTheDomainList.FailingTypeNames ?? []);
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
