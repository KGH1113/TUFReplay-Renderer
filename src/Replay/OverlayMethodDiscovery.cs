using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TUFReplayRenderer.Replay;

// Start at actual Canvas scripts and shared input consumers. Follow their managed
// calls, delegates and state machines; a clock read elsewhere in a mod is not a root.
internal static class OverlayMethodDiscovery
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    internal static MethodInfo[] Discover(IEnumerable<Type> components, Type inputEvent)
    {
        Type[] roots = components.Distinct().ToArray();
        var assemblies = new HashSet<Assembly>(roots.Select(t => t.Assembly));
        var pending = new Queue<MethodBase>();
        var seen = new HashSet<MethodBase>();
        foreach (Type type in roots) Seed(type);
        // Input listeners often own long-lived workers outside the Canvas hierarchy.
        // Their standard event parameter identifies the consumer without mod names.
        foreach (Assembly assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = error.Types.Where(t => t != null).ToArray(); }
            foreach (Type type in types)
                if (type.GetMethods(Declared).Any(m => m.GetParameters().Any(p => p.ParameterType == inputEvent))) Seed(type);
        }
        while (pending.Count != 0)
        {
            MethodBase method = pending.Dequeue();
            if (method.ContainsGenericParameters || method.IsAbstract || method.GetMethodBody() == null || !seen.Add(method)) continue;
            foreach (MemberInfo member in ManagedInstructionReader.Members(method))
                if (member is MethodBase called && assemblies.Contains(called.DeclaringType.Assembly)) pending.Enqueue(called);
            foreach (StateMachineAttribute state in method.GetCustomAttributes(typeof(StateMachineAttribute), false))
                Seed(state.StateMachineType);
        }
        return seen.OfType<MethodInfo>().ToArray();

        void Seed(Type type)
        {
            foreach (MethodInfo method in type.GetMethods(Declared)) pending.Enqueue(method);
            foreach (ConstructorInfo constructor in type.GetConstructors(Declared)) pending.Enqueue(constructor);
        }
    }
}
