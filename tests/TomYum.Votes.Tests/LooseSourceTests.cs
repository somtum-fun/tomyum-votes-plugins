using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using TomYum.Plugins.Abstractions;

using Xunit;

namespace TomYum.Votes.Tests;

/// <summary>
/// The folder the server mounts must compile the way TomYum's PluginLoader compiles loose source:
/// Roslyn, no implicit usings, nullable on, every assembly the process runs with as a reference. Then
/// the plugin it finds must be the one plugin.json names.
/// </summary>
public sealed class LooseSourceTests
{
    [Fact]
    public void The_plugin_folder_compiles_like_the_server_compiles_it()
    {
        var folder = FindPluginFolder();
        var sources = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(sources);

        var trees = sources.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f)).ToList();
        var compilation = CSharpCompilation.Create(
            "TomYumPlugin_tomyum_votes",
            trees,
            References(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable));

        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        var problems = emit.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()).ToList();
        Assert.True(emit.Success, string.Join("\n", problems));
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "plugin.json")));
        var id = manifest.RootElement.GetProperty("id").GetString();
        var entryPoint = manifest.RootElement.GetProperty("entryPoint").GetString();
        Assert.Equal("tomyum.votes", id);

        pe.Position = 0;
        var (manifestId, unload) = LoadAndReadManifest(pe, entryPoint!);
        Assert.Equal(id, manifestId);
        unload.Unload();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string Id, AssemblyLoadContext Context) LoadAndReadManifest(Stream pe, string entryPoint)
    {
        var context = new AssemblyLoadContext("votes-test", isCollectible: true);
        var assembly = context.LoadFromStream(pe);
        var type = assembly.GetTypes().Single(t => t.Name == entryPoint && typeof(IPlugin).IsAssignableFrom(t));
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        var plugin = (IPlugin)Activator.CreateInstance(type)!;
        return (plugin.Manifest.Id, context);
    }

    private static List<MetadataReference> References()
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // This test assembly holds the same types (it compiles the folder too); the server's doesn't.
        seen.Add(Path.GetFileName(typeof(LooseSourceTests).Assembly.Location));
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(path) && seen.Add(Path.GetFileName(path)))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        var abstractions = typeof(IPlugin).Assembly.Location;
        if (seen.Add(Path.GetFileName(abstractions)))
            references.Add(MetadataReference.CreateFromFile(abstractions));
        return references;
    }

    internal static string FindPluginFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tomyum-votes");
            if (File.Exists(Path.Combine(candidate, "plugin.json")))
                return candidate;
        }

        throw new DirectoryNotFoundException("tomyum-votes/plugin.json not found above the test output");
    }
}
