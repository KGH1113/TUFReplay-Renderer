using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TUFReplayRenderer.Replay;

// Ghostify 0.4.3 observes desktop events on a callback, but applies them on
// Unity's thread. Recorded input uses its own Unity-key routing, including feet
// and duplicate bindings; native polling and persistence stay isolated.
internal sealed class GhostifyOverlayReplayAdapter : IDisposable
{
    private readonly RecordedReplayDriver driver;
    private readonly Harmony harmony = new("KGH1113.TUFReplayRenderer.GhostifyInput");
    private readonly List<Action> restore = new();
    private readonly HashSet<GameObject> generatedRains = new();
    private readonly HashSet<object> temporaryPools = new();
    private readonly List<Action<bool>> drawKeys = new();
    private static GhostifyOverlayReplayAdapter active;
    private object viewer, counts, rainManager;
    private Type viewerType;
    private Action<KeyCode, bool, long> work;
    private Action update, counters, rainTick;
    private object[] keys;
    private bool manualUpdate;
    private bool disposed;

    internal GhostifyOverlayReplayAdapter(RecordedReplayDriver driver) { this.driver = driver; }
    internal void Begin()
    {
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("DonQuixoteOverlay.KeyViewerContents.KeyViewer") != null);
        if (assembly == null) return;
        viewerType = assembly.GetType("DonQuixoteOverlay.KeyViewerContents.KeyViewer");
        viewer = Field(viewerType, "Instance").GetValue(null);
        if (viewer == null || Field(viewerType, "Keys").GetValue(viewer) is not Array slots) return;
        keys = slots.Cast<object>().Where(k => k != null).ToArray();
        work = (Action<KeyCode, bool, long>)Delegate.CreateDelegate(typeof(Action<KeyCode, bool, long>), viewer,
            RequireMethod(viewerType, "WorkUnity", typeof(KeyCode), typeof(bool), typeof(long)));
        MethodInfo updateMethod = RequireMethod(viewerType, "Update");
        update = (Action)Delegate.CreateDelegate(typeof(Action), viewer, updateMethod);
        counters = (Action)Delegate.CreateDelegate(typeof(Action), viewer, RequireMethod(viewerType, "UpdateCounters"));
        Type countType = assembly.GetType("DonQuixoteOverlay.KeyViewerContents.KeyCountData") ?? throw new TypeLoadException("Ghostify key counts");
        FieldInfo instance = Field(countType, "Instance");
        object originalCounts = instance.GetValue(null);
        counts = Activator.CreateInstance(countType);
        active = this;
        try
        {
            Patch(RequireMethod(viewerType, "OnKeyEvent"), nameof(BlockNative));
            Patch(RequireMethod(viewerType, "ObserveRawEvent"), nameof(BlockNative));
            Patch(RequireMethod(viewerType, "PumpInput"), nameof(BlockNative));
            Patch(RequireMethod(viewerType, "ResetTransient"), nameof(BlockNative));
            Patch(RequireMethod(viewerType, "OnApplicationFocus"), nameof(BlockNative));
            Patch(RequireMethod(viewerType, "BeginGameplayRun"), nameof(BlockNative));
            Patch(updateMethod, nameof(AllowManualUpdate));
            Patch(AccessTools.PropertyGetter(viewerType, "CurrentTicks") ?? throw new MissingMemberException("Ghostify CurrentTicks"), nameof(VideoTicks));
            Patch(RequireMethod(countType, "Save"), nameof(BlockSave));
            Patch(RequireMethod(countType, "Flush"), nameof(BlockSave));
            restore.Add(() => instance.SetValue(null, originalCounts));
            instance.SetValue(null, counts);
            Reset("_pressTimes", Activator.CreateInstance(Field(viewerType, "_pressTimes").FieldType));
            Reset("_lastKpsCount", -1);
            Reset("_lastTotalCount", -1L);
            Reset("_focused", true);
            Reset("_suspended", false);
            Reset("_rawGhostInput", false);
            Reset("_visible", Field(viewerType, "_visible").GetValue(viewer));
            if (Field(viewerType, "KeyViewerObject").GetValue(viewer) is GameObject root)
            {
                bool visible = root.activeSelf;
                restore.Add(() => { if (root != null) root.SetActive(visible); });
            }
            SnapshotArray(Field(viewerType, "_shownCounts").GetValue(viewer) as Array);
            object state = Field(viewerType, "_state").GetValue(viewer);
            SnapshotArray((Array)Field(state.GetType(), "_state").GetValue(state));
            SnapshotArray((Array)Field(state.GetType(), "_startHeld").GetValue(state));
            var held = (bool[])Field(viewerType, "_keyState").GetValue(viewer);
            var originalHeld = (bool[])held.Clone();
            restore.Add(() => Array.Copy(originalHeld, held, held.Length));
            Array.Clear(held, 0, held.Length);
            foreach (object key in keys) SnapshotKey(key);
            SnapshotText(Field(viewerType, "Kps").GetValue(viewer));
            SnapshotText(Field(viewerType, "Total").GetValue(viewer));
            rainManager = Field(viewerType, "RainManager").GetValue(null);
            if (rainManager != null)
            {
                rainTick = (Action)Delegate.CreateDelegate(typeof(Action), rainManager, RequireMethod(rainManager.GetType(), "Tick"));
                SnapshotRains();
            }
        }
        catch { Dispose(); throw; }
    }

    private void Reset(string name, object value)
    {
        FieldInfo field = Field(viewerType, name); object old = field.GetValue(viewer);
        restore.Add(() => field.SetValue(viewer, old)); field.SetValue(viewer, value);
    }
    private void SnapshotArray(Array array)
    {
        if (array == null) return;
        Array old = (Array)array.Clone();
        restore.Add(() => Array.Copy(old, array, array.Length));
        Array.Clear(array, 0, array.Length);
    }
    private void SnapshotText(object key)
    {
        if (key == null) return;
        foreach (string name in new[] { "Text", "Value" })
            if (Field(key.GetType(), name).GetValue(key) is object text)
            {
                PropertyInfo property = AccessTools.Property(text.GetType(), "text");
                string old = (string)property.GetValue(text);
                restore.Add(() => property.SetValue(text, old));
                if (name == "Value") property.SetValue(text, "0");
            }
    }
    private void SnapshotKey(object key)
    {
        SnapshotText(key);
        var type = key.GetType();
        var saved = new[] { "_requested", "_current", "_dirty", "LastRain", "LastGhostRain" }
            .Select(n => (field: Field(type, n), value: Field(type, n).GetValue(key))).ToArray();
        var draw = (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), key, RequireMethod(type, "UpdateKey", typeof(bool)));
        drawKeys.Add(draw);
        restore.Add(() => {
            foreach (var item in saved) item.field.SetValue(key, item.value);
            draw(true);
            foreach (var item in saved) item.field.SetValue(key, item.value);
        });
        Field(type, "_requested").SetValue(key, false);
        Field(type, "LastRain").SetValue(key, null);
        Field(type, "LastGhostRain").SetValue(key, null);
        draw(true);
        FieldInfo poolField = Field(type, "RainPool");
        object oldPool = poolField.GetValue(key);
        if (oldPool != null)
        {
            object parent = Field(oldPool.GetType(), "Transform").GetValue(oldPool);
            object temporary = Activator.CreateInstance(oldPool.GetType(), new[] { parent });
            restore.Add(() => poolField.SetValue(key, oldPool)); poolField.SetValue(key, temporary);
            temporaryPools.Add(temporary);
            restore.Add(() => {
                foreach (RectTransform layer in (Array)Field(temporary.GetType(), "_layers").GetValue(temporary))
                    if (layer != null) generatedRains.Add(layer.gameObject);
            });
            MethodInfo get = RequireMethod(oldPool.GetType(), "GetOrNewRain", typeof(bool));
            if (temporaryPools.Count == 1) harmony.Patch(get, postfix: new HarmonyMethod(typeof(GhostifyOverlayReplayAdapter), nameof(TrackRain)));
        }
    }
    private void SnapshotRains()
    {
        IList list = (IList)Field(rainManager.GetType(), "RainList").GetValue(rainManager);
        object[] saved = list.Cast<object>().ToArray();
        var visibility = saved.Select(r => (GameObject)Field(r.GetType(), "GameObject").GetValue(r)).Select(go => (go, go.activeSelf)).ToArray();
        foreach (var item in visibility) item.go.SetActive(false);
        list.Clear();
        object queue = Field(rainManager.GetType(), "RawRainQueue").GetValue(rainManager);
        MethodInfo dequeue = queue.GetType().GetMethod("TryDequeue"), enqueue = queue.GetType().GetMethod("Enqueue");
        var pending = new List<object>(); var args = new object[] { null };
        while ((bool)dequeue.Invoke(queue, args)) pending.Add(args[0]);
        restore.Add(() => {
            foreach (object rain in list) generatedRains.Add((GameObject)Field(rain.GetType(), "GameObject").GetValue(rain));
            list.Clear(); foreach (object rain in saved) list.Add(rain);
            foreach (var item in visibility) if (item.go != null) item.go.SetActive(item.activeSelf);
            while ((bool)dequeue.Invoke(queue, args)) { }
            foreach (object raw in pending) enqueue.Invoke(queue, new[] { raw });
        });
    }
    internal void BeforeFrame()
    {
        if (active != this) return;
        manualUpdate = true;
        try { update(); }
        finally { manualUpdate = false; }
    }
    internal void Apply(RecordedKeyEvent input, KeyCode key)
    {
        if (active == this) work(key, input.Down, checked(driver.CurrentVideoTimeUs * 10));
    }
    internal void AfterFrame()
    {
        if (active != this) return;
        foreach (var draw in drawKeys) draw(false);
        counters();
        rainTick?.Invoke();
    }
    private void Patch(MethodBase target, string prefix) => harmony.Patch(target, prefix: new HarmonyMethod(typeof(GhostifyOverlayReplayAdapter), prefix));
    private static bool BlockNative(object __instance) => active == null || !ReferenceEquals(active.viewer, __instance);
    private static bool BlockSave(object __instance) => active == null || !ReferenceEquals(active.counts, __instance);
    private static bool AllowManualUpdate(object __instance) => active == null || !ReferenceEquals(active.viewer, __instance) || active.manualUpdate;
    private static bool VideoTicks(ref long __result) { if (active == null) return true; __result = checked(active.driver.CurrentVideoTimeUs * 10); return false; }
    private static void TrackRain(object __instance, object __result)
    {
        if (active != null && active.temporaryPools.Contains(__instance) && __result != null)
            active.generatedRains.Add((GameObject)Field(__result.GetType(), "GameObject").GetValue(__result));
    }
    private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo RequireMethod(Type type, string name, params Type[] args) => (args.Length == 0 ? AccessTools.Method(type, name) : AccessTools.Method(type, name, args)) ?? throw new MissingMethodException(type.FullName, name);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (active != this) return;
        Exception failure = null;
        try { for (int i = restore.Count - 1; i >= 0; i--) try { restore[i](); } catch (Exception e) { failure ??= e; } }
        finally { active = null; harmony.UnpatchAll(harmony.Id); foreach (var go in generatedRains) if (go != null) UnityEngine.Object.Destroy(go); }
        if (failure != null) throw new InvalidOperationException("Ghostify could not restore its key viewer state.", failure);
    }
}
