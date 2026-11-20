using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.AI;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class AiClassTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    [Fact]
    public void Synthetic_DefaultValues_MatchSithEngineDefaults()
    {
        var aiClass = AiClassReader.Parse(string.Empty, "default.ai");

        Assert.Equal("default.ai", aiClass.Name);
        Assert.Equal(-1, aiClass.Index);
        Assert.Equal(0.0f, aiClass.Alignment);
        Assert.Equal(0.0f, aiClass.Rank);
        Assert.Equal(0.5f, aiClass.MaxStep);
        Assert.Equal(20.0f, aiClass.SightDistance);
        Assert.Equal(10.0f, aiClass.HeardDistance);
        Assert.Equal(0.0f, aiClass.WakeupDistance);
        Assert.Equal(0.5f, aiClass.Accuracy);
        Assert.Equal(180.0f, aiClass.FovDegrees);
        Assert.Equal(0.0f, aiClass.Fov);
        Assert.Empty(aiClass.Instincts);
    }

    [Fact]
    public void Synthetic_ParseHeaderProperties_CorrectValues()
    {
        string aiText = @"
# Synthetic AI test
alignment=-1.0, rank=0.75, fov=200, maxstep=0.6, sightdist=15.0, heardist=7.5, wakeupdist=25.0, accuracy=0.85
";
        var ai = AiClassReader.Parse(aiText, "test.ai");

        Assert.Equal(-1.0f, ai.Alignment);
        Assert.Equal(0.75f, ai.Rank);
        Assert.Equal(200.0f, ai.FovDegrees);
        Assert.Equal(MathF.Cos(100.0f * (MathF.PI / 180.0f)), ai.Fov, precision: 4);
        Assert.Equal(0.6f, ai.MaxStep);
        Assert.Equal(15.0f, ai.SightDistance);
        Assert.Equal(7.5f, ai.HeardDistance);
        Assert.Equal(25.0f, ai.WakeupDistance);
        Assert.Equal(0.85f, ai.Accuracy);
    }

    [Fact]
    public void Synthetic_HeaderSpacingVariations_ParseCorrectly()
    {
        string aiText = @"
alignment = -1.0, rank = 0.5, fov=180, maxstep =0.2, sightdist= 5.0, heardist=2.0, accuracy=0.20
";
        var ai = AiClassReader.Parse(aiText);

        Assert.Equal(-1.0f, ai.Alignment);
        Assert.Equal(0.5f, ai.Rank);
        Assert.Equal(180.0f, ai.FovDegrees);
        Assert.Equal(0.0f, ai.Fov);
        Assert.Equal(0.2f, ai.MaxStep);
        Assert.Equal(5.0f, ai.SightDistance);
        Assert.Equal(2.0f, ai.HeardDistance);
        Assert.Equal(0.20f, ai.Accuracy);
    }

    [Theory]
    [InlineData(180.0f, 0.0f)]
    [InlineData(360.0f, -1.0f)]
    [InlineData(0.0f, 1.0f)]
    [InlineData(90.0f, 0.7071068f)]
    [InlineData(170.0f, 0.08715574f)]
    [InlineData(200.0f, -0.17364818f)]
    public void Synthetic_FovCalculation_MatchesExpectedCosines(float fovDegrees, float expectedCos)
    {
        float computed = AiClass.ComputeFovCos(fovDegrees);
        Assert.Equal(expectedCos, computed, precision: 4);

        var ai = new AiClass { FovDegrees = fovDegrees };
        Assert.Equal(expectedCos, ai.Fov, precision: 4);
    }

    [Fact]
    public void Synthetic_InstinctArguments_FloatsAndIntsPopulated()
    {
        string aiText = @"
alignment=-1.0
PrimaryFire 2000.0, 0.8, 5.0, 0.20, 0.0, 300.0, 0.20, 0.00, 2.0, 500.0
OpenDoors
";
        var ai = AiClassReader.Parse(aiText);
        Assert.Equal(2, ai.Instincts.Count);

        var primaryFire = ai.Instincts[0];
        Assert.Equal("PrimaryFire", primaryFire.Name);
        Assert.Equal(10, primaryFire.ArgCount);
        Assert.Equal(2000.0f, primaryFire.FloatArgs[0]);
        Assert.Equal(2000, primaryFire.IntArgs[0]);
        Assert.Equal(0.8f, primaryFire.FloatArgs[1]);
        Assert.Equal(0, primaryFire.IntArgs[1]);
        Assert.Equal(5.0f, primaryFire.FloatArgs[2]);
        Assert.Equal(300.0f, primaryFire.FloatArgs[5]);
        Assert.Equal(300, primaryFire.IntArgs[5]);
        Assert.Equal(500.0f, primaryFire.FloatArgs[9]);
        Assert.Equal(0.0f, primaryFire.FloatArgs[10]); // Unspecified defaults to 0
        Assert.Equal(2000.0f, primaryFire[0]);
        Assert.Equal(0.0f, primaryFire.GetFloat(99)); // Out-of-bounds safe
        Assert.Equal(0, primaryFire.GetInt(99));

        var openDoors = ai.Instincts[1];
        Assert.Equal("OpenDoors", openDoors.Name);
        Assert.Equal(0, openDoors.ArgCount);
    }

    [Fact]
    public void Synthetic_RegisteredInstinctModes_Populated()
    {
        string aiText = @"
LookForTarget 500.0, 45000.0
Listen 0.75, 2.0
Follow 0.0, 1.5
Talk 10000.0, 0.10
Jump 2000.0, 0.2, 0.2
Dodge 0.1, 0.1
";
        var ai = AiClassReader.Parse(aiText);

        var look = ai.FindInstinct("LookForTarget")!;
        Assert.NotNull(look);
        Assert.Equal((int)(AiMode.Active | AiMode.Searching), look.Param1);
        Assert.Equal(0, look.Param2);
        Assert.Equal(0, look.Param3);

        var listen = ai.FindInstinct("listen")!;
        Assert.NotNull(listen);
        Assert.Equal(0, listen.Param1);
        Assert.Equal(0, listen.Param2);
        Assert.Equal((int)(AiMode.Searching | AiMode.Attacking | AiMode.Moving), listen.Param3);

        var follow = ai.FindInstinct("Follow")!;
        Assert.NotNull(follow);
        Assert.Equal((int)AiMode.Attacking, follow.Param1);
        Assert.Equal((int)AiMode.Fleeing, follow.Param2);
        Assert.Equal((int)(AiMode.Fleeing | AiMode.TargetVisible | AiMode.Active), follow.Param3);

        var talk = ai.FindInstinct("Talk")!;
        Assert.NotNull(talk);
        Assert.Equal(0xFFFF, talk.Param1);

        var jump = ai.FindInstinct("Jump")!;
        Assert.NotNull(jump);
        Assert.Equal((int)(AiMode.TargetVisible | AiMode.Active | AiMode.Searching), jump.Param3);

        var dodge = ai.FindInstinct("Dodge")!;
        Assert.NotNull(dodge);
        Assert.Equal((int)(AiMode.Sleeping | AiMode.Attacking | AiMode.Moving), dodge.Param3);
    }

    [Fact]
    public void Synthetic_CommentsAndFormatting_Ignored()
    {
        string aiText = @"
# Jedi Knight AI File
; Alternate comment style
#
# Comments everywhere

alignment=-1.0, rank=0.5 # inline comment here

# instincts section
LookForTarget   	500.0, 30000.0  # target range
; another comment
OpenDoors
";
        var ai = AiClassReader.Parse(aiText);
        Assert.Equal(-1.0f, ai.Alignment);
        Assert.Equal(0.5f, ai.Rank);
        Assert.Equal(2, ai.Instincts.Count);
        Assert.True(ai.HasInstinct("LookForTarget"));
        Assert.True(ai.HasInstinct("OpenDoors"));
    }

    [Fact]
    public void Synthetic_AiClass_HelperMethods()
    {
        var ai = new AiClass { Name = "test.ai" };
        ai.Instincts.Add(new AiInstinct { Name = "LookForTarget" });
        ai.Instincts.Add(new AiInstinct { Name = "PrimaryFire" });
        ai.Instincts.Add(new AiInstinct { Name = "Dodge" });

        Assert.True(ai.HasInstinct("lookfortarget"));
        Assert.True(ai.HasInstinct("PRIMARYFIRE"));
        Assert.False(ai.HasInstinct("NonExistent"));

        var found = ai.FindInstinct("dodge");
        Assert.NotNull(found);
        Assert.Equal("Dodge", found.Name);

        var multiple = ai.GetInstincts("dodge").ToList();
        Assert.Single(multiple);

        Assert.Contains("test.ai", ai.ToString());
    }

    [Fact]
    public void Synthetic_AiClassReader_StreamAndOverloads()
    {
        string aiText = "alignment=-1.0, rank=0.5\nOpenDoors\n";
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(aiText));

        var ai = AiClassReader.Read(ms, "from_stream.ai");
        Assert.Equal("from_stream.ai", ai.Name);
        Assert.Equal(-1.0f, ai.Alignment);
        Assert.Single(ai.Instincts);
        Assert.Equal("OpenDoors", ai.Instincts[0].Name);
    }

    [Fact]
    public void Synthetic_DifficultyResolution_WorksWithVirtualFileSystem()
    {
        using var vfs = new MemoryVfs();
        vfs.Add("misc/ai/guard.ai", "alignment=-1.0, rank=0.5\nOpenDoors\n");
        vfs.Add("misc/ai/guard.ai0", "alignment=-1.0, rank=0.2\nOpenDoors\n");
        vfs.Add("misc/ai/guard.ai2", "alignment=-1.0, rank=0.9\nOpenDoors\n");

        // Difficulty 0 should load .ai0
        var easy = AiClassReader.Load(vfs, "guard.ai", difficulty: 0);
        Assert.Equal(0.2f, easy.Rank);

        // Difficulty 1 should load base .ai
        var medium = AiClassReader.Load(vfs, "guard.ai", difficulty: 1);
        Assert.Equal(0.5f, medium.Rank);

        // Difficulty 2 should load .ai2
        var hard = AiClassReader.Load(vfs, "guard.ai", difficulty: 2);
        Assert.Equal(0.9f, hard.Rank);

        // Path with misc/ai/ prefix should also work
        var mediumWithPrefix = AiClassReader.Load(vfs, "misc/ai/guard.ai", difficulty: 1);
        Assert.Equal(0.5f, mediumWithPrefix.Rank);
    }

    [Fact]
    public void Synthetic_FallbackToDefaultAi_WhenFileNotFound()
    {
        using var vfs = new MemoryVfs();
        vfs.Add("misc/ai/default.ai", "alignment=0.0, rank=0.0\n");

        // Requesting a non-existent AI file should fall back to default.ai
        var fallback = AiClassReader.Load(vfs, "missing_trooper.ai", difficulty: 1);
        Assert.NotNull(fallback);
        Assert.Equal(0.0f, fallback.Alignment);
    }

    private sealed class MemoryVfs : IVirtualFileSystem
    {
        private readonly System.Collections.Generic.Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path, string content)
        {
            _files[GobArchive.NormalizePath(path)] = Encoding.ASCII.GetBytes(content);
        }

        public bool FileExists(string path) => _files.ContainsKey(GobArchive.NormalizePath(path));

        public byte[] ReadAllBytes(string path)
        {
            if (_files.TryGetValue(GobArchive.NormalizePath(path), out var data))
                return data;
            throw new FileNotFoundException($"File not found in test VFS: {path}");
        }

        public Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path), writable: false);

        public string ReadAllText(string path) => Encoding.ASCII.GetString(ReadAllBytes(path));

        public void Dispose() { }
    }

    [Fact]
    public void RetailGob_Stormtrooper_MatchesExactRetailSpecs()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var st = AiClassReader.Load(archive, "stdefault.ai", difficulty: 1);

        Assert.Equal("stdefault.ai", st.Name);
        Assert.Equal(-1.0f, st.Alignment);
        Assert.Equal(0.5f, st.Rank);
        Assert.Equal(170.0f, st.FovDegrees);
        Assert.Equal(MathF.Cos(85.0f * (MathF.PI / 180.0f)), st.Fov, precision: 4);
        Assert.Equal(0.2f, st.MaxStep);
        Assert.Equal(5.0f, st.SightDistance);
        Assert.Equal(2.0f, st.HeardDistance);
        Assert.Equal(0.20f, st.Accuracy);

        Assert.Equal(9, st.Instincts.Count);
        Assert.True(st.HasInstinct("LookForTarget"));
        Assert.True(st.HasInstinct("Listen"));
        Assert.True(st.HasInstinct("PrimaryFire"));
        Assert.True(st.HasInstinct("BlindFire"));
        Assert.True(st.HasInstinct("Follow"));
        Assert.True(st.HasInstinct("OpenDoors"));
        Assert.True(st.HasInstinct("CircleStrafe"));
        Assert.True(st.HasInstinct("ReturnHome"));
        Assert.True(st.HasInstinct("Talk"));

        var pf = st.FindInstinct("PrimaryFire")!;
        Assert.Equal(10, pf.ArgCount);
        Assert.Equal(2000.0f, pf.FloatArgs[0]);
        Assert.Equal(0.8f, pf.FloatArgs[1]);
        Assert.Equal(5.0f, pf.FloatArgs[2]);
        Assert.Equal(0.20f, pf.FloatArgs[3]);
        Assert.Equal(0.0f, pf.FloatArgs[4]);
        Assert.Equal(300.0f, pf.FloatArgs[5]);
        Assert.Equal(0.20f, pf.FloatArgs[6]);
        Assert.Equal(0.00f, pf.FloatArgs[7]);
        Assert.Equal(2.0f, pf.FloatArgs[8]);
        Assert.Equal(500.0f, pf.FloatArgs[9]);
    }

    [Fact]
    public void RetailGob_ReeYees_MatchesExactRetailSpecs()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var ry = AiClassReader.Load(archive, "rydefault.ai", difficulty: 1);

        Assert.Equal("rydefault.ai", ry.Name);
        Assert.Equal(-1.0f, ry.Alignment);
        Assert.Equal(0.5f, ry.Rank);
        Assert.Equal(200.0f, ry.FovDegrees);
        Assert.Equal(MathF.Cos(100.0f * (MathF.PI / 180.0f)), ry.Fov, precision: 4);
        Assert.Equal(0.4f, ry.MaxStep);
        Assert.Equal(5.0f, ry.SightDistance);
        Assert.Equal(2.0f, ry.HeardDistance);
        Assert.Equal(0.10f, ry.Accuracy);

        Assert.True(ry.HasInstinct("Jump"));
        Assert.True(ry.HasInstinct("LookForTarget"));
        Assert.True(ry.HasInstinct("Listen"));
        Assert.True(ry.HasInstinct("PrimaryFire"));
        Assert.True(ry.HasInstinct("BlindFire"));
        Assert.True(ry.HasInstinct("Follow"));
        Assert.True(ry.HasInstinct("OpenDoors"));
        Assert.True(ry.HasInstinct("ReturnHome"));
        Assert.True(ry.HasInstinct("Retreat"));
        Assert.True(ry.HasInstinct("Flee"));
        Assert.True(ry.HasInstinct("Talk"));

        var jump = ry.FindInstinct("Jump")!;
        Assert.Equal(3, jump.ArgCount);
        Assert.Equal(2000.0f, jump.FloatArgs[0]);
        Assert.Equal(0.2f, jump.FloatArgs[1]);
        Assert.Equal(0.2f, jump.FloatArgs[2]);
    }

    [Fact]
    public void RetailGob_Greedo_Rodian_MatchesExactRetailSpecs()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var gr = AiClassReader.Load(archive, "grdefault.ai", difficulty: 1);

        Assert.Equal("grdefault.ai", gr.Name);
        Assert.Equal(-1.0f, gr.Alignment);
        Assert.Equal(0.5f, gr.Rank);
        Assert.Equal(200.0f, gr.FovDegrees);
        Assert.Equal(0.6f, gr.MaxStep);
        Assert.Equal(5.0f, gr.SightDistance);
        Assert.Equal(2.0f, gr.HeardDistance);
        Assert.Equal(0.25f, gr.Accuracy);

        Assert.True(gr.HasInstinct("Dodge"));
        Assert.True(gr.HasInstinct("Retreat"));
        Assert.True(gr.HasInstinct("Flee"));
    }

    [Fact]
    public void RetailGob_Tusken_MatchesExactRetailSpecs()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var tu = AiClassReader.Load(archive, "tudefault.ai", difficulty: 1);

        Assert.Equal("tudefault.ai", tu.Name);
        Assert.Equal(-1.0f, tu.Alignment);
        Assert.Equal(0.5f, tu.Rank);
        Assert.Equal(180.0f, tu.FovDegrees);
        Assert.Equal(0.0f, tu.Fov);
        Assert.Equal(0.3f, tu.MaxStep);
        Assert.Equal(5.0f, tu.SightDistance);
        Assert.Equal(2.0f, tu.HeardDistance);
        Assert.Equal(0.20f, tu.Accuracy);

        Assert.True(tu.HasInstinct("Jump"));
        Assert.True(tu.HasInstinct("PrimaryFire"));
        Assert.True(tu.HasInstinct("Flee"));
    }

    [Fact]
    public void RetailGob_DifficultyResolution_LoadsCorrectDifficultyFile()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var archive = GobArchive.FromFile(SteamRes2Path);

        // In Res2.gob, tudefault.ai, tudefault.ai0, and tudefault.ai2 all exist
        var easy = AiClassReader.Load(archive, "tudefault.ai", difficulty: 0);
        var medium = AiClassReader.Load(archive, "tudefault.ai", difficulty: 1);
        var hard = AiClassReader.Load(archive, "tudefault.ai", difficulty: 2);

        Assert.NotNull(easy);
        Assert.NotNull(medium);
        Assert.NotNull(hard);

        // Verify they were parsed successfully
        Assert.NotEmpty(easy.Instincts);
        Assert.NotEmpty(medium.Instincts);
        Assert.NotEmpty(hard.Instincts);
    }

    [Fact]
    public void RetailGob_All231AiFiles_ParseSuccessfully()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var aiEntries = archive.Entries
            .Where(e => e.Path.StartsWith("misc/ai/", StringComparison.OrdinalIgnoreCase) &&
                        (e.Path.EndsWith(".ai", StringComparison.OrdinalIgnoreCase) ||
                         e.Path.EndsWith(".ai0", StringComparison.OrdinalIgnoreCase) ||
                         e.Path.EndsWith(".ai2", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(aiEntries.Count >= 200, $"Expected >= 200 AI files in Res2.gob, found {aiEntries.Count}");

        foreach (var entry in aiEntries)
        {
            byte[] bytes = archive.ReadAllBytes(entry.Path);
            string text = Encoding.ASCII.GetString(bytes);

            var ai = AiClassReader.Parse(text, Path.GetFileName(entry.Path));
            Assert.NotNull(ai);
            Assert.True(ai.SightDistance >= 0f, $"{entry.Path} has invalid SightDistance {ai.SightDistance}");
            Assert.True(ai.HeardDistance >= 0f, $"{entry.Path} has invalid HeardDistance {ai.HeardDistance}");
            Assert.True(ai.MaxStep >= 0f, $"{entry.Path} has invalid MaxStep {ai.MaxStep}");
            Assert.True(ai.Accuracy >= 0f, $"{entry.Path} has invalid Accuracy {ai.Accuracy}");
        }
    }
}
