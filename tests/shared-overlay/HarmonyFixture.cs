using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// The installed Mono detour runtime cannot execute on this .NET 10 macOS host. This shim exercises
// the production adapter's prefix boundaries against an explicit managed worker fixture instead.
namespace HarmonyLib;

public sealed class HarmonyMethod
{
    public readonly MethodInfo method;
    public int priority;
    public HarmonyMethod(Type type, string name) { method = AccessTools.Method(type, name); }
}
public sealed class Harmony
{
    public string Id { get; }
    public Harmony(string id) { Id = id; }
    public void Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
    { if (prefix != null) FixturePatches.Add(Id, original, prefix.method); }
    public void UnpatchAll(string id) { if (FixturePatches.FailUnpatch) throw new InvalidOperationException("Fixture cleanup failure"); FixturePatches.Remove(id); }
    public static Patches GetPatchInfo(MethodBase original) => null;
}
public sealed class Patches { public System.Collections.Generic.List<string> Owners { get; } = new(); }
public sealed class CodeInstruction { public OpCode opcode; public object operand; }
public static class AccessTools
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static FieldInfo Field(Type type, string name) => type.GetField(name, All);
    public static PropertyInfo Property(Type type, string name) => type.GetProperty(name, All);
    public static MethodInfo PropertyGetter(Type type, string name) => Property(type, name)?.GetGetMethod(true);
    public static MethodInfo Method(Type type, string name, Type[] parameters = null) => parameters == null ? type.GetMethods(All).Single(m => m.Name == name) : type.GetMethod(name, All, null, parameters, null);
}
internal static class FixturePatches
{
    internal static bool FailUnpatch;
    private static readonly ConcurrentDictionary<string, (string owner, MethodInfo prefix)> patches = new();
    internal static void Add(string owner, MethodBase method, MethodInfo prefix) => patches[method.DeclaringType.FullName + "." + method.Name] = (owner, prefix);
    internal static void Remove(string owner) { foreach (var item in patches) if (item.Value.owner == owner) patches.TryRemove(item.Key, out _); }
    internal static bool Allow(Type type, string name, object instance, object[] inputs, out object result)
    {
        result = null;
        if (!patches.TryGetValue(type.FullName + "." + name, out var patch)) return true;
        var parameters = patch.prefix.GetParameters();
        object[] values = parameters.Select(p => p.Name == "__instance" ? instance : p.Name == "__0" ? inputs[0] : p.Name == "__result" ? Activator.CreateInstance(p.ParameterType.GetElementType()) : throw new Exception("Unknown fixture prefix parameter " + p.Name)).ToArray();
        bool allowed = (bool)patch.prefix.Invoke(null, values);
        for (int index = 0; index < parameters.Length; index++) if (parameters[index].Name == "__result") result = values[index];
        return allowed;
    }
}

public static class Priority { public const int First = 800; }
