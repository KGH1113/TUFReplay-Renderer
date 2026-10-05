using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using OrbitRender.Renderer;
using TUFReplayRenderer.Replay;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            Type owner = BuildOverlay();
            var component = (MonoBehaviour)Activator.CreateInstance(owner);
            UnityEngine.Object.Canvases = new[] { new Canvas { Components = new[] { component } } };
            var onEvent = (Func<long, long>)Delegate.CreateDelegate(typeof(Func<long, long>), component, owner.GetMethod("OnEvent"));
            var update = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), component, owner.GetMethod("UpdateAge"));
            var scaledDelta = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), component, owner.GetMethod("ReadScaledDelta"));
            var unscaledDelta = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), component, owner.GetMethod("ReadUnscaledDelta"));
            var utcNow = (Func<DateTime>)Delegate.CreateDelegate(typeof(Func<DateTime>), component, owner.GetMethod("ReadUtcNow"));
            var localNow = (Func<DateTime>)Delegate.CreateDelegate(typeof(Func<DateTime>), component, owner.GetMethod("ReadLocalNow"));
            var elapsed = (Func<TimeSpan>)Delegate.CreateDelegate(typeof(Func<TimeSpan>), component, owner.GetMethod("ReadElapsed"));
            var elapsedTicks = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), component, owner.GetMethod("ReadElapsedTicks"));
            var elapsedMilliseconds = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), component, owner.GetMethod("ReadElapsedMilliseconds"));
            var watch = (Stopwatch)owner.GetField("Watch").GetValue(null);
            // Warm the listener and UI caller, and retain their delegates as a
            // native event hub and Unity do throughout consecutive renders.
            onEvent(DateTime.UtcNow.Ticks);
            update();
            for (int session = 0; session < 4; session++)
            {
                var driver = new RecordedReplayDriver();
                RendererController.Instance.Clock.Fps = session == 0 ? 60 : session == 2 ? 240 : 120;
                using (OptionalModClock.Begin(driver, _ => { }))
                {
                    onEvent(OptionalModClock.Clock.UtcNow.Ticks);
                    driver.CurrentVideoTimeUs = 100_000;
                    long age = update();
                    if (Math.Abs(age / (double)TimeSpan.TicksPerSecond - .1) > .001)
                        throw new Exception("Cached Mono listener/UI delegates lost the 100ms video age in session " + session + ": " + age);
                    long nativeTime = watch.ElapsedTicks;
                    Thread.Sleep(25);
                    if (update() != age) throw new Exception("Frozen video time advanced with native wall time in session " + session);
                    if (watch.ElapsedTicks <= nativeTime) throw new Exception("The host's uninstrumented stopwatch was frozen.");
                    if (localNow().ToUniversalTime() != utcNow()) throw new Exception("Local and UTC video clocks use different origins.");
                    float expectedDelta = 1f / RendererController.Instance.Clock.Fps;
                    if (Math.Abs(scaledDelta() - expectedDelta) > 1e-6 || Math.Abs(unscaledDelta() - expectedDelta) > 1e-6)
                        throw new Exception("Overlay deltas did not follow the selected simulation FPS.");
                    RendererController.OverlayRefreshOnly = true;
                    if (scaledDelta() != 0 || unscaledDelta() != 0) throw new Exception("A UI refresh advanced animation time.");
                    RendererController.OverlayRefreshOnly = false;
                    // The third job exits early, exercising reuse after cancel.
                    long terminal = session == 2 ? 300_000 : 18_000_000;
                    driver.CurrentVideoTimeUs = terminal;
                    if (Math.Abs(update() / (double)TimeSpan.TicksPerSecond - terminal / 1e6) > .001)
                        throw new Exception("The persistent UI caller lost its video clock in session " + session);
                }
                // Native processing between jobs must read the unmodified watch.
                onEvent(DateTime.UtcNow.Ticks);
                Thread.Sleep(25);
                if (update() <= 0) throw new Exception("Live input did not resume between jobs.");
                if (scaledDelta() != 0 || unscaledDelta() != .02f)
                    throw new Exception("Idle patches changed the native distinction between paused and unscaled delta time.");
                watch.Stop();
                if (elapsed() != watch.Elapsed || elapsedTicks() != watch.ElapsedTicks || elapsedMilliseconds() != watch.ElapsedMilliseconds)
                    throw new Exception("Idle patches converted the original stopwatch's values or units.");
                watch.Start();
                if (session == 0)
                {
                    // Another Harmony owner can rebuild a caller while idle.
                    // The retained transpiler must still preserve video time
                    // when the next job starts, despite there being no active clock now.
                    new Harmony("fixture.other.owner").Patch(owner.GetMethod("UpdateAge"),
                        postfix: new HarmonyMethod(typeof(Program), nameof(Observer)));
                }
                Console.WriteLine("PASS: Mono cached listener/UI delegates, frozen frame and native inter-job time, session " + session);
            }
            OptionalModClock.Shutdown();
            if (Harmony.GetPatchInfo(owner.GetMethod("UpdateAge"))?.Owners.Contains("KGH1113.TUFReplayRenderer.OptionalModClock") == true)
                throw new Exception("Mod shutdown retained its own clock patches.");
            if (scaledDelta() != 0 || unscaledDelta() != .02f) throw new Exception("Native deltas were not restored on shutdown.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Observer() { }

    private static Type BuildOverlay()
    {
        // A foreign assembly is required: the real discovery intentionally
        // excludes the renderer/host assembly from overlay rewriting.
        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("PersistentOverlayClockFixture"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Overlay").DefineType("PersistentCanvas", TypeAttributes.Public, typeof(MonoBehaviour));
        FieldBuilder watch = type.DefineField("Watch", typeof(Stopwatch), FieldAttributes.Public | FieldAttributes.Static);
        FieldBuilder offset = type.DefineField("Offset", typeof(long), FieldAttributes.Private);
        FieldBuilder birth = type.DefineField("Birth", typeof(long), FieldAttributes.Private);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        ILGenerator init = type.DefineTypeInitializer().GetILGenerator();
        init.Emit(OpCodes.Call, typeof(Stopwatch).GetMethod(nameof(Stopwatch.StartNew)));
        init.Emit(OpCodes.Stsfld, watch); init.Emit(OpCodes.Ret);
        var getter = type.DefineMethod("get_Current", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName, typeof(long), Type.EmptyTypes);
        getter.SetImplementationFlags(MethodImplAttributes.AggressiveInlining);
        ILGenerator il = getter.GetILGenerator();
        LocalBuilder elapsed = il.DeclareLocal(typeof(TimeSpan));
        il.Emit(OpCodes.Ldsfld, watch); il.Emit(OpCodes.Callvirt, typeof(Stopwatch).GetProperty(nameof(Stopwatch.Elapsed)).GetGetMethod());
        il.Emit(OpCodes.Stloc, elapsed); il.Emit(OpCodes.Ldloca, elapsed); il.Emit(OpCodes.Call, typeof(TimeSpan).GetProperty(nameof(TimeSpan.Ticks)).GetGetMethod()); il.Emit(OpCodes.Ret);
        type.DefineProperty("Current", PropertyAttributes.None, typeof(long), Type.EmptyTypes).SetGetMethod(getter);
        var normalize = type.DefineMethod("Normalize", MethodAttributes.Private, typeof(long), new[] { typeof(long) });
        normalize.SetImplementationFlags(MethodImplAttributes.AggressiveInlining);
        il = normalize.GetILGenerator();
        LocalBuilder current = il.DeclareLocal(typeof(long)), value = il.DeclareLocal(typeof(long));
        Label past = il.DefineLabel();
        il.Emit(OpCodes.Call, getter); il.Emit(OpCodes.Stloc, current);
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, offset); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Ldloc, value); il.Emit(OpCodes.Ldloc, current); il.Emit(OpCodes.Blt, past);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloc, current); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stfld, offset);
        il.Emit(OpCodes.Ldloc, current); il.Emit(OpCodes.Ret); il.MarkLabel(past); il.Emit(OpCodes.Ldloc, value); il.Emit(OpCodes.Ret);
        var input = type.DefineMethod("OnEvent", MethodAttributes.Public, typeof(long), new[] { typeof(long) });
        input.SetImplementationFlags(MethodImplAttributes.NoInlining);
        il = input.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, normalize);
        il.Emit(OpCodes.Stfld, birth); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, birth); il.Emit(OpCodes.Ret);
        var update = type.DefineMethod("UpdateAge", MethodAttributes.Public, typeof(long), Type.EmptyTypes);
        update.SetImplementationFlags(MethodImplAttributes.NoInlining);
        il = update.GetILGenerator(); il.Emit(OpCodes.Call, getter); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, birth); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Ret);
        foreach (var pair in new[] { ("ReadScaledDelta", "deltaTime"), ("ReadUnscaledDelta", "unscaledDeltaTime") })
        {
            var delta = type.DefineMethod(pair.Item1, MethodAttributes.Public, typeof(float), Type.EmptyTypes);
            delta.SetImplementationFlags(MethodImplAttributes.NoInlining);
            il = delta.GetILGenerator(); il.Emit(OpCodes.Call, typeof(Time).GetProperty(pair.Item2).GetGetMethod()); il.Emit(OpCodes.Ret);
        }
        foreach (var pair in new[] { ("ReadUtcNow", "UtcNow"), ("ReadLocalNow", "Now") })
        {
            var now = type.DefineMethod(pair.Item1, MethodAttributes.Public, typeof(DateTime), Type.EmptyTypes);
            now.SetImplementationFlags(MethodImplAttributes.NoInlining);
            il = now.GetILGenerator(); il.Emit(OpCodes.Call, typeof(DateTime).GetProperty(pair.Item2).GetGetMethod()); il.Emit(OpCodes.Ret);
        }
        foreach (string property in new[] { "Elapsed", "ElapsedTicks", "ElapsedMilliseconds" })
        {
            MethodInfo read = typeof(Stopwatch).GetProperty(property).GetGetMethod();
            var elapsedRead = type.DefineMethod("Read" + property, MethodAttributes.Public, read.ReturnType, Type.EmptyTypes);
            elapsedRead.SetImplementationFlags(MethodImplAttributes.NoInlining);
            il = elapsedRead.GetILGenerator(); il.Emit(OpCodes.Ldsfld, watch); il.Emit(OpCodes.Callvirt, read); il.Emit(OpCodes.Ret);
        }
        return type.CreateType();
    }
}
