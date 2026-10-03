using System;

namespace TUFReplayRenderer.Tests;

internal static class Program
{
  private static int Main()
  {
    try
    {
      ParserTests.Run();
      MediaTests.Run();
      Console.WriteLine("Renderer parser, timeline and media tests passed.");
      return 0;
    }
    catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
  }
}
