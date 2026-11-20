using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSith.Core.Cog;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// Comprehensive unit tests for OpenSith COG Script Lexer, Parser, and AST.
/// Tests synthetic cases as well as the full retail LucasArts Jedi Knight COG corpus (Res2.gob and JK1.GOB).
/// </summary>
public class CogParserTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    private readonly ITestOutputHelper _output;

    public CogParserTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SyntheticSymbols_AllTypesAndQualifiers_ParsedCorrectly()
    {
        const string scriptText = """
            # Header comments
            flags=0x240

            symbols
            0: thing     door=door_obj   linkid=2 mask=0x401 desc="Main blast door"
            1: surface   switch0         0        local desc=switch_surface
            2: sector    sec0            nolink
            3: sound     locked=snd.wav  local
            4: template  tpl=ghost       local
            5: model     mod=kyle.3do    local
            6: keyframe  key=anim.key    local
            7: cog       child_cog       local
            8: material  mat=metal.mat   local
            9: ai        ai_file=bot.ai  local
            10: flex     movespeed=8.5   desc="Move speed"
            11: float    delay=1.25      local
            12: int      counter=10      local
            13: vector   direction       local
            14: message  startup
            15: message  activate        1
            end

            code
            startup:
                return;
            end
            """;

        var script = CogParser.Parse(scriptText, "test_symbols.cog");

        Assert.Equal("test_symbols.cog", script.Name);
        Assert.Equal(0x240, script.Flags);
        Assert.Equal(16, script.Symbols.Count);

        // Thing
        var door = script.Symbols[0];
        Assert.Equal("door", door.Name);
        Assert.Equal(CogSymbolType.Thing, door.Type);
        Assert.Equal("door_obj", door.DefaultValue);
        Assert.Equal(2, door.LinkId);
        Assert.Equal(0x401, door.Mask);
        Assert.Equal("Main blast door", door.Desc);
        Assert.False(door.IsLocal);

        // Surface
        var sw = script.Symbols[1];
        Assert.Equal("switch0", sw.Name);
        Assert.Equal(CogSymbolType.Surface, sw.Type);
        Assert.Equal("0", sw.DefaultValue);
        Assert.True(sw.IsLocal);
        Assert.Equal("switch_surface", sw.Desc);

        // Sector with nolink
        var sec = script.Symbols[2];
        Assert.Equal("sec0", sec.Name);
        Assert.Equal(CogSymbolType.Sector, sec.Type);
        Assert.Equal(-1, sec.LinkId);

        // Sound
        var snd = script.Symbols[3];
        Assert.Equal("locked", snd.Name);
        Assert.Equal(CogSymbolType.Sound, snd.Type);
        Assert.Equal("snd.wav", snd.DefaultValue);
        Assert.True(snd.IsLocal);

        // Flex & Float
        var flex = script.Symbols[10];
        Assert.Equal("movespeed", flex.Name);
        Assert.Equal(CogSymbolType.Flex, flex.Type);
        Assert.Equal("8.5", flex.DefaultValue);
        Assert.Equal("Move speed", flex.Desc);

        var flt = script.Symbols[11];
        Assert.Equal("delay", flt.Name);
        Assert.Equal(CogSymbolType.Float, flt.Type);
        Assert.Equal("1.25", flt.DefaultValue);
        Assert.True(flt.IsLocal);

        // Int
        var counter = script.Symbols[12];
        Assert.Equal("counter", counter.Name);
        Assert.Equal(CogSymbolType.Int, counter.Type);
        Assert.Equal("10", counter.DefaultValue);
        Assert.True(counter.IsLocal);

        // Vector
        var vec = script.Symbols[13];
        Assert.Equal("direction", vec.Name);
        Assert.Equal(CogSymbolType.Vector, vec.Type);
        Assert.True(vec.IsLocal);

        // Messages
        var msg1 = script.Symbols[14];
        Assert.Equal("startup", msg1.Name);
        Assert.Equal(CogSymbolType.Message, msg1.Type);

        var msg2 = script.Symbols[15];
        Assert.Equal("activate", msg2.Name);
        Assert.Equal(CogSymbolType.Message, msg2.Type);
        Assert.Equal("1", msg2.DefaultValue);
    }

    [Fact]
    public void SyntheticControlFlow_ForWhileDoWhileIfElse_ParsedIntoAst()
    {
        const string scriptText = """
            symbols
            int i local
            int sum=0 local
            end

            code
            startup:
                // If / Else
                if (sum == 0)
                {
                    sum = 1;
                }
                else
                {
                    sum = 2;
                }

                // While loop
                while (sum < 10)
                {
                    sum = sum + 1;
                }

                // Do-While loop
                do
                {
                    sum = sum - 1;
                }
                while (sum > 5);

                // For loop
                for (i = 0; i < 5; i = i + 1)
                {
                    sum = sum + i;
                    if (sum > 100) break;
                    continue;
                }

                call mysub;
                goto mytarget;

            mytarget:
                return;

            mysub:
                stop;
            end
            """;

        var script = CogParser.Parse(scriptText, "control_flow.cog");

        Assert.NotEmpty(script.Statements);
        Assert.Contains("startup", script.Labels.Keys);
        Assert.Contains("mytarget", script.Labels.Keys);
        Assert.Contains("mysub", script.Labels.Keys);

        // Find If statement
        var ifStmt = script.Statements.OfType<@If>().FirstOrDefault();
        Assert.NotNull(ifStmt);
        Assert.IsType<BinaryExpr>(ifStmt.Condition);
        Assert.IsType<Block>(ifStmt.ThenBranch);
        Assert.NotNull(ifStmt.ElseBranch);
        Assert.IsType<Block>(ifStmt.ElseBranch);

        // Find While statement
        var whileStmt = script.Statements.OfType<@While>().FirstOrDefault();
        Assert.NotNull(whileStmt);
        Assert.IsType<BinaryExpr>(whileStmt.Condition);
        Assert.IsType<Block>(whileStmt.Body);

        // Find DoWhile statement
        var doWhileStmt = script.Statements.OfType<DoWhile>().FirstOrDefault();
        Assert.NotNull(doWhileStmt);
        Assert.IsType<Block>(doWhileStmt.Body);
        Assert.IsType<BinaryExpr>(doWhileStmt.Condition);

        // Find For statement
        var forStmt = script.Statements.OfType<@For>().FirstOrDefault();
        Assert.NotNull(forStmt);
        Assert.NotNull(forStmt.Init);
        Assert.NotNull(forStmt.Condition);
        Assert.NotNull(forStmt.Step);
        Assert.IsType<Block>(forStmt.Body);

        // Check inside for loop body for Break and Continue
        var forBody = (Block)forStmt.Body;
        var innerIf = forBody.Statements.OfType<@If>().First();
        Assert.IsType<@Break>(innerIf.ThenBranch);
        Assert.Contains(forBody.Statements, s => s is @Continue);

        // Check Call and Goto
        Assert.Contains(script.Statements, s => s is CallLabel call && call.Label == "mysub");
        Assert.Contains(script.Statements, s => s is @Goto gt && gt.Label == "mytarget");

        // Check Return / Stop
        Assert.Contains(script.Statements, s => s is @Return);
    }

    [Fact]
    public void SyntheticExpressions_PrecedenceAndOperators_ParsedIntoAst()
    {
        const string scriptText = """
            symbols
            int a local
            int b local
            int c local
            int d local
            int r local
            end

            code
            startup:
                // Precedence: * before +
                r = a + b * c;

                // Precedence: == before &&, && before ||
                r = a == 1 && b == 2 || c == 3;

                // Bitwise operators
                r = (a & b) | (c ^ d);

                // Unary operators: - ! ~
                r = -a + !b + ~c;

                // Array indexing and function calls
                a = b[c + 1];
                SetTimer(0.5);
                PlaySound(a, 1, 0.0, 0);
                r = Rand() * 2;
                return;
            end
            """;

        var script = CogParser.Parse(scriptText, "expressions.cog");

        var exprStmts = script.Statements.OfType<ExprStmt>().Select(s => s.Expression).ToList();
        Assert.NotEmpty(exprStmts);

        // First statement: r = a + b * c;
        var assign1 = Assert.IsType<AssignExpr>(exprStmts[0]);
        Assert.Equal("r", ((Identifier)assign1.Target).Name);
        var addExpr = Assert.IsType<BinaryExpr>(assign1.Value);
        Assert.Equal(CogBinaryOp.Add, addExpr.Op);
        Assert.Equal("a", ((Identifier)addExpr.Left).Name);
        var mulExpr = Assert.IsType<BinaryExpr>(addExpr.Right);
        Assert.Equal(CogBinaryOp.Multiply, mulExpr.Op);

        // Second statement: r = a == 1 && b == 2 || c == 3;
        var assign2 = Assert.IsType<AssignExpr>(exprStmts[1]);
        var orExpr = Assert.IsType<BinaryExpr>(assign2.Value);
        Assert.Equal(CogBinaryOp.LogicalOr, orExpr.Op);
        var andExpr = Assert.IsType<BinaryExpr>(orExpr.Left);
        Assert.Equal(CogBinaryOp.LogicalAnd, andExpr.Op);

        // Array indexing statement: a = b[c + 1];
        var arrayAssign = Assert.IsType<AssignExpr>(exprStmts[4]);
        var indexExpr = Assert.IsType<IndexExpr>(arrayAssign.Value);
        Assert.Equal("b", ((Identifier)indexExpr.Target).Name);
        Assert.IsType<BinaryExpr>(indexExpr.Index);

        // Function call: PlaySound(a, 1, 0.0, 0);
        var callVerb = Assert.IsType<CallVerbExpr>(exprStmts[6]);
        Assert.Equal("PlaySound", callVerb.VerbName);
        Assert.Equal(4, callVerb.Arguments.Count);
        Assert.IsType<Identifier>(callVerb.Arguments[0]);
        Assert.IsType<IntLiteral>(callVerb.Arguments[1]);
        Assert.IsType<FloatLiteral>(callVerb.Arguments[2]);
        Assert.IsType<IntLiteral>(callVerb.Arguments[3]);

        // Zero-arg function call: Rand() * 2;
        var randAssign = Assert.IsType<AssignExpr>(exprStmts[7]);
        var randMul = Assert.IsType<BinaryExpr>(randAssign.Value);
        var randCall = Assert.IsType<CallVerbExpr>(randMul.Left);
        Assert.Equal("Rand", randCall.VerbName);
        Assert.Empty(randCall.Arguments);
    }

    [Fact]
    public void SyntheticVectorLiterals_SpacesAndSlashes_ParsedCorrectly()
    {
        const string scriptText = """
            symbols
            vector v1 local
            vector v2 local
            vector v3 local
            end

            code
            startup:
                v1 = '1.0 2.5 -3.0';
                v2 = '0/1/0';
                v3 = '0.5, -0.5, 10.0';
                return;
            end
            """;

        var script = CogParser.Parse(scriptText, "vectors.cog");

        var exprStmts = script.Statements.OfType<ExprStmt>().Select(s => s.Expression).ToList();
        Assert.Equal(3, exprStmts.Count);

        var a1 = Assert.IsType<AssignExpr>(exprStmts[0]);
        var v1 = Assert.IsType<VectorLiteral>(a1.Value);
        Assert.Equal(new Vector3(1.0f, 2.5f, -3.0f), v1.Value);

        var a2 = Assert.IsType<AssignExpr>(exprStmts[1]);
        var v2 = Assert.IsType<VectorLiteral>(a2.Value);
        Assert.Equal(new Vector3(0.0f, 1.0f, 0.0f), v2.Value);

        var a3 = Assert.IsType<AssignExpr>(exprStmts[2]);
        var v3 = Assert.IsType<VectorLiteral>(a3.Value);
        Assert.Equal(new Vector3(0.5f, -0.5f, 10.0f), v3.Value);
    }

    [Fact]
    public void SyntheticCaseInsensitivity_KeywordsAndIdentifiers_ParsedCorrectly()
    {
        const string scriptText = """
            SYMBOLS
            INT MyVar=1 LOCAL
            END

            CODE
            STARTUP:
                IF (MYVAR == 1)
                {
                    CALL SUBROUTINE;
                }
                STOP;

            SUBROUTINE:
                RETURN;
            END
            """;

        var script = CogParser.Parse(scriptText, "case_test.cog");

        Assert.Single(script.Symbols);
        Assert.Equal("MyVar", script.Symbols[0].Name);
        Assert.True(script.Symbols[0].IsLocal);

        // Labels dictionary should be case-insensitive
        Assert.True(script.Labels.ContainsKey("startup"));
        Assert.True(script.Labels.ContainsKey("STARTUP"));
        Assert.True(script.Labels.ContainsKey("Subroutine"));
        Assert.True(script.Labels.ContainsKey("SUBROUTINE"));

        var ifStmt = Assert.IsType<@If>(script.Statements[1]);
        var block = Assert.IsType<Block>(ifStmt.ThenBranch);
        Assert.Contains(block.Statements, s => s is CallLabel c && c.Label.Equals("SUBROUTINE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(script.Statements, s => s is @Return);
    }

    [Fact]
    public void RetailCorpus_All484Files_ParseWithoutExceptions()
    {
        var archives = new[] { SteamRes2Path, SteamJk1Path };
        int parsedFiles = 0;
        var failures = new List<(string file, string error)>();

        foreach (var path in archives)
        {
            if (!File.Exists(path))
            {
                _output.WriteLine($"Skipping non-existent archive: {path}");
                continue;
            }

            using var archive = GobArchive.FromFile(path);
            var entries = archive.Entries.Where(e => e.Path.EndsWith(".cog", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var entry in entries)
            {
                var text = Encoding.ASCII.GetString(archive.ReadAllBytes(entry.Path));
                try
                {
                    var script = CogParser.Parse(text, Path.GetFileName(entry.Path));
                    Assert.NotNull(script);
                    Assert.NotEmpty(script.Name);
                    parsedFiles++;
                }
                catch (Exception ex)
                {
                    failures.Add((entry.Path, ex.Message));
                }
            }
        }

        if (failures.Count > 0)
        {
            _output.WriteLine($"Failures ({failures.Count}):");
            foreach (var f in failures)
            {
                _output.WriteLine($"  {f.file}: {f.error}");
            }
        }

        _output.WriteLine($"Successfully parsed {parsedFiles} / {parsedFiles + failures.Count} retail cogs.");
        Assert.Empty(failures);

        // When retail assets are present on disk, ensure both Res2.gob (215) and JK1.GOB (269) = 484 were parsed!
        if (File.Exists(SteamRes2Path) && File.Exists(SteamJk1Path))
        {
            Assert.Equal(484, parsedFiles);
        }
    }
}
