using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Cog;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// Full AST-based audit across every COG script in retail <c>JK1.GOB</c> (269 COGs across 25 levels)
/// and <c>Res2.gob</c> (215 core COGs), verifying zero parse/compile failures and 100% verb support.
/// </summary>
public class CampaignAllCogsAuditTests(ITestOutputHelper output)
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    [Fact]
    public void All_Retail_SP_And_Res2_Cogs_Parse_Compile_And_Use_Only_Supported_Verbs()
    {
        if (!File.Exists(SteamRes2Path) || !File.Exists(SteamJk1Path))
        {
            output.WriteLine("Retail JK GOBs not found at Steam path; skipping retail audit.");
            return;
        }

        int totalScripts = 0;
        var parseErrors = new List<string>();
        var compileErrors = new List<string>();
        var unsupportedVerbs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, path) in new[] { ("JK1.GOB", SteamJk1Path), ("Res2.gob", SteamRes2Path) })
        {
            using var gob = GobArchive.FromFile(path);
            foreach (var entry in gob.Entries.Where(e => e.Path.EndsWith(".cog", StringComparison.OrdinalIgnoreCase)))
            {
                totalScripts++;
                string fullName = $"{label}:{entry.Path}";
                string text = Encoding.Latin1.GetString(gob.ReadAllBytes(entry.Path));

                CogScript script;
                try
                {
                    script = CogParser.Parse(text, entry.Path);
                }
                catch (Exception ex)
                {
                    parseErrors.Add($"{fullName}: {ex.Message}");
                    continue;
                }

                try
                {
                    _ = CogCompiler.Compile(script);
                }
                catch (Exception ex)
                {
                    compileErrors.Add($"{fullName}: {ex.Message}");
                }

                var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectVerbsFromStatements(script.Statements, verbs);
                foreach (string verb in verbs)
                {
                    if (!CogVm.IsVerbSupported(verb))
                    {
                        if (!unsupportedVerbs.TryGetValue(verb, out var files))
                        {
                            files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            unsupportedVerbs[verb] = files;
                        }
                        files.Add(fullName);
                    }
                }
            }
        }

        output.WriteLine($"Audited {totalScripts} retail COGs across JK1.GOB + Res2.gob.");
        Assert.Empty(parseErrors);
        Assert.Empty(compileErrors);
        Assert.True(
            unsupportedVerbs.Count == 0,
            $"Unsupported verbs found: {string.Join(", ", unsupportedVerbs.Select(kv => $"{kv.Key} ({string.Join(", ", kv.Value.Take(3))})"))}");
        Assert.True(totalScripts >= 480, $"Expected at least 480 scripts across JK1.GOB + Res2.gob, found {totalScripts}.");
    }

    private static void CollectVerbsFromStatements(IEnumerable<CogStmt> statements, HashSet<string> verbs)
    {
        foreach (var stmt in statements)
        {
            CollectVerbsFromStatement(stmt, verbs);
        }
    }

    private static void CollectVerbsFromStatement(CogStmt? stmt, HashSet<string> verbs)
    {
        switch (stmt)
        {
            case null:
                break;
            case Block b:
                CollectVerbsFromStatements(b.Statements, verbs);
                break;
            case @If i:
                CollectVerbsFromExpr(i.Condition, verbs);
                CollectVerbsFromStatement(i.ThenBranch, verbs);
                CollectVerbsFromStatement(i.ElseBranch, verbs);
                break;
            case @For f:
                CollectVerbsFromStatement(f.Init, verbs);
                CollectVerbsFromExpr(f.Condition, verbs);
                CollectVerbsFromExpr(f.Step, verbs);
                CollectVerbsFromStatement(f.Body, verbs);
                break;
            case @While w:
                CollectVerbsFromExpr(w.Condition, verbs);
                CollectVerbsFromStatement(w.Body, verbs);
                break;
            case DoWhile d:
                CollectVerbsFromStatement(d.Body, verbs);
                CollectVerbsFromExpr(d.Condition, verbs);
                break;
            case @Return r:
                CollectVerbsFromExpr(r.Value, verbs);
                break;
            case ExprStmt e:
                CollectVerbsFromExpr(e.Expression, verbs);
                break;
        }
    }

    private static void CollectVerbsFromExpr(CogExpr? expr, HashSet<string> verbs)
    {
        switch (expr)
        {
            case null:
                break;
            case IndexExpr idx:
                CollectVerbsFromExpr(idx.Target, verbs);
                CollectVerbsFromExpr(idx.Index, verbs);
                break;
            case BinaryExpr bin:
                CollectVerbsFromExpr(bin.Left, verbs);
                CollectVerbsFromExpr(bin.Right, verbs);
                break;
            case UnaryExpr un:
                CollectVerbsFromExpr(un.Operand, verbs);
                break;
            case AssignExpr asn:
                CollectVerbsFromExpr(asn.Target, verbs);
                CollectVerbsFromExpr(asn.Value, verbs);
                break;
            case CallVerbExpr call:
                verbs.Add(call.VerbName);
                foreach (var arg in call.Arguments)
                    CollectVerbsFromExpr(arg, verbs);
                break;
        }
    }
}
