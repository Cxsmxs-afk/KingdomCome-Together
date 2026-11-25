// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KcdMp.Client.Tests;

public sealed class EmptyCatchTests
{
    private const int MaximumUntypedEmptyCatches = 0;

    [Fact]
    public void Production_code_has_no_untyped_empty_catches()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "KCDMP_launcher")))
            root = root.Parent;
        Assert.NotNull(root);

        var locations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string projectRoot in new[] { "dotnet", "KCDMP_launcher" })
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root.FullName, projectRoot), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root.FullName, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(segment =>
                    segment is "bin" or "obj" || segment.EndsWith(".Tests", StringComparison.Ordinal)))
                continue;
            string source = File.ReadAllText(file);
            foreach (var symbols in new[] { new[] { "DEBUG", "TRACE" }, new[] { "TRACE" } })
            {
                var syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: symbols));
                foreach (var handler in syntax.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
                    if (handler.Declaration is null && handler.Block.Statements.Count == 0)
                        locations.Add($"{relative}:{handler.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
            }
        }
        Assert.True(locations.Count <= MaximumUntypedEmptyCatches, string.Join(Environment.NewLine, locations));
    }
}
