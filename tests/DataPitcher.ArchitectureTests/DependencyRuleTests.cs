using System.Xml.Linq;
using Xunit;
namespace DataPitcher.ArchitectureTests;

// Nothing_ReferencesTheApiProject and OnlyApi_ReferencesConcreteProviderProjects are
// intentionally vacuous until a DataPitcher.Api project and a DataPitcher.Providers.*
// project exist: with no such projects in the solution yet, Projects() never yields a
// name that could violate either rule. They start enforcing real boundaries the moment
// those projects are added in a later slice.
public sealed class DependencyRuleTests
{
    [Fact] public void Core_ReferencesNoAspNetDataAccessOrProviderPackage() { var names=References(Project("DataPitcher.Core")).Concat(Packages(Project("DataPitcher.Core"))); Assert.DoesNotContain(names,name => name.StartsWith("Microsoft.AspNetCore",StringComparison.Ordinal) || name.StartsWith("DataPitcher.Providers.",StringComparison.Ordinal) || name is "Dapper" or "LinqToDB" or "Microsoft.EntityFrameworkCore" or "Npgsql" or "Microsoft.Data.SqlClient"); }
    [Fact] public void Core_HasNoProjectOrPackageDependencies() { var core=Project("DataPitcher.Core"); Assert.Empty(References(core)); Assert.Empty(Packages(core)); }
    // Scoped to src/ like OnlyApi_ReferencesConcreteProviderProjects below: the API is a leaf composition root for
    // production code, but its own integration-test project legitimately references it to host WebApplicationFactory<Program>.
    [Fact] public void Nothing_ReferencesTheApiProject() => Assert.DoesNotContain(Projects().Where(p=>Path.GetRelativePath(Root,p).StartsWith("src"+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&Name(p)!="DataPitcher.Api").SelectMany(References),name=>name=="DataPitcher.Api");
    // Production projects must remain provider-agnostic outside the API composition root; test projects need their providers to test them.
    [Fact] public void OnlyApi_ReferencesConcreteProviderProjects() => Assert.DoesNotContain(Projects().Where(p=>Path.GetRelativePath(Root,p).StartsWith("src"+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&Name(p)!="DataPitcher.Api").SelectMany(References),name=>name.StartsWith("DataPitcher.Providers.",StringComparison.Ordinal));
    [Fact]
    public void AuthAbstractions_ReferencesCoreAndHasNoPackages()
    {
        var project = Project("DataPitcher.Auth.Abstractions");
        Assert.Equal(["DataPitcher.Core"], References(project));
        Assert.Empty(Packages(project));
    }
    [Fact]
    public void Api_IsCompositionRootAndCoreRemainsDependencyFree()
    {
        var core = Project("DataPitcher.Core");
        Assert.Empty(References(core));
        Assert.Empty(Packages(core));
        var api = Project("DataPitcher.Api");
        Assert.Equal(["DataPitcher.Auth.Abstractions", "DataPitcher.Core", "DataPitcher.Infrastructure"], References(api).OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(Projects().Where(p => Path.GetRelativePath(Root, p).StartsWith("src" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Name(p) != "DataPitcher.Api").SelectMany(References), name => name == "DataPitcher.Api");
    }
    private static string Root { get; }=FindRoot();
    private static string Project(string name)=>Projects().Single(p=>Name(p)==name);
    private static IEnumerable<string> Projects()=>Directory.GetFiles(Root,"*.csproj",SearchOption.AllDirectories);
    private static string Name(string project)=>Path.GetFileNameWithoutExtension(project);
    private static IEnumerable<string> References(string project)=>XDocument.Load(project).Descendants("ProjectReference").Select(x=>Path.GetFileNameWithoutExtension(x.Attribute("Include")!.Value));
    private static IEnumerable<string> Packages(string project)=>XDocument.Load(project).Descendants("PackageReference").Select(x=>x.Attribute("Include")!.Value);
    private static string FindRoot() { for (var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent) if (File.Exists(Path.Combine(directory.FullName,"DataPitcher.sln"))) return directory.FullName; throw new DirectoryNotFoundException("DataPitcher.sln"); }
}
