using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;
using TUFReplayRenderer.Contracts;
using TUFReplayRenderer.Media;
using TUFReplayRenderer.Replay;

namespace TUFReplayRenderer.Tests;

internal static class ParserTests
{
  internal static void Run()
  {
    CultureInfo previous = CultureInfo.CurrentCulture;
    try
    {
      CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
      var inputs = ReadInputs("-2,A,1,0\n-2,A,0,1\n100,KeypadEnter,1,2\n");
      Assert(inputs.Length == 3 && inputs[0].Down && !inputs[1].Down && inputs[2].Key == "KeypadEnter", "input parsing");
      var hits = ReadHits("0,0,1.25,0.5,0,0,0,1.24,1.26,0,0,0,XPerfect\n");
      Assert(hits.Length == 1 && hits[0].Angle == 1.25 && hits[0].OverloadCounter == 0.5f, "invariant hit parsing and fractional overload");
      Reject(() => ReadInputs("0,A,1,2\n0,A,0,2\n"));
      Reject(() => ReadInputs("3,A,1,0\n2,A,0,1\n"));
      Reject(() => ReadInputs("0,A,true,0\n"));
      Reject(() => ReadInputs("0,65,1,0\n"));
      Reject(() => ReadHits("0,0,NaN,0,0,0,0,1,1,0,0,0,XPerfect\n"));
      Reject(() => ReadHits("0,0,1,0,0,0,0,1,1,0,0,0,\n"));
      Reject(() => ReadHits("0,0,1,0,0,0,0,1,1,0,0,0,9\n"));
      var orderedInputs = ReadInputs("0,A,1,0\n0,A,0,1\n1,B,1,2\n");
      var orderedHits = ReadHits("0,0,1,0,0,0,0,1,1,0,0,0,XPerfect\n1,1,1,0,0,0,0,1,1,0,0,0,Auto\n");
      var cursor = new ReplayEventCursor(orderedInputs, orderedHits);
      var ordering = new List<string>();
      cursor.AdvanceTo(0, value => ordering.Add("key" + value.Sequence), value => ordering.Add("hit" + value.FloorId));
      Assert(string.Join(",", ordering) == "key0,key1,hit0", "same-time ordering keeps every short tap");
      cursor.AdvanceTo(1, value => ordering.Add("key" + value.Sequence), value => ordering.Add("hit" + value.FloorId));
      Assert(cursor.AllHitsConsumed && cursor.InputsConsumed == 3, "all events consumed once");
      try { cursor.AdvanceTo(0, _ => { }, _ => { }); throw new Exception("Backwards seek accepted"); }
      catch (InvalidOperationException) { }
      CheckBundle();
      // Recorded microseconds follow conductor songposition, which already includes original pitch.
      var originalPitch = new RenderTimeline(1_000_000, 1.5, 6_000_000);
      Assert(originalPitch.ReplayToOutput(3_000_000) == 3_000_000, "original pitch is applied exactly once");
      Assert(originalPitch.ReplayToOutput(6_500_000) == 5_500_000, "clear tail returns to rate one");
      Assert(originalPitch.OutputToReplay(5_500_000) == 6_500_000, "clear tail inverse mapping");
      var doubleSpeed = new RenderTimeline(1_000_000, 3, 6_000_000);
      Assert(doubleSpeed.ReplayToOutput(3_000_000) == 2_000_000, "requested speed multiplies original pitch");
      Assert(doubleSpeed.OutputToReplay(500_000) == -1_500_000, "countdown is before recording zero");
    }
    finally { CultureInfo.CurrentCulture = previous; }
  }

  private static void CheckBundle()
  {
    string directory = Path.Combine(Path.GetTempPath(), "renderer-bundle-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
      File.WriteAllText(Path.Combine(directory, "inputs.csv"), RecordingCsvReader.InputsHeader + "\n0,A,1,0\n");
      File.WriteAllText(Path.Combine(directory, "hits.csv"), RecordingCsvReader.HitsHeader + "\n0,0,1,0.5,0,0,0,1,1,0,0,0,XPerfect\n");
      var manifest = new RecordingManifest { SchemaVersion = 1, RecordingId = "test", Level = new LevelManifest { Path = "level.adofai" },
        Replay = new ReplayManifest { GameplayStartSongPosition = -0.25, EffectivePitch = 1.5, GameInputOffsetMs = -30,
          NoFailMode = false, JudgmentSystem = "ModernClassic", WonTimeUs = 5, TerminalTimeUs = 10 }, InputsFile = "inputs.csv", HitsFile = "hits.csv" };
      string path = Path.Combine(directory, "manifest.json");
      File.WriteAllText(path, JsonConvert.SerializeObject(manifest));
      RecordingBundle bundle = RecordingBundle.Load(path);
      Assert(bundle.Manifest.Replay.EffectivePitch == 1.5 && bundle.Hits.Length == 1, "complete bundle");
      manifest.Replay.StartTile = 10;
      Reject(() => manifest.Validate());
      manifest.Replay.StartTile = 0;
      Reject(() => bundle.ResolveFile("../outside.csv"));
      Reject(() => bundle.ResolveFile(path));
      string level = Path.Combine(directory, "level.adofai");
      File.WriteAllText(level, "{\"angleData\":[0,180]}");
      using (var stream = File.OpenRead(level))
      using (var hash = SHA256.Create()) manifest.Level.FileSha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
      File.WriteAllText(path, JsonConvert.SerializeObject(manifest));
      RecordingBundle.Load(path).ValidateLevelHash();
      File.AppendAllText(level, "changed");
      Reject(() => RecordingBundle.Load(path).ValidateLevelHash());
      manifest.Level.FileSha256 = "bad";
      Reject(() => manifest.Validate());
      manifest.Level.FileSha256 = null;
      manifest.InputsFile = "../outside.csv";
      File.WriteAllText(path, JsonConvert.SerializeObject(manifest));
      Reject(() => RecordingBundle.Load(path));
      manifest.InputsFile = "inputs.csv";
      manifest.Replay.TerminalTimeUs = 0;
      manifest.Replay.WonTimeUs = null;
      File.WriteAllText(path, JsonConvert.SerializeObject(manifest));
      File.WriteAllText(Path.Combine(directory, "inputs.csv"), RecordingCsvReader.InputsHeader + "\n1,A,1,0\n");
      Reject(() => RecordingBundle.Load(path));
    }
    finally { Directory.Delete(directory, true); }
  }
  private static RecordedKeyEvent[] ReadInputs(string rows) => RecordingCsvReader.ReadInputs(new StringReader(RecordingCsvReader.InputsHeader + "\n" + rows));
  private static RecordedHitEvent[] ReadHits(string rows) => RecordingCsvReader.ReadHits(new StringReader(RecordingCsvReader.HitsHeader + "\n" + rows));
  private static void Reject(Action action)
  {
    try { action(); throw new Exception("Malformed recording accepted"); }
    catch (RecordingFormatException) { }
  }
  private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Failed: " + message); }
}
