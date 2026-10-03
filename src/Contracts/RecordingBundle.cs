using System;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;
using TUFReplayRenderer.Replay;

namespace TUFReplayRenderer.Contracts;

public sealed class RecordingBundle
{
  private RecordingBundle(string path, RecordingManifest manifest, RecordedKeyEvent[] inputs, RecordedHitEvent[] hits)
  { ManifestPath = path; Manifest = manifest; Inputs = inputs; Hits = hits; }
  public string ManifestPath { get; }
  public string DirectoryPath => Path.GetDirectoryName(ManifestPath);
  public RecordingManifest Manifest { get; }
  public RecordedKeyEvent[] Inputs { get; }
  public RecordedHitEvent[] Hits { get; }

  public static RecordingBundle Load(string manifestPath)
  {
    if (string.IsNullOrWhiteSpace(manifestPath)) throw new ArgumentException("A recording manifest path is required.", nameof(manifestPath));
    string fullPath = Path.GetFullPath(manifestPath);
    if (new FileInfo(fullPath).Length > 1_048_576) throw new RecordingFormatException("The recording manifest is too large.");
    RecordingManifest manifest;
    try { manifest = JsonConvert.DeserializeObject<RecordingManifest>(File.ReadAllText(fullPath)); }
    catch (JsonException exception) { throw new RecordingFormatException("The recording manifest could not be read. Export the recording again.", exception); }
    if (manifest == null) throw new RecordingFormatException("The recording manifest is empty.");
    manifest.Validate();
    string directory = Path.GetDirectoryName(fullPath);
    RecordedKeyEvent[] inputs;
    RecordedHitEvent[] hits;
    using (var reader = new StreamReader(ResolveBundleFile(directory, manifest.InputsFile))) inputs = RecordingCsvReader.ReadInputs(reader);
    using (var reader = new StreamReader(ResolveBundleFile(directory, manifest.HitsFile))) hits = RecordingCsvReader.ReadHits(reader);
    if ((inputs.Length > 0 && inputs[inputs.Length - 1].TimeUs > manifest.Replay.TerminalTimeUs)
      || (hits.Length > 0 && hits[hits.Length - 1].TimeUs > manifest.Replay.TerminalTimeUs))
      throw new RecordingFormatException("The recording contains events after its end time.");
    return new RecordingBundle(fullPath, manifest, inputs, hits);
  }
  public string ResolveFile(string relativePath) => ResolveBundleFile(DirectoryPath, relativePath);
  public string ResolveLevelPath() => Path.IsPathRooted(Manifest.Level.Path) ? Path.GetFullPath(Manifest.Level.Path) : ResolveFile(Manifest.Level.Path);
  public void ValidateLevelHash()
  {
    if (Manifest.Level.FileSha256 == null) return;
    using var stream = File.OpenRead(ResolveLevelPath());
    using var hash = SHA256.Create();
    string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    if (!string.Equals(actual, Manifest.Level.FileSha256, StringComparison.OrdinalIgnoreCase))
      throw new RecordingFormatException("The level has changed since this recording was made. Restore the recorded level version and render again.");
  }
  private static string ResolveBundleFile(string directory, string relativePath)
  {
    if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
      throw new RecordingFormatException("A recording data file must be inside its bundle folder.");
    string path = Path.GetFullPath(Path.Combine(directory, relativePath));
    string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new RecordingFormatException("A recording data file points outside its bundle folder.");
    return path;
  }
}
