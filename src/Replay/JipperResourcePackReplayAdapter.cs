using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace TUFReplayRenderer.Replay;

// The native viewer keeps its own binding, rain and drawing code. Only its input and persistence
// boundaries change for an export; the desktop input worker remains parked on its existing queue.
internal sealed class JipperResourcePackReplayAdapter : IDisposable
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private readonly RecordedReplayDriver driver;
    private readonly Action<string> warning;
    private readonly Harmony harmony = new("KGH1113.TUFReplayRenderer.JipperResourcePackInput");
    private readonly List<Action> restore = new();
    private readonly HashSet<object> keys = new();
    private readonly HashSet<object> texts = new();
    private readonly HashSet<object> renderPools = new();
    private readonly HashSet<object> renderRain = new();
    private readonly Dictionary<KeyCode, (object label, ushort native)> mappedKeys = new();
    private readonly HashSet<KeyCode> unmappedKeys = new();
    private readonly ManualResetEventSlim workerBarrier = new(false);
    private static volatile JipperResourcePackReplayAdapter active;
    [ThreadStatic] private static bool injecting;
    private object viewer, ephemeralCounts, rainManager, updater, barrierQueue;
    private ConstructorInfo eventConstructor;
    private MethodInfo process, keyToLabel, labelToNative, updateKey, setText, rainUpdate, updaterUpdate;
    private Type viewerType, keyType, textType;
    private bool disposed;

    internal JipperResourcePackReplayAdapter(RecordedReplayDriver driver, Action<string> warning)
    { this.driver = driver; this.warning = warning; }

    internal void Begin()
    {
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "JipperResourcePack");
        if (assembly == null) return;
        Begin(assembly);
    }

    internal void Begin(Assembly assembly)
    {
        if (active != null) throw new InvalidOperationException("The JipperResourcePack replay adapter is already active.");
        viewerType = RequireType(assembly, "JipperResourcePack.KeyViewerContents.KeyViewer");
        viewer = Field(viewerType, "Instance").GetValue(null);
        if (viewer == null) return;
        var viewerKeys = Field(viewerType, "Keys").GetValue(viewer) as Array;
        if (viewerKeys == null) return; // The installed mod's KeyViewer feature is disabled.
        var eventType = viewerType.GetNestedType("KeyEvent", BindingFlags.NonPublic) ?? throw new TypeLoadException("JipperResourcePack.KeyViewer.KeyEvent");
        eventConstructor = eventType.GetConstructors(All).Single(c => c.GetParameters().Length == 4);
        var eventParameters = eventConstructor.GetParameters();
        if (eventParameters[1].ParameterType != typeof(ushort) || eventParameters[2].ParameterType != typeof(bool) || eventParameters[3].ParameterType != typeof(long)) throw new InvalidOperationException("Unsupported JipperResourcePack native event contract.");
        process = Method(viewerType, "ProcessKeyEvent", eventType);
        keyType = RequireType(assembly, "JipperResourcePack.KeyViewerContents.Key");
        textType = RequireType(assembly, "JipperResourcePack.Async.AsyncText");
        updateKey = Method(keyType, "UpdateKey", typeof(bool));
        setText = Method(textType, "SetTextForce", typeof(string));
        Type versionSafe = FindType("JipperResourcePack.VersionSafe");
        keyToLabel = Method(versionSafe, "UnityKeyToSkyHookKey", typeof(KeyCode));
        if (keyToLabel.ReturnType != eventParameters[0].ParameterType) throw new InvalidOperationException("The installed JALib/SkyHook key-label versions do not match.");
        labelToNative = Method(FindType("SkyHook.SkyHookKeyMapper"), "KeyLabelToNativeKeyCode", eventParameters[0].ParameterType);
        Type countsType = RequireType(assembly, "JipperResourcePack.KeyViewerContents.KeyCountData");
        FieldInfo countsInstance = Field(countsType, "Instance");
        object originalCounts = countsInstance.GetValue(null);
        ephemeralCounts = Activator.CreateInstance(countsType);
        rainManager = Field(viewerType, "RainManager").GetValue(null);
        updater = Field(viewerType, "Updater").GetValue(viewer);
        if (rainManager != null) rainUpdate = Method(rainManager.GetType(), "Update");
        if (updater != null) updaterUpdate = Method(updater.GetType(), "Update");
        object queue = Field(viewerType, "_eventQueue").GetValue(viewer);
        barrierQueue = queue;
        var signal = Field(viewerType, "_eventSignal").GetValue(viewer) as SemaphoreSlim;
        if (queue == null || signal == null || !(bool)Field(viewerType, "_listening").GetValue(viewer)) throw new InvalidOperationException("JipperResourcePack's native key viewer has not initialized its input worker.");

        active = this;
        try
        {
            Patch(viewerType.GetMethods(All).Single(m => m.Name == "OnKeyEvent"), nameof(BlockNative));
            Patch(process, nameof(AllowRecorded));
            Patch(Method(viewerType, "get_CurrentTicks"), nameof(VideoTicks));
            Patch(Method(countsType, "Save"), nameof(BlockExportSave));
            // The acknowledgement is processed after any already executing native event. No render
            // state is replaced until that event has finished; the worker is never awaited per frame.
            object sentinel = eventConstructor.Invoke(new[] { Enum.ToObject(eventParameters[0].ParameterType, 0), (object)ushort.MaxValue, false, long.MinValue });
            Method(queue.GetType(), "Enqueue", eventType).Invoke(queue, new[] { sentinel });
            signal.Release();
            if (!workerBarrier.Wait(2000)) throw new InvalidOperationException("JipperResourcePack's input worker did not acknowledge replay mode. Try again after its pending input finishes.");

            restore.Add(() => countsInstance.SetValue(null, originalCounts));
            countsInstance.SetValue(null, ephemeralCounts);
            ResetField(viewerType, viewer, "_selectedKey", -1);
            ResetField(viewerType, viewer, "_lastKpsCount", -1);
            ResetField(viewerType, viewer, "_lastTotalCount", -1);
            var state = (bool[])Field(viewerType, "_keyState").GetValue(viewer);
            var savedState = (bool[])state.Clone();
            restore.Add(() => Array.Copy(savedState, state, state.Length));
            Array.Clear(state, 0, state.Length);
            FieldInfo pressTimes = Field(viewerType, "_pressTimes");
            object originalPressTimes = pressTimes.GetValue(viewer);
            restore.Add(() => pressTimes.SetValue(viewer, originalPressTimes));
            pressTimes.SetValue(viewer, Activator.CreateInstance(pressTimes.FieldType));
            if (rainManager != null) SnapshotRains();
            foreach (object key in viewerKeys) if (key != null) SnapshotKey(key);
            SnapshotText(Field(viewerType, "Kps").GetValue(viewer), true);
            SnapshotText(Field(viewerType, "Total").GetValue(viewer), true);
            Patch(Method(keyType, "UpdateRequestKey", typeof(bool)), nameof(SynchronousKey));
            Patch(Method(textType, "set_Text", typeof(string)), nameof(SynchronousText));
            if (renderPools.Count > 0) harmony.Patch(Method(renderPools.First().GetType(), "GetOrNewRain", typeof(bool)), postfix: new HarmonyMethod(typeof(JipperResourcePackReplayAdapter), nameof(TrackRain)));
            AfterFrame(0);
        }
        catch { Dispose(); throw; }
    }

    private void SnapshotKey(object key)
    {
        keys.Add(key);
        bool requested = (bool)Field(keyType, "_requestEnabled").GetValue(key);
        int pending = (int)Field(keyType, "_updateRequested").GetValue(key);
        restore.Add(() => { Field(keyType, "_requestEnabled").SetValue(key, requested); updateKey.Invoke(key, new object[] { true }); Field(keyType, "_updateRequested").SetValue(key, pending); });
        Field(keyType, "_requestEnabled").SetValue(key, false);
        Field(keyType, "_updateRequested").SetValue(key, 0);
        updateKey.Invoke(key, new object[] { true });
        ResetField(keyType, key, "LastRain", null);
        ResetField(keyType, key, "LastGhostRain", null);
        FieldInfo poolField = Field(keyType, "RainPool");
        object oldPool = poolField.GetValue(key);
        if (oldPool != null)
        {
            object transform = Field(oldPool.GetType(), "Transform").GetValue(oldPool);
            object pool = Activator.CreateInstance(oldPool.GetType(), new[] { transform });
            renderPools.Add(pool);
            restore.Add(() => poolField.SetValue(key, oldPool));
            poolField.SetValue(key, pool);
        }
        SnapshotText(key, true);
    }

    private void SnapshotText(object key, bool resetCounter)
    {
        if (key == null) return;
        foreach (string name in new[] { "Text", "Value" })
        {
            object text = Field(key.GetType(), name).GetValue(key);
            if (text == null || !texts.Add(text)) continue;
            object original = Field(textType, "_text").GetValue(text);
            object tmp = Field(textType, "TMP").GetValue(text);
            PropertyInfo textProperty = AccessTools.Property(tmp.GetType(), "text") ?? throw new MissingMemberException(tmp.GetType().FullName, "text");
            string visible = (string)textProperty.GetValue(tmp);
            int pending = (int)Field(textType, "_textChangeRequested").GetValue(text);
            restore.Add(() => { setText.Invoke(text, new object[] { visible }); Field(textType, "_text").SetValue(text, original); Field(textType, "_textChangeRequested").SetValue(text, pending); });
            Field(textType, "_textChangeRequested").SetValue(text, 0);
            if (resetCounter && name == "Value") setText.Invoke(text, new object[] { "0" });
        }
    }

    private void SnapshotRains()
    {
        IList list = (IList)Field(rainManager.GetType(), "RainList").GetValue(rainManager);
        object[] original = list.Cast<object>().ToArray();
        var visibility = original.Select(r => (GameObject)Field(r.GetType(), "GameObject").GetValue(r)).Select(go => (go, go.activeSelf)).ToArray();
        foreach (var item in visibility) item.go.SetActive(false);
        restore.Add(() => { list.Clear(); foreach (object rain in original) list.Add(rain); foreach (var item in visibility) item.go.SetActive(item.activeSelf); });
        list.Clear();
        object queue = Field(rainManager.GetType(), "RawRainQueue").GetValue(rainManager);
        MethodInfo dequeue = queue.GetType().GetMethod("TryDequeue");
        var saved = new List<object>(); var itemArgs = new object[] { null };
        while ((bool)dequeue.Invoke(queue, itemArgs)) saved.Add(itemArgs[0]);
        restore.Add(() => { while ((bool)dequeue.Invoke(queue, itemArgs)) {} MethodInfo enqueue = queue.GetType().GetMethod("Enqueue"); foreach (object raw in saved) enqueue.Invoke(queue, new[] { raw }); });
    }

    internal void Apply(RecordedKeyEvent input, KeyCode code)
    {
        if (active != this) return;
        if (!mappedKeys.TryGetValue(code, out var mapped))
        {
            object label = keyToLabel.Invoke(null, new object[] { code });
            // The installed SkyHook Unity mapper omits these unshifted punctuation spellings.
            if (label.ToString() == "Unknown")
            {
                string alias = code == KeyCode.BackQuote ? "Grave" : code == KeyCode.Quote ? "Apostrophe" : null;
                if (alias != null) label = Enum.Parse(label.GetType(), alias);
            }
            if (label.ToString() == "Unknown" || label.ToString() == "IgnoredInternal")
            {
                if (unmappedKeys.Add(code)) warning?.Invoke("JipperResourcePack cannot display " + code + " with the installed SkyHook mapper. Its raw native-key bindings need a native-code recording contract.");
                return;
            }
            mapped = (label, Convert.ToUInt16(labelToNative.Invoke(null, new[] { label })));
            mappedKeys.Add(code, mapped);
        }
        object keyEvent = eventConstructor.Invoke(new[] { mapped.label, (object)mapped.native, input.Down, checked(driver.CurrentVideoTimeUs * 10) });
        injecting = true;
        try { process.Invoke(viewer, new[] { keyEvent }); }
        finally { injecting = false; }
    }

    internal void AfterFrame(double deltaTime)
    {
        if (active != this) return;
        updaterUpdate?.Invoke(updater, null);
        rainUpdate?.Invoke(rainManager, null);
    }

    private static bool BlockNative(object __instance) => active == null || !ReferenceEquals(active.viewer, __instance);
    private static bool AllowRecorded(object __instance, object __0)
    {
        var adapter = active;
        if (adapter == null || !ReferenceEquals(adapter.viewer, __instance)) return true;
        if ((long)Field(__0.GetType(), "Ticks").GetValue(__0) == long.MinValue)
        {
            try { adapter.workerBarrier.Set(); } catch (ObjectDisposedException) {}
            return false;
        }
        return injecting;
    }
    private static bool VideoTicks(ref long __result)
    { if (active == null) return true; __result = checked(active.driver.CurrentVideoTimeUs * 10); return false; }
    private static bool BlockExportSave(object __instance) => active == null || !ReferenceEquals(active.ephemeralCounts, __instance);
    private static bool SynchronousKey(object __instance, bool __0)
    {
        if (active == null || !active.keys.Contains(__instance)) return true;
        Field(active.keyType, "_requestEnabled").SetValue(__instance, __0);
        Field(active.keyType, "_updateRequested").SetValue(__instance, 1);
        active.updateKey.Invoke(__instance, new object[] { false });
        return false;
    }
    private static bool SynchronousText(object __instance, string __0)
    {
        if (active == null || !active.texts.Contains(__instance)) return true;
        active.setText.Invoke(__instance, new object[] { __0 });
        Field(active.textType, "_textChangeRequested").SetValue(__instance, 0);
        return false;
    }
    private static void TrackRain(object __instance, object __result)
    { if (active != null && active.renderPools.Contains(__instance)) active.renderRain.Add(__result); }
    private void Patch(MethodInfo target, string prefix) => harmony.Patch(target, prefix: new HarmonyMethod(typeof(JipperResourcePackReplayAdapter), prefix));
    private void ResetField(Type type, object instance, string name, object value)
    { var field = Field(type, name); object original = field.GetValue(instance); restore.Add(() => field.SetValue(instance, original)); field.SetValue(instance, value); }
    private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo Method(Type type, string name, params Type[] parameters) => AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);
    private static Type RequireType(Assembly assembly, string name) => assembly.GetType(name) ?? throw new TypeLoadException(name);
    private static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null) ?? throw new TypeLoadException(name);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Exception failure = null;
        if (active == this)
        {
            // A failed initial barrier must not leave its artificial event to be processed after
            // native input resumes. Preserve any other events still waiting in that same queue.
            if (barrierQueue != null) try
            {
                MethodInfo dequeue = barrierQueue.GetType().GetMethod("TryDequeue"), enqueue = barrierQueue.GetType().GetMethod("Enqueue");
                var pending = new List<object>(); var item = new object[] { null };
                while ((bool)dequeue.Invoke(barrierQueue, item)) if ((long)Field(item[0].GetType(), "Ticks").GetValue(item[0]) != long.MinValue) pending.Add(item[0]);
                foreach (object value in pending) enqueue.Invoke(barrierQueue, new[] { value });
            } catch (Exception exception) { failure ??= exception; }
            foreach (object rain in renderRain) try { var go = (GameObject)Field(rain.GetType(), "GameObject").GetValue(rain); go.SetActive(false); UnityEngine.Object.Destroy(go); } catch (Exception exception) { failure ??= exception; }
            for (int index = restore.Count - 1; index >= 0; index--) try { restore[index](); } catch (Exception exception) { failure ??= exception; }
            active = null;
            harmony.UnpatchAll(harmony.Id);
        }
        workerBarrier.Dispose();
        if (failure != null) warning?.Invoke("JipperResourcePack could not restore its temporary viewer state: " + failure.Message);
    }
}
