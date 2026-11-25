// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

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
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root.FullName, projectRoot), "*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".razor" or ".cshtml")) continue;
            string relative = Path.GetRelativePath(root.FullName, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(segment =>
                    segment is "bin" or "obj" || segment.EndsWith(".Tests", StringComparison.Ordinal)))
                continue;
            string source = File.ReadAllText(file);
            int lineOffset = 0;
            if (extension != ".cs")
            {
                // Component C# is in a class-body directive surrounded by markup.
                Match code = Regex.Match(source, @"(?m)^[ \t]*@(?:code|functions)\s*\{");
                if (!code.Success) continue;
                int brace = code.Index + code.Length - 1;
                lineOffset = source[..brace].Count(chr => chr == '\n');
                source = "class RazorComponent " + source[brace..];
            }
            foreach (var symbols in new[] { new[] { "DEBUG", "TRACE" }, new[] { "TRACE" } })
            {
                var syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: symbols));
                foreach (var handler in syntax.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
                    if (handler.Declaration is null && handler.Block.Statements.Count == 0)
                        locations.Add($"{relative}:{lineOffset + handler.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
            }
        }
        Assert.True(locations.Count <= MaximumUntypedEmptyCatches, string.Join(Environment.NewLine, locations));
    }
}
