using System.Collections;
using HarmonyLib;

namespace OrbitRender.Patches
{
    // UMM's Harmony exposes List<mscorlib.Label>. Access the collection through
    // IList so netstandard can preserve the exact branch-label objects on Mono.
    internal static class InstructionMetadata
    {
        internal static void MoveLabels(CodeInstruction source, CodeInstruction destination)
        {
            var field = AccessTools.Field(typeof(CodeInstruction), "labels");
            var from = (IList)field.GetValue(source);
            var to = (IList)field.GetValue(destination);
            foreach (object label in from) to.Add(label);
            from.Clear();
        }
    }
}
