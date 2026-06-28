# gmsb-codec-webm

WebM (`.webm`) playback codec plugin for
[Game Master Sound Board](https://github.com/DevinSanders/game-master-soundboard).

Adds `.webm` playback by demuxing the Matroska/EBML container in
pure-managed C# (via [NEbml](https://github.com/oleg-st/NEbml)) and
borrowing the Opus frame decoder from the
[`codec.opus`](https://github.com/DevinSanders/gmsb-codec-opus) plugin
through the host's codec registry — so no Opus library is bundled here.

WebM audio tracks in the wild are almost always Opus (Discord voice
messages, browser `MediaRecorder` recordings, WebRTC dumps, YouTube
audio-only streams). This plugin handles those.

## Required companion plugin

`codec.opus` must also be installed. This plugin is a Matroska *demuxer*
— it pulls Opus packets out of the WebM container and asks `codec.opus`
to turn them into PCM. Without `codec.opus` present, `.webm` playback
fails with a clear "codec.opus required" message when you try to play a
file.

## Install

**Paid plugin.** The source is open here for reference, but the
pre-built binary is distributed pay-what-you-want on itch.io (release
URL TBD — first release will register a slug under
`dsand64.itch.io`).

Download the `.zip` from that page and drop it onto **Settings → Plugin
Manager** in Game Master Sound Board. Restart when prompted, then
enable it under **Settings → Plugins**.

## Build

Requires .NET 10 SDK. `SoundBoard.PluginApi` is restored from NuGet, so
no sibling checkout is required.

```powershell
dotnet build src/WebmCodecPlugin.csproj
pwsh scripts/package.ps1
# → dist/github.DevinSanders-codec.webm-<version>.zip
```

Released versions are built by CI from a `v*` git tag — the tag's
SemVer (stripped of the leading `v`) is stamped into the manifest, the
assembly metadata, and the zip filename, so the release artifact's
version always matches its tag.

## Plugin manifest

| Field     | Value                       |
|-----------|-----------------------------|
| publisher | `github.DevinSanders`       |
| id        | `codec.webm`                |
| entryDll  | `WebmCodecPlugin.dll`       |
| isTheme   | `false`                     |

## License

Released under the [MIT License](LICENSE).

Third-party components used by this plugin:

- NEbml (MIT) for pure-managed EBML/Matroska container parsing.
- Opus decoding is delegated to the installed `codec.opus` plugin via
  the host's codec registry — no Opus library shipped here.
