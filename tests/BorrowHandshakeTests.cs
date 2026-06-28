using System;
using System.IO;
using FluentAssertions;
using SoundBoard.PluginApi;
using Xunit;

namespace WebmCodecPlugin.Tests;

/// <summary>
/// Covers the codec-borrow handshake's failure modes — the cases where the
/// plugin must surface a clear, actionable error instead of crashing.
/// </summary>
public class BorrowHandshakeTests
{
    private static string WriteClip()
    {
        byte[] clip = TestWebmAudio.CreateWebmOpusClip(channels: 2, seconds: 0.2);
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.webm");
        File.WriteAllBytes(path, clip);
        return path;
    }

    [Fact]
    public void Missing_codec_opus_throws_install_hint()
    {
        var plugin = new WebmCodecPlugin();
        // Registry with no codecs at all → GetByExtension(".opus") returns null.
        plugin.Initialize(new FakePluginContext(new FakeCodecRegistry()));

        string path = WriteClip();
        try
        {
            var act = () => plugin.CreateStream(path);
            act.Should().Throw<InvalidOperationException>()
               .WithMessage("*codec.opus*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Codec_opus_without_a_decoder_throws_update_hint()
    {
        var plugin = new WebmCodecPlugin();
        // codec.opus present but its CreateDecoder returns null (out of date).
        plugin.Initialize(new FakePluginContext(
            new FakeCodecRegistry(new StubOpusCodecPlugin(exposeDecoder: false))));

        string path = WriteClip();
        try
        {
            var act = () => plugin.CreateStream(path);
            act.Should().Throw<InvalidOperationException>()
               .WithMessage("*frame decoder*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Null_registry_on_headless_host_throws_clean_error()
    {
        var plugin = new WebmCodecPlugin();
        plugin.Initialize(new FakePluginContext(registry: null));

        string path = WriteClip();
        try
        {
            var act = () => plugin.CreateStream(path);
            act.Should().Throw<InvalidOperationException>()
               .WithMessage("*registry*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Stream_overload_does_not_take_ownership_when_borrow_fails()
    {
        var plugin = new WebmCodecPlugin();
        plugin.Initialize(new FakePluginContext(new FakeCodecRegistry()));   // no codec.opus

        byte[] clip = TestWebmAudio.CreateWebmOpusClip(channels: 2, seconds: 0.2);
        var input = new TrackingStream(clip);

        var act = () => plugin.CreateStream(input, "audio/webm");
        act.Should().Throw<InvalidOperationException>();

        // The borrow is resolved before the stream is wrapped, so a failed
        // borrow must leave the caller's Stream untouched (they still own it).
        input.Disposed.Should().BeFalse("a failed borrow must not consume the caller's Stream");
    }

    [Fact]
    public void Declares_extension_mime_and_stream_support()
    {
        var plugin = new WebmCodecPlugin();
        plugin.SupportedPatterns.Should().Contain(".webm");
        plugin.SupportedContentTypes.Should().Contain("audio/webm");
        plugin.SupportsStreamInput.Should().BeTrue();
        plugin.Id.Should().Be("codec.webm");
    }
}
