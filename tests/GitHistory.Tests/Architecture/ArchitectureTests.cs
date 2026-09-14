using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GitHistory.Core.ViewModels;

namespace GitHistory.Tests.Architecture;

public sealed class ArchitectureTests
{
    [Fact]
    public void Core_has_no_Wpf_DevExpress_application_or_infrastructure_assembly_dependencies()
    {
        var references = typeof(WorkspaceViewModel).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => IsForbiddenAssembly(reference.Name ?? ""));
        string project = Path.Combine(RepositoryRoot(), "src", "GitHistory.Core", "GitHistory.Core.csproj");
        var document = XDocument.Load(project);
        Assert.DoesNotContain(document.Descendants("ProjectReference"), item =>
            (item.Attribute("Include")?.Value ?? "").Contains("Infrastructure", StringComparison.OrdinalIgnoreCase) ||
            (item.Attribute("Include")?.Value ?? "").Contains("App", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(document.Descendants("PackageReference"), item => IsForbiddenAssembly(item.Attribute("Include")?.Value ?? ""));
        Assert.DoesNotContain(document.Descendants("UseWPF"), item => item.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void View_models_are_sealed_and_have_no_Wpf_types_in_fields_constructors_or_member_signatures()
    {
        var viewModels = typeof(WorkspaceViewModel).Assembly.GetTypes()
            .Where(type => type.IsClass && type.IsPublic && type.Namespace == "GitHistory.Core.ViewModels")
            .ToArray();
        Assert.NotEmpty(viewModels);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in viewModels)
        {
            Assert.True(type.IsSealed, $"{type.FullName} must be sealed.");
            var signatures = type.GetFields(flags).Select(field => field.FieldType)
                .Concat(type.GetProperties(flags).Select(property => property.PropertyType))
                .Concat(type.GetMethods(flags).Select(method => method.ReturnType))
                .Concat(type.GetMethods(flags).SelectMany(method => method.GetParameters()).Select(parameter => parameter.ParameterType))
                .Concat(type.GetConstructors(flags).SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType));
            foreach (var dependency in signatures.SelectMany(ExpandType))
                Assert.False(IsForbiddenNamespace(dependency.Namespace ?? ""), $"{type.FullName} exposes or depends on {dependency.FullName}.");
        }
    }

    [Fact]
    public void Main_window_code_behind_contains_only_its_initialize_component_constructor()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "GitHistory.App", "MainWindow.xaml.cs"));
        string classBody = source[(source.IndexOf('{') + 1)..source.LastIndexOf('}')];
        string normalized = Regex.Replace(classBody, @"\s+", "");
        Assert.Contains(normalized, new[]
        {
            "publicMainWindow()=>InitializeComponent();",
            "publicMainWindow(){InitializeComponent();}"
        });
    }

    [Fact]
    public void View_model_sources_do_not_use_service_location_or_blocking_async_calls()
    {
        foreach (string path in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "GitHistory.Core", "ViewModels"), "*.cs"))
        {
            string source = File.ReadAllText(path);
            Assert.DoesNotMatch(@"\bIServiceProvider\b|\bGetRequiredService\s*[<(]|\bApplication\.Current\b", source);
            Assert.DoesNotMatch(@"\.GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(|\.Wait\s*\(|\.Result\b", source);
            Assert.DoesNotMatch(@"\basync\s+void\b", source);
        }
    }

    private static bool IsForbiddenAssembly(string name) =>
        name.StartsWith("DevExpress", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase) ||
        name is "WindowsBase" or "System.Xaml" or "GitHistory.App" or "GitHistory.Infrastructure";

    private static bool IsForbiddenNamespace(string name) =>
        name.StartsWith("System.Windows", StringComparison.Ordinal) || name.StartsWith("DevExpress", StringComparison.Ordinal) ||
        name.StartsWith("GitHistory.Infrastructure", StringComparison.Ordinal) || name.StartsWith("GitHistory.App", StringComparison.Ordinal);

    private static IEnumerable<Type> ExpandType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
            foreach (var item in ExpandType(element)) yield return item;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                foreach (var item in ExpandType(argument)) yield return item;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "GitHistory.Core", "GitHistory.Core.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Run architecture tests from a checkout containing the application source.");
    }
}
