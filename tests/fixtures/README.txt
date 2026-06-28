Test fixtures
=============

There are no committed binary audio fixtures in this directory.

The demux tests synthesize their WebM-Opus input at runtime — see
tests/TestWebmAudio.cs. A 440 Hz sine tone is encoded with Concentus (the
same Opus stack the real codec.opus plugin decodes with) and then muxed into
a hand-written Matroska container. A synthesized clip is:

  * deterministic   — the same bytes every run, so tests stay hermetic;
  * license-free    — it's a generated sine wave, not third-party audio,
                      so nothing here needs an attribution / license note;
  * self-validating — the packets are produced by the same Concentus stack
                      the borrowed decoder uses, so a valid encode proves a
                      valid decode target;
  * self-contained  — the muxer lives in the test project, so the fixtures
                      depend on no third-party Matroska writer.

The generated clip carries one A_OPUS track (track 1), 20 ms SimpleBlocks
split across two Clusters, an Info Duration, and (optionally) a Cues index so
the seek path can be exercised.

If you ever need a real-world fixture (e.g. to reproduce a decoder bug
against a specific encoder's output — a Discord voice message, a browser
MediaRecorder capture), drop the .webm file here and note its source +
license in this file.
