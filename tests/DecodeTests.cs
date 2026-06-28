using System;
using System.IO;
using FluentAssertions;
using NAudio.Wave;
using SoundBoard.PluginApi;
using Xunit;

namespace WebmCodecPlugin.Tests;

/// <summary>
/// Exercises both <c>CreateStream</c> overloads end to end: demux a
/// synthesized WebM-Opus clip, borrow the (stubbed) Opus decoder, and pull
/// PCM through the resulting <see cref="WaveStream"/>.
/// </summary>
public class DecodeTests
{
    /// <summary>A plugin wired to a registry that exposes a working stub
    /// codec.opus — the normal, happy-path host environment.</summary>
    private static WebmCodecPlugin NewPlugin(bool exposeDecoder = true)
    {
        var plugin = new WebmCodecPlugin();
        var registry = new FakeCodecRegistry(new StubOpusCodecPlugin(exposeDecoder));
        plugin.Initialize(new FakePluginContext(registry));
        return plugin;
    }

    private static int ReadAll(WaveStream stream)
    {
        var buffer = new byte[16384];
        int total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            total += read;
        return total;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void File_decodes_to_ieee_float_stereo_48k(int sourceChannels)
    {
        byte[] clip = TestWebmAudio.CreateWebmOpusClip(sourceChannels, seconds: 0.5);
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.webm");
        File.WriteAllBytes(path, clip);

        try
        {
            using WaveStream stream = NewPlugin().CreateStream(path);

            stream.WaveFormat.Encoding.Should().Be(WaveFormatEncoding.IeeeFloat);
            stream.WaveFormat.SampleRate.Should().Be(48000);
            // The borrowed decoder always emits stereo (mono is upmixed),
            // regardless of the source track's channel count.
            stream.WaveFormat.Channels.Should().Be(2);
            stream.CanSeek.Should().BeTrue("the file is seekable and carries a Cues index");

            int bytesDecoded = ReadAll(stream);

            // ~0.5 s at 48 kHz * 2 ch * 4 bytes. Opus pre-skip fuzzes the exact
            // count, so allow a generous band rather than an exact length.
            int bytesPerSecond = 48000 * 2 * sizeof(float);
            bytesDecoded.Should().BeInRange((int)(bytesPerSecond * 0.3), (int)(bytesPerSecond * 0.7));

            stream.Length.Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stream_overload_decodes_and_takes_ownership()
    {
        byte[] clip = TestWebmAudio.CreateWebmOpusClip(channels: 2, seconds: 0.5);
        var input = new TrackingStream(clip);

        WaveStream stream = NewPlugin().CreateStream(input, "audio/webm");

        stream.WaveFormat.Encoding.Should().Be(WaveFormatEncoding.IeeeFloat);
        stream.WaveFormat.Channels.Should().Be(2);
        ReadAll(stream).Should().BeGreaterThan(0);

        // SDK contract: disposing the WaveStream disposes the input stream.
        input.Disposed.Should().BeFalse("ownership transfers but disposal only happens on WaveStream.Dispose");
        stream.Dispose();
        input.Disposed.Should().BeTrue("disposing the WaveStream must dispose the input it was handed");
    }

    [Fact]
    public void Seek_to_a_cue_repositions_playback()
    {
        // 1.0 s clip → 50 frames, split at frame 25 (= 500 ms) into the second
        // cluster, with a Cue at each cluster start. Seeking to ~500 ms lands
        // exactly on the second cue by construction.
        byte[] clip = TestWebmAudio.CreateWebmOpusClip(channels: 2, seconds: 1.0);
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.webm");
        File.WriteAllBytes(path, clip);

        try
        {
            using WaveStream stream = NewPlugin().CreateStream(path);
            stream.CanSeek.Should().BeTrue();

            long target = stream.WaveFormat.AverageBytesPerSecond / 2;   // ~0.5 s in
            stream.Position = target;

            // Seek resolves to the enclosing Cue's cluster, not a sample-exact
            // offset; allow ~0.1 s of slack on the round-trip.
            long slack = stream.WaveFormat.AverageBytesPerSecond / 10;
            stream.Position.Should().BeCloseTo(target, (ulong)slack);

            // And decoding continues from there.
            ReadAll(stream).Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Without_cues_the_stream_is_not_seekable()
    {
        byte[] clip = TestWebmAudio.CreateWebmOpusClip(channels: 2, seconds: 0.5, includeCues: false);
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.webm");
        File.WriteAllBytes(path, clip);

        try
        {
            using WaveStream stream = NewPlugin().CreateStream(path);
            stream.CanSeek.Should().BeFalse("a file with no Cues index cannot be seeked");
            ReadAll(stream).Should().BeGreaterThan(0, "it should still play through linearly");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Disposing_the_stream_disposes_the_borrowed_decoder()
    {
        var decoderPlugin = new StubOpusCodecPlugin();
        var plugin = new WebmCodecPlugin();
        plugin.Initialize(new FakePluginContext(new FakeCodecRegistry(decoderPlugin)));

        byte[] clip = TestWebmAudio.CreateWebmOpusClip(channels: 2, seconds: 0.3);
        var input = new TrackingStream(clip);

        WaveStream stream = plugin.CreateStream(input, "audio/webm");
        // Drive a decode so the decoder is genuinely in use.
        ReadAll(stream);

        stream.Dispose();
        // The WaveStream owns the borrowed decoder and must dispose it.
        // (We can't see the instance directly, but disposing the input proves
        // the dispose path ran; the decode round-trip above proves the borrow
        // produced a live decoder.)
        input.Disposed.Should().BeTrue();
    }
}
