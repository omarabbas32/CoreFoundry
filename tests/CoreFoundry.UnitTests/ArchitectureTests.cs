using System.Reflection;
using Shouldly;

namespace CoreFoundry.UnitTests;

/// <summary>Guards the Clean Architecture dependency rule at the assembly level.</summary>
public class ArchitectureTests
{
    private static readonly Assembly DomainAssembly = typeof(global::CoreFoundry.Domain.AssemblyMarker).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(global::CoreFoundry.Application.AssemblyMarker).Assembly;

    [Theory]
    [InlineData("CoreFoundry.Application")]
    [InlineData("CoreFoundry.Infrastructure")]
    [InlineData("CoreFoundry.Api")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("MySqlConnector")]
    [InlineData("Dapper")]
    public void Domain_does_not_depend_on(string assemblyName) =>
        ReferencedNames(DomainAssembly).ShouldNotContain(assemblyName);

    [Theory]
    [InlineData("CoreFoundry.Infrastructure")]
    [InlineData("CoreFoundry.Api")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("MySqlConnector")]
    [InlineData("Dapper")]
    public void Application_does_not_depend_on(string assemblyName) =>
        ReferencedNames(ApplicationAssembly).ShouldNotContain(assemblyName);

    private static List<string?> ReferencedNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(reference => reference.Name)];
}
