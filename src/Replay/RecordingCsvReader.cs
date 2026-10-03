using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TUFReplayRenderer.Contracts;

namespace TUFReplayRenderer.Replay;

public static class RecordingCsvReader
{
  public const string InputsHeader = "timeUs,key,down,sequence";
  public const string HitsHeader = "timeUs,floorId,angle,overloadCounter,noFailHit,isAuto,nextFloorAuto,cachedAngle,targetExitAngle,midspinInfiniteMargin,rdcAuto,freeRoamSection,margin";
  private const int MaximumEvents = 5_000_000;

  public static RecordedKeyEvent[] ReadInputs(TextReader reader)
  {
    ReadHeader(reader, InputsHeader);
    var result = new List<RecordedKeyEvent>();
    var names = new Dictionary<string, string>(StringComparer.Ordinal);
    int lineNumber = 1;
    string line;
    while ((line = reader.ReadLine()) != null)
    {
      lineNumber++;
      if (string.IsNullOrWhiteSpace(line)) continue;
      string[] row = Split(line, 4, lineNumber);
      long timeUs = Integer(row[0], lineNumber);
      if (!IsSymbol(row[1])) Fail(lineNumber, "The key must be a symbolic Unity key name.");
      if (!names.TryGetValue(row[1], out string key)) names.Add(row[1], key = row[1]);
      bool down = Flag(row[2], lineNumber);
      long sequence = Integer(row[3], lineNumber);
      if (sequence < 0) Fail(lineNumber, "The input sequence must be non-negative.");
      if (result.Count > 0)
      {
        RecordedKeyEvent previous = result[result.Count - 1];
        if (timeUs < previous.TimeUs || (timeUs == previous.TimeUs && sequence <= previous.Sequence))
          Fail(lineNumber, "Inputs must be ordered by time and a unique sequence at each timestamp.");
      }
      if (result.Count == MaximumEvents) Fail(lineNumber, "The recording has too many input events.");
      result.Add(new RecordedKeyEvent(timeUs, key, down, sequence));
    }
    return result.ToArray();
  }

  public static RecordedHitEvent[] ReadHits(TextReader reader)
  {
    ReadHeader(reader, HitsHeader);
    var result = new List<RecordedHitEvent>();
    int lineNumber = 1;
    string line;
    while ((line = reader.ReadLine()) != null)
    {
      lineNumber++;
      if (string.IsNullOrWhiteSpace(line)) continue;
      string[] row = Split(line, 13, lineNumber);
      long timeUs = Integer(row[0], lineNumber);
      int floorId = SmallInteger(row[1], lineNumber);
      double overload = Number(row[3], lineNumber);
      int freeRoamSection = SmallInteger(row[11], lineNumber);
      if (floorId < 0 || freeRoamSection < 0) Fail(lineNumber, "The recorded tile or freeroam section is invalid.");
      if (overload < 0 || overload > float.MaxValue) Fail(lineNumber, "The overload counter is invalid.");
      if (!IsSymbol(row[12])) Fail(lineNumber, "This recording is missing a resolved judgment. Export it again from a compatible recorder.");
      if (result.Count > 0 && timeUs < result[result.Count - 1].TimeUs) Fail(lineNumber, "Judgment events must be ordered by time.");
      if (result.Count == MaximumEvents) Fail(lineNumber, "The recording has too many judgment events.");
      result.Add(new RecordedHitEvent(timeUs, floorId, Number(row[2], lineNumber), (float)overload,
        Flag(row[4], lineNumber), Flag(row[5], lineNumber), Flag(row[6], lineNumber), Number(row[7], lineNumber),
        Number(row[8], lineNumber), Flag(row[9], lineNumber), Flag(row[10], lineNumber), freeRoamSection, row[12]));
    }
    return result.ToArray();
  }

  private static void ReadHeader(TextReader reader, string expected)
  {
    if (reader == null) throw new ArgumentNullException(nameof(reader));
    string header = reader.ReadLine()?.TrimStart('\uFEFF');
    if (!string.Equals(header, expected, StringComparison.Ordinal))
      throw new RecordingFormatException("The recording CSV header does not match bundle version 1.");
  }
  private static string[] Split(string line, int columns, int lineNumber)
  {
    if (line.Length > 4096) Fail(lineNumber, "The recording row is too long.");
    string[] row = line.Split(',');
    if (row.Length != columns) Fail(lineNumber, "The recording row has the wrong number of columns.");
    return row;
  }
  private static long Integer(string text, int lineNumber)
  {
    if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long result))
      Fail(lineNumber, "The recording contains an invalid integer.");
    return result;
  }
  private static int SmallInteger(string text, int lineNumber)
  {
    long result = Integer(text, lineNumber);
    if (result < int.MinValue || result > int.MaxValue) Fail(lineNumber, "The recording integer is out of range.");
    return (int)result;
  }
  private static double Number(string text, int lineNumber)
  {
    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
      || double.IsNaN(result) || double.IsInfinity(result)) Fail(lineNumber, "The recording contains an invalid angle or counter.");
    return result;
  }
  private static bool Flag(string text, int lineNumber)
  {
    if (text == "1") return true;
    if (text == "0") return false;
    Fail(lineNumber, "A recording flag must be 0 or 1.");
    return false;
  }
  private static bool IsSymbol(string value)
  {
    if (string.IsNullOrEmpty(value) || value.Length > 64 || !char.IsLetter(value[0])) return false;
    foreach (char character in value) if (!char.IsLetterOrDigit(character) && character != '_') return false;
    return true;
  }
  private static void Fail(int lineNumber, string message) => throw new RecordingFormatException($"{message} (CSV line {lineNumber})");
}
