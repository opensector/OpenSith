using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Audio;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class SoundClassTests
{
    private readonly ITestOutputHelper _output;

    private const string SteamRes2Path =
        @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamRes1HiPath =
        @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res1hi.gob";

    public SoundClassTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // =========================================================================
    // SYNTHETIC TESTS: SndReader & SoundClass
    // =========================================================================

    [Fact]
    public void SndReader_ParsesBasicEntriesAndAppliesDefaults()
    {
        const string sndText = """
            # Sample Sound Definition
            ; Another comment style
            startmove   door_open.wav
            stopmove    door_stop.wav   0x0000
            moving      door_loop.wav   0x0001  1.0     5.0     0.8
            """;

        var soundClass = SndReader.Parse(sndText, "door.snd");

        Assert.Equal("door.snd", soundClass.Name);
        Assert.Equal(3, soundClass.EntryCount);

        // startmove - defaults applied
        var startMove = soundClass.GetFirst(SoundEvent.StartMove);
        Assert.NotNull(startMove);
        Assert.Equal("door_open.wav", startMove.SoundFileName);
        Assert.Equal(SoundFlags.Absolute, startMove.PlayFlags);
        Assert.Equal(0.5f, startMove.MinRadius);
        Assert.Equal(2.5f, startMove.MaxRadius);
        Assert.Equal(1.0f, startMove.MaxVolume);
        Assert.True(startMove.HasSound);

        // stopmove - explicit 0x0000 flags, default radii & volume
        var stopMove = soundClass.GetFirst(SoundEvent.StopMove);
        Assert.NotNull(stopMove);
        Assert.Equal("door_stop.wav", stopMove.SoundFileName);
        Assert.Equal(SoundFlags.None, stopMove.PlayFlags);
        Assert.Equal(0.5f, stopMove.MinRadius);
        Assert.Equal(2.5f, stopMove.MaxRadius);
        Assert.Equal(1.0f, stopMove.MaxVolume);

        // moving - all fields explicitly specified
        var moving = soundClass.GetFirst(SoundEvent.Moving);
        Assert.NotNull(moving);
        Assert.Equal("door_loop.wav", moving.SoundFileName);
        Assert.Equal(SoundFlags.Loop, moving.PlayFlags);
        Assert.Equal(1.0f, moving.MinRadius);
        Assert.Equal(5.0f, moving.MaxRadius);
        Assert.Equal(0.8f, moving.MaxVolume);
    }

    [Fact]
    public void SndReader_HandlesNoneSoundFilename()
    {
        const string sndText = """
            create   none
            activate df_switch.wav
            """;

        var soundClass = SndReader.Parse(sndText);
        var createEntry = soundClass.GetFirst(SoundEvent.Create);

        Assert.NotNull(createEntry);
        Assert.Equal("none", createEntry.SoundFileName);
        Assert.False(createEntry.HasSound);

        var activateEntry = soundClass.GetFirst(SoundEvent.Activate);
        Assert.NotNull(activateEntry);
        Assert.True(activateEntry.HasSound);
    }

    [Fact]
    public void SndReader_ResolvesStandardAndTyppoAliases()
    {
        const string sndText = """
            surprise    alert1.wav
            suprise     alert2.wav
            choking     choke1.wav
            choked      choke2.wav
            """;

        var soundClass = SndReader.Parse(sndText);

        var surpriseEntries = soundClass[SoundEvent.Surprise];
        Assert.Equal(2, surpriseEntries.Count);
        Assert.Equal("alert1.wav", surpriseEntries[0].SoundFileName);
        Assert.Equal("alert2.wav", surpriseEntries[1].SoundFileName);

        var chokingEntries = soundClass[SoundEvent.Choking];
        Assert.Equal(2, chokingEntries.Count);
        Assert.Equal("choke1.wav", chokingEntries[0].SoundFileName);
        Assert.Equal("choke2.wav", chokingEntries[1].SoundFileName);
    }

    [Fact]
    public void SndReader_SupportsMultipleVariationsAndSelection()
    {
        const string sndText = """
            hurtimpact   hit_light.wav    0x14000  0.3  1.0  0.5
            hurtimpact   hit_medium.wav   0x14000  0.5  2.0  0.7
            hurtimpact   hit_heavy.wav    0x14000  1.0  4.0  0.9
            hurtimpact   hit_critical.wav 0x14000  1.0  5.0  1.0
            """;

        var soundClass = SndReader.Parse(sndText);
        var entries = soundClass[SoundEvent.HurtImpact];
        Assert.Equal(4, entries.Count);

        // Flags should have Precached (0x4000) and Voice (0x10000)
        Assert.True(entries[0].PlayFlags.HasFlag(SoundFlags.Precached));
        Assert.True(entries[0].PlayFlags.HasFlag(SoundFlags.Voice));

        // Ratio selection: 0.0 -> hit_light, 1.0 -> hit_critical
        var sel0 = soundClass.Select(SoundEvent.HurtImpact, 0.0f);
        Assert.NotNull(sel0);
        Assert.Equal("hit_light.wav", sel0.SoundFileName);

        var sel1 = soundClass.Select(SoundEvent.HurtImpact, 0.25f);
        Assert.NotNull(sel1);
        Assert.Equal("hit_medium.wav", sel1.SoundFileName);

        var sel2 = soundClass.Select(SoundEvent.HurtImpact, 0.5f);
        Assert.NotNull(sel2);
        Assert.Equal("hit_heavy.wav", sel2.SoundFileName);

        var sel3 = soundClass.Select(SoundEvent.HurtImpact, 1.0f);
        Assert.NotNull(sel3);
        Assert.Equal("hit_critical.wav", sel3.SoundFileName);
    }

    [Fact]
    public void SndReader_SelectRandom_ChoosesDeterministicallyWithSeed()
    {
        const string sndText = """
            lwalkhard step1.wav
            lwalkhard step2.wav
            lwalkhard step3.wav
            """;

        var soundClass = SndReader.Parse(sndText);
        var rng = new Random(42);

        var selected = Enumerable.Range(0, 30)
            .Select(_ => soundClass.SelectRandom(SoundEvent.LWalkHard, rng)!.SoundFileName)
            .Distinct()
            .ToList();

        Assert.Contains("step1.wav", selected);
        Assert.Contains("step2.wav", selected);
        Assert.Contains("step3.wav", selected);
    }

    [Fact]
    public void SndReader_HandlesWhitespaceAndMalformedLines()
    {
        const string sndText = """
            # Valid comments
            ; Semicolon comments
                    # Indented comments
            
                    singleTokenLineOnly
            
            lwalkmetal    metal1.wav   ,  0x4000  ,  0.3  ,  1.0
            """;

        var soundClass = SndReader.Parse(sndText);
        Assert.Equal(1, soundClass.EntryCount);
        Assert.True(soundClass.HasEvent(SoundEvent.LWalkMetal));
        Assert.Equal("metal1.wav", soundClass.GetFirst(SoundEvent.LWalkMetal)!.SoundFileName);
    }

    // =========================================================================
    // SYNTHETIC TESTS: WavReader & WavInfo
    // =========================================================================

    [Fact]
    public void WavReader_ParsesSyntheticPcm16Mono()
    {
        byte[] wavBytes = CreateSyntheticWav(
            channels: 1,
            sampleRate: 22050,
            bitsPerSample: 16,
            sampleFrames: 1000);

        var info = WavReader.Parse(wavBytes);

        Assert.Equal(WaveAudioFormat.Pcm, info.AudioFormat);
        Assert.True(info.IsPcm);
        Assert.False(info.IsStereo);
        Assert.Equal(1, info.Channels);
        Assert.Equal(22050u, info.SampleRate);
        Assert.Equal(16, info.BitsPerSample);
        Assert.Equal(2, info.BlockAlign);
        Assert.Equal(44100u, info.ByteRate);
        Assert.Equal(2000, info.DataSize);
        Assert.Equal(1000, info.TotalFrames);
        Assert.False(info.HasLoop);

        double expectedSeconds = 1000.0 / 22050.0;
        Assert.InRange(info.DurationSeconds, expectedSeconds - 0.0001, expectedSeconds + 0.0001);

        var pcm = WavReader.ExtractPcmData(wavBytes);
        Assert.Equal(2000, pcm.Length);
        Assert.Equal(wavBytes[info.DataOffset], pcm[0]);
    }

    [Fact]
    public void WavReader_ParsesSyntheticPcm16StereoWithLoopPoints()
    {
        byte[] wavBytes = CreateSyntheticWav(
            channels: 2,
            sampleRate: 44100,
            bitsPerSample: 16,
            sampleFrames: 500,
            loopPoints: (start: 100, end: 400));

        var info = WavReader.Parse(wavBytes);

        Assert.True(info.IsStereo);
        Assert.Equal(2, info.Channels);
        Assert.Equal(44100u, info.SampleRate);
        Assert.Equal(4, info.BlockAlign);
        Assert.Equal(176400u, info.ByteRate);
        Assert.Equal(2000, info.DataSize);
        Assert.Equal(500, info.TotalFrames);
        Assert.True(info.HasLoop);
        Assert.Equal(100u, info.LoopStart);
        Assert.Equal(400u, info.LoopEnd);
    }

    [Fact]
    public void WavReader_ParsesSyntheticPcm8MonoWithOddChunkPadding()
    {
        byte[] wavBytes = CreateSyntheticWav(
            channels: 1,
            sampleRate: 11025,
            bitsPerSample: 8,
            sampleFrames: 101, // odd data size
            oddChunkSize: true,
            loopPoints: (start: 10, end: 90));

        var info = WavReader.Parse(wavBytes);

        Assert.False(info.IsStereo);
        Assert.Equal(11025u, info.SampleRate);
        Assert.Equal(8, info.BitsPerSample);
        Assert.Equal(1, info.BlockAlign);
        Assert.Equal(102, info.DataSize); // 101 + 1 odd
        Assert.True(info.HasLoop);
        Assert.Equal(10u, info.LoopStart);
        Assert.Equal(90u, info.LoopEnd);
    }

    [Fact]
    public void WavReader_ThrowsOnInvalidOrTruncatedHeaders()
    {
        // 1. Data too short (< 12 bytes)
        Assert.Throws<InvalidDataException>(() => WavReader.Parse(new byte[8]));

        // 2. Bad RIFF signature
        byte[] badRiff = CreateSyntheticWav();
        badRiff[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => WavReader.Parse(badRiff));

        // 3. Bad WAVE signature
        byte[] badWave = CreateSyntheticWav();
        badWave[8] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => WavReader.Parse(badWave));

        // 4. Missing data chunk (only fmt)
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(28);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((ushort)1);
            w.Write((ushort)1);
            w.Write(22050u);
            w.Write(44100u);
            w.Write((ushort)2);
            w.Write((ushort)16);
        }
        Assert.Throws<InvalidDataException>(() => WavReader.Parse(ms.ToArray()));

        // 5. TryParse returns false cleanly without throwing
        Assert.False(WavReader.TryParse(new byte[5], out var info));
        Assert.Null(info);
    }

    // =========================================================================
    // RETAIL ASSET TESTS: Res2.gob & Res1hi.gob (Steam Jedi Knight)
    // =========================================================================

    [Fact]
    public void Retail_AllSndFilesParseSuccessfully()
    {
        if (!File.Exists(SteamRes2Path))
        {
            _output.WriteLine($"Skipping test: {SteamRes2Path} not found.");
            return;
        }

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var sndEntries = archive.Entries
            .Where(e => e.Path.EndsWith(".snd", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(sndEntries);
        _output.WriteLine($"Testing {sndEntries.Count} retail .snd files from Res2.gob...");

        int totalEntries = 0;
        foreach (var entry in sndEntries)
        {
            byte[] bytes = archive.ReadAllBytes(entry.Path);
            var soundClass = SndReader.Read(bytes, entry.Path);

            Assert.Equal(entry.Path, soundClass.Name);
            Assert.True(soundClass.EntryCount > 0, $"SoundClass '{entry.Path}' has 0 entries.");
            totalEntries += soundClass.EntryCount;
        }

        _output.WriteLine($"Successfully parsed {sndEntries.Count} .snd files with {totalEntries} total entries!");
    }

    [Fact]
    public void Retail_KyleSndMatchesEngineExpectations()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        byte[] bytes = archive.ReadAllBytes("misc/snd/ky.snd");
        var ky = SndReader.Read(bytes, "ky.snd");

        // Footsteps
        Assert.True(ky.HasEvent(SoundEvent.LWalkHard));
        Assert.True(ky.HasEvent(SoundEvent.RWalkHard));
        Assert.True(ky.HasEvent(SoundEvent.LRunHard));
        Assert.True(ky.HasEvent(SoundEvent.RRunHard));
        Assert.True(ky.HasEvent(SoundEvent.LWalkMetal));
        Assert.Equal("kwalk01.wav", ky.GetFirst(SoundEvent.LWalkHard)!.SoundFileName);
        Assert.Equal("kwalk02.wav", ky.GetFirst(SoundEvent.RWalkHard)!.SoundFileName);

        // Jump & Land
        Assert.True(ky.HasEvent(SoundEvent.Jump));
        Assert.True(ky.HasEvent(SoundEvent.LandHard));

        // Hurt variations
        var hurtImpact = ky[SoundEvent.HurtImpact];
        Assert.Equal(4, hurtImpact.Count);
        Assert.Equal("kyljpd.wav", hurtImpact[0].SoundFileName);
        Assert.Equal("kylgtsb.wav", hurtImpact[1].SoundFileName);
        Assert.Equal("kylgtsc.wav", hurtImpact[2].SoundFileName);
        Assert.Equal("kylgtlc.wav", hurtImpact[3].SoundFileName);

        // Vocal flags (0x010000 Voice)
        var falling = ky.GetFirst(SoundEvent.Falling);
        Assert.NotNull(falling);
        Assert.Equal("kylyld.wav", falling.SoundFileName);
        Assert.True(falling.PlayFlags.HasFlag(SoundFlags.Voice));

        // Death flags (0x010100 Voice | HighPriority)
        var death1 = ky.GetFirst(SoundEvent.Death1);
        Assert.NotNull(death1);
        Assert.Equal("i00ky86z.wav", death1.SoundFileName);
        Assert.True(death1.PlayFlags.HasFlag(SoundFlags.Voice));
        Assert.True(death1.PlayFlags.HasFlag(SoundFlags.HighPriority));
    }

    [Fact]
    public void Retail_BoomRailSndMatchesEngineExpectations()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        byte[] bytes = archive.ReadAllBytes("misc/snd/02boom.snd");
        var boom = SndReader.Read(bytes, "02boom.snd");

        Assert.Equal(3, boom.EntryCount);
        var start = boom.GetFirst(SoundEvent.StartMove);
        var stop = boom.GetFirst(SoundEvent.StopMove);
        var moving = boom.GetFirst(SoundEvent.Moving);

        Assert.NotNull(start);
        Assert.Equal("09pipesdetatch.wav", start.SoundFileName.ToLowerInvariant());

        Assert.NotNull(stop);
        Assert.Equal("funicularstop01.wav", stop.SoundFileName.ToLowerInvariant());

        Assert.NotNull(moving);
        Assert.Equal("00conveyoramb02.wav", moving.SoundFileName.ToLowerInvariant());
        Assert.True(moving.PlayFlags.HasFlag(SoundFlags.Loop));
    }

    [Fact]
    public void Retail_AllWavFilesInRes1HiParseSuccessfully()
    {
        if (!File.Exists(SteamRes1HiPath))
        {
            _output.WriteLine($"Skipping test: {SteamRes1HiPath} not found.");
            return;
        }

        using var archive = GobArchive.FromFile(SteamRes1HiPath);
        var wavEntries = archive.Entries
            .Where(e => e.Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(wavEntries);
        _output.WriteLine($"Testing all {wavEntries.Count} retail .wav files from Res1hi.gob...");

        int loopCount = 0;
        int stereoCount = 0;
        int monoCount = 0;

        foreach (var entry in wavEntries)
        {
            byte[] bytes = archive.ReadAllBytes(entry.Path);
            var info = WavReader.Parse(bytes);

            Assert.Equal(WaveAudioFormat.Pcm, info.AudioFormat);
            Assert.True(info.BitsPerSample == 8 || info.BitsPerSample == 16,
                $"Unexpected bitsPerSample {info.BitsPerSample} in {entry.Path}");
            Assert.True(info.SampleRate == 11025 || info.SampleRate == 22050 || info.SampleRate == 44100,
                $"Unexpected sample rate {info.SampleRate} in {entry.Path}");
            Assert.True(info.DataSize > 0, $"Empty data chunk in {entry.Path}");

            var pcm = WavReader.ExtractPcmData(bytes);
            Assert.Equal(info.DataSize, pcm.Length);

            if (info.HasLoop) loopCount++;
            if (info.IsStereo) stereoCount++; else monoCount++;
        }

        _output.WriteLine($"Parsed {wavEntries.Count} WAV files: {monoCount} mono, {stereoCount} stereo, {loopCount} with sampler loop points.");
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    private static byte[] CreateSyntheticWav(
        ushort channels = 1,
        uint sampleRate = 22050,
        ushort bitsPerSample = 16,
        int sampleFrames = 100,
        (uint start, uint end)? loopPoints = null,
        bool oddChunkSize = false)
    {
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        uint byteRate = sampleRate * blockAlign;
        int dataBytes = sampleFrames * blockAlign;
        if (oddChunkSize) dataBytes += 1;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0); // placeholder for file size - 8
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // "fmt " chunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((ushort)1); // PCM
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        // "data" chunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (int i = 0; i < dataBytes; i++)
        {
            writer.Write((byte)(i % 256));
        }
        if ((dataBytes & 1) != 0)
        {
            writer.Write((byte)0);
        }

        // Optional "smpl" chunk
        if (loopPoints.HasValue)
        {
            writer.Write(Encoding.ASCII.GetBytes("smpl"));
            writer.Write(60); // 36 header + 24 loop record
            writer.Write(0);  // manufacturer
            writer.Write(0);  // product
            writer.Write((int)(1_000_000_000.0 / sampleRate)); // sample period in ns
            writer.Write(60); // midi note
            writer.Write(0);  // pitch fraction
            writer.Write(0);  // smpte format
            writer.Write(0);  // smpte offset
            writer.Write(1);  // num loops
            writer.Write(0);  // sampler data
            // loop record
            writer.Write(0);  // cue id
            writer.Write(0);  // type
            writer.Write(loopPoints.Value.start);
            writer.Write(loopPoints.Value.end);
            writer.Write(0);  // fraction
            writer.Write(0);  // play count
        }

        long totalLen = ms.Length;
        writer.Seek(4, SeekOrigin.Begin);
        writer.Write((uint)(totalLen - 8));

        return ms.ToArray();
    }
}
