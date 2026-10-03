using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TUFReplayRenderer.Contracts;

public sealed class RecordingManifest
{
  [JsonProperty("schemaVersion", Required = Required.Always)] public int SchemaVersion { get; set; }
  [JsonProperty("recordingId", Required = Required.Always)] public string RecordingId { get; set; }
  [JsonProperty("level", Required = Required.Always)] public LevelManifest Level { get; set; }
  [JsonProperty("replay", Required = Required.Always)] public ReplayManifest Replay { get; set; }
  [JsonProperty("inputsFile", Required = Required.Always)] public string InputsFile { get; set; }
  [JsonProperty("hitsFile", Required = Required.Always)] public string HitsFile { get; set; }
  [JsonProperty("media")] public JObject Media { get; set; }

  public void Validate()
  {
    if (SchemaVersion != 1) throw new RecordingFormatException("This recording bundle version is not supported.");
    if (string.IsNullOrWhiteSpace(RecordingId) || RecordingId.Length > 256)
      throw new RecordingFormatException("The recording ID is missing or too long.");
    if (Level == null || string.IsNullOrWhiteSpace(Level.Path)) throw new RecordingFormatException("The recording is missing its level file.");
    Level.Validate();
    if (Replay == null) throw new RecordingFormatException("The recording is missing its replay settings.");
    if (string.IsNullOrWhiteSpace(InputsFile) || string.IsNullOrWhiteSpace(HitsFile))
      throw new RecordingFormatException("The recording is missing its input or judgment file.");
    Replay.Validate();
  }
}

public sealed class LevelManifest
{
  [JsonProperty("path", Required = Required.Always)] public string Path { get; set; }
  [JsonProperty("fileSha256")] public string FileSha256 { get; set; }
  public void Validate()
  {
    if (FileSha256 == null) return;
    if (FileSha256.Length != 64) throw new RecordingFormatException("The recorded level fingerprint is invalid.");
    foreach (char value in FileSha256)
      if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F')))
        throw new RecordingFormatException("The recorded level fingerprint is invalid.");
  }
}

public sealed class ReplayManifest
{
  [JsonProperty("gameplayStartSongPosition", Required = Required.Always)] public double GameplayStartSongPosition { get; set; }
  [JsonProperty("effectivePitch", Required = Required.Always)] public double EffectivePitch { get; set; }
  [JsonProperty("gameInputOffsetMs", Required = Required.Always)] public double GameInputOffsetMs { get; set; }
  [JsonProperty("noFailMode", Required = Required.Always)] public bool NoFailMode { get; set; }
  [JsonProperty("judgmentSystem", Required = Required.Always)] public string JudgmentSystem { get; set; }
  [JsonProperty("judgmentDifficulty")] public string JudgmentDifficulty { get; set; }
  [JsonProperty("startTile")] public int StartTile { get; set; }
  [JsonProperty("wonTimeUs")] public long? WonTimeUs { get; set; }
  [JsonProperty("terminalTimeUs", Required = Required.Always)] public long TerminalTimeUs { get; set; }

  public void Validate()
  {
    if (StartTile != 0) throw new RecordingFormatException("Rendering currently supports recordings that start at tile zero. Record a full run from the beginning and render again.");
    if (!IsFinite(GameplayStartSongPosition) || !IsFinite(EffectivePitch) || EffectivePitch <= 0)
      throw new RecordingFormatException("The recording has an invalid playback speed or song origin.");
    if (!IsFinite(GameInputOffsetMs)) throw new RecordingFormatException("The recording has an invalid input offset.");
    if (string.IsNullOrWhiteSpace(JudgmentSystem) || JudgmentSystem.Length > 128)
      throw new RecordingFormatException("The recording is missing its judgment system.");
    if (TerminalTimeUs < 0 || (WonTimeUs.HasValue && (WonTimeUs.Value < 0 || WonTimeUs.Value > TerminalTimeUs)))
      throw new RecordingFormatException("The recording has an invalid end time.");
  }

  private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

public sealed class RecordingFormatException : Exception
{
  public RecordingFormatException(string message) : base(message) { }
  public RecordingFormatException(string message, Exception innerException) : base(message, innerException) { }
}
