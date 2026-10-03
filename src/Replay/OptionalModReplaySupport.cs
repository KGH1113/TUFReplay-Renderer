using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace TUFReplayRenderer.Replay;

public sealed class OverlayCapability
{
  public string Mod { get; set; }
  public bool PixelCapture { get; set; } = true;
  public bool InputReplay { get; set; }
  public bool RuntimeVerified { get; set; }
  public string Status { get; set; }
  public string Detail { get; set; }
}

public static class OptionalModCapabilities
{
  public static OverlayCapability[] Inspect()
  {
    var result = new List<OverlayCapability>();
    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
      string name = assembly.GetName().Name;
      switch (name)
      {
        case "KeyViewer":
          if (assembly.GetType("KeyViewer.ReplayInput") == null) break; // Embedded input shim has no drawable overlay.
          result.Add(new OverlayCapability { Mod = name, InputReplay = true, Status = "requires-runtime-verification",
            Detail = "Public replay input, event-level key updates, count restoration and render clock hooks are implemented. Rain, tweens and multiple profiles require an in-game comparison." });
          break;
        case "JipperKeyViewer":
          result.Add(new OverlayCapability { Mod = name, InputReplay = true, Status = "requires-runtime-verification",
            Detail = "Recorded held keys and event-level main, foot, ghost and custom key updates are implemented. Counters are restored. VideoPlayer textures require independent decoding and are unsupported." });
          break;
        case "Overlayer":
          result.Add(new OverlayCapability { Mod = name, InputReplay = false, Status = "requires-runtime-verification",
            Detail = "Canvas capture and known Unity/Stopwatch/date clock callsites are supported. Custom tags and asynchronous effects require an in-game comparison." });
          break;
        case "JipperResourcePack":
          result.Add(new OverlayCapability { Mod = name, InputReplay = true, Status = "requires-runtime-verification",
            Detail = "Native input is blocked behind an acknowledgement barrier, recorded events use the viewer's own binding/processing path, and temporary counters/rain/text updates are isolated and restored. Runtime visual comparison is still required." });
          break;
        case "ImplResourcePack":
          result.Add(new OverlayCapability { Mod = name, InputReplay = false, Status = "requires-runtime-verification",
            Detail = "Gameplay-derived overlay Canvas and clock hooks are implemented. External ImplDmNote uses its separate alpha exporter. Key-limiter and custom InputEvent integration require an in-game comparison." });
          break;
      }
    }
    return result.ToArray();
  }

  public static string[] Warnings() => Inspect().Select(c => c.Mod + ": " + c.Detail).ToArray();
}

// Reflection uses each viewer's own drawing and input implementation; no overlay geometry is recreated.
internal sealed class OptionalModReplaySupport : IDisposable
{
  private readonly RecordedReplayDriver driver;
  private readonly Action<string> warning;
  private readonly Harmony harmony = new("KGH1113.TUFReplayRenderer.OptionalInput");
  private readonly List<Action> restore = new();
  private readonly List<Action<RecordedKeyEvent, KeyCode>> inputAdapters = new();
  private static OptionalModReplaySupport active;
  private MethodInfo keyViewerPress;
  private readonly Dictionary<KeyCode, long> lastKpsSequence = new();
  private readonly Dictionary<object, KeyBinding> keyBindings = new();
  private IDictionary keyViewerFlags;
  private JipperResourcePackReplayAdapter resourcePack;
  private bool disposed;
  private bool injectingInput;
  private long previousKpsVideoUs;

  internal OptionalModReplaySupport(RecordedReplayDriver driver, Action<string> warning)
  { this.driver = driver; this.warning = warning; }

  internal void Begin()
  {
    active = this;
    foreach (string item in OptionalModCapabilities.Warnings()) warning(item);
    TryAdapter("KeyViewer", BeginKeyViewer);
    TryAdapter("JipperKeyViewer", BeginJipperKeyViewer);
    resourcePack = new JipperResourcePackReplayAdapter(driver, warning);
    resourcePack.Begin();
  }

  private void TryAdapter(string name, Action<Assembly> begin)
  {
    Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
    if (assembly == null) return;
    try { begin(assembly); }
    catch (Exception exception)
    {
      // A mismatched installed version must not silently show incorrect key input.
      throw new InvalidOperationException($"{name} could not connect to the recorded input. Disable that viewer or install the investigated version before rendering.", exception);
    }
  }

  private void BeginKeyViewer(Assembly assembly)
  {
    Type replay = assembly.GetType("KeyViewer.ReplayInput");
    if (replay == null) return;
    Type api = RequireType(assembly, "KeyViewer.API.InputAPI");
    Type main = RequireType(assembly, "KeyViewer.Main");
    Type keyType = RequireType(assembly, "KeyViewer.Key");
    Type kps = RequireType(assembly, "KeyViewer.KPSCalculator");
    StopKeyViewerBackup(RequireType(assembly, "KeyViewer.BackupManager"));
    MethodInfo start = RequireMethod(replay, "OnStartInputs");
    MethodInfo end = RequireMethod(replay, "OnEndInputs");
    MethodInfo down = RequireMethod(replay, "OnKeyPressed", typeof(KeyCode));
    MethodInfo up = RequireMethod(replay, "OnKeyReleased", typeof(KeyCode));
    MethodInfo update = RequireMethod(keyType, "Update");
    PropertyInfo count = AccessTools.Property(keyType, "Count");
    PropertyInfo code = AccessTools.Property(keyType, "Code");
    PropertyInfo activeProperty = AccessTools.Property(api, "Active");
    bool wasActive = (bool)activeProperty.GetValue(null);
    PropertyInfo eventActiveProperty = AccessTools.Property(api, "EventActive");
    bool wasEventActive = (bool)eventActiveProperty.GetValue(null);
    IDictionary flags = (IDictionary)Read(api, null, "APIFlags");
    keyViewerFlags = flags;
    SnapshotDictionary(flags);
    flags.Clear();
    restore.Add(() => { end.Invoke(null, null); activeProperty.SetValue(null, wasActive); eventActiveProperty.SetValue(null, wasEventActive); });
    var keys = new List<object>();
    foreach (object manager in (IEnumerable)Read(main, null, "KeyManagers"))
    {
      PropertyInfo total = AccessTools.Property(manager.GetType(), "TotalCount");
      object originalTotal = total.GetValue(manager);
      restore.Add(() => total.SetValue(manager, originalTotal));
      total.SetValue(manager, 0u);
      foreach (object key in (IEnumerable)Read(manager.GetType(), manager, "Keys"))
      {
        object oldCount = count.GetValue(key);
        bool oldPressed = (bool)Read(keyType, key, "prevPressed");
        restore.Add(() => { count.SetValue(key, oldCount); SetCounterText(keyType, key, oldCount); AccessTools.Field(keyType, "prevPressed").SetValue(key, oldPressed); });
        count.SetValue(key, 0u);
        SetCounterText(keyType, key, 0u);
        AccessTools.Field(keyType, "prevPressed").SetValue(key, false);
        keys.Add(key);
        object config = Read(keyType, key, "config");
        keyBindings.Add(key, new KeyBinding((KeyCode)code.GetValue(key), (KeyCode)Read(config.GetType(), config, "SpareCode")));
      }
    }
    SnapshotKps(kps);
    keyViewerPress = RequireMethod(kps, "Press");
    harmony.Patch(RequireMethod(kps, "Press", typeof(KeyCode)), prefix: new HarmonyMethod(typeof(OptionalModReplaySupport), nameof(CountRecordedKps)));
    harmony.Patch(RequireMethod(kps, "Tick", typeof(float)), prefix: new HarmonyMethod(typeof(OptionalModReplaySupport), nameof(KpsRenderDelta)));
    PatchSaveIfPresent(main, "OnSaveGUI");
    PatchSaveIfPresent(RequireType(assembly, "KeyViewer.Settings"), "Save");
    PatchSaveIfPresent(main, "BeginKeyCapture");
    PatchSaveIfPresent(main, "BeginViewerDrag");
    harmony.Patch(update, prefix: new HarmonyMethod(typeof(OptionalModReplaySupport), nameof(KeyViewerBindingInput)),
      postfix: new HarmonyMethod(typeof(OptionalModReplaySupport), nameof(RestoreKeyViewerFlags)));
    start.Invoke(null, null);
    inputAdapters.Add((input, keyCode) =>
    {
      (input.Down ? down : up).Invoke(null, new object[] { keyCode });
      // Sampling just the last held state per video frame loses short taps and rapid repeats.
      foreach (object key in keys)
      {
        KeyBinding binding = keyBindings[key];
        if (binding.Primary == keyCode || binding.Spare == keyCode) update.Invoke(key, null);
      }
    });
  }

  private void BeginJipperKeyViewer(Assembly assembly)
  {
    Type source = RequireType(assembly, "JipperKeyViewer.KeyViewer.KeySource");
    Type viewer = RequireType(assembly, "JipperKeyViewer.KeyViewer.KeyViewer");
    Type rainType = RequireType(assembly, "JipperKeyViewer.KeyViewer.Rain.RainSystem");
    harmony.Patch(RequireMethod(source, "GetKey", typeof(KeyCode)),
      prefix: new HarmonyMethod(typeof(OptionalModReplaySupport), nameof(RecordedHeldKey)) { priority = Priority.First });
    MethodInfo mainFoot = RequireMethod(viewer, "ProcessMainAndFootKeysInUpdate", typeof(long));
    MethodInfo custom = RequireMethod(viewer, "ProcessCustomKeysInUpdate", typeof(long));
    MethodInfo ghost = RequireMethod(viewer, "ProcessGhostKeysInUpdate");
    PropertyInfo customLayout = AccessTools.Property(viewer, "IsCustomLayout");
    object settings = Read(viewer, null, "Settings");
    if (settings == null) throw new InvalidOperationException("JipperKeyViewer has not initialized its settings.");
    object data = Read(settings.GetType(), settings, "Data");
    if (!(bool)Read(data.GetType(), data, "Enabled")) return;
    SnapshotCounters(data);
    PatchSaveIfPresent(viewer, "SaveSettings");
    PatchSaveIfPresent(viewer, "SaveSettingsFromGui");
    harmony.Patch(AccessTools.Method(rainType, "UpdateEffects"), prefix: new HarmonyMethod(typeof(OptionalModReplaySupport), nameof(OneRainStepPerFrame)));
    var viewers = UnityEngine.Object.FindObjectsByType(viewer, FindObjectsSortMode.None).Cast<object>().ToArray();
    if (viewers.Length == 0) throw new InvalidOperationException("JipperKeyViewer has not initialized its viewer.");
    foreach (object instance in viewers)
    {
      SnapshotCollection(AccessTools.Field(viewer, "PressTimes")?.GetValue(instance));
      SnapshotCollection(AccessTools.Field(viewer, "keyPressTimes")?.GetValue(instance));
      SnapshotCollection(AccessTools.Field(viewer, "customGroupPresses")?.GetValue(instance));
      SnapshotCollection(AccessTools.Field(viewer, "customGhostStates")?.GetValue(instance));
      SnapshotCollection(AccessTools.Field(viewer, "counterBounces")?.GetValue(instance));
      SnapshotBooleanArray(viewer, instance, "ghostKeyStates");
      Array viewerKeys = Read(viewer, instance, "Keys") as Array;
      if (viewerKeys != null) IsolateJipperViewer(viewer, rainType, instance, viewerKeys, settings, data);
    }
    inputAdapters.Add((input, keyCode) =>
    {
      long milliseconds = driver.CurrentVideoTimeUs / 1000;
      foreach (object instance in viewers)
      {
        if ((bool)customLayout.GetValue(null)) custom.Invoke(instance, new object[] { milliseconds });
        else { mainFoot.Invoke(instance, new object[] { milliseconds }); ghost.Invoke(instance, null); }
      }
    });
  }

  private void IsolateJipperViewer(Type viewerType, Type rainType, object instance, Array viewerKeys, object settings, object data)
  {
    FieldInfo rainField = AccessTools.Field(viewerType, "rainSystem");
    object originalRain = rainField.GetValue(instance);
    object temporaryRain = Activator.CreateInstance(rainType, new[] { settings });
    object layer = Read(rainType, originalRain, "Layer"), ghost = Read(rainType, originalRain, "GhostLayer");
    MethodInfo attach = AccessTools.Method(rainType, "AttachLayers");
    restore.Add(() => { rainField.SetValue(instance, originalRain); attach.Invoke(originalRain, new[] { layer, ghost }); });
    rainField.SetValue(instance, temporaryRain);
    AccessTools.Field(rainType, "Keys").SetValue(temporaryRain, viewerKeys);
    attach.Invoke(temporaryRain, new[] { layer, ghost });
    MethodInfo colors = AccessTools.Method(viewerType, "UpdateKeyColors");
    MethodInfo customColors = AccessTools.Method(viewerType, "ApplyCustomKeyColors");
    MethodInfo customSpecialColors = AccessTools.Method(viewerType, "ApplyCustomSpecialColors");
    for (int index = 0; index < viewerKeys.Length; index++)
    {
      object key = viewerKeys.GetValue(index);
      if (key == null) continue;
      Type keyType = key.GetType();
      FieldInfo pressed = AccessTools.Field(keyType, "isPressed");
      bool oldPressed = (bool)pressed.GetValue(key);
      FieldInfo rains = AccessTools.Field(keyType, "rainList");
      object oldRains = rains.GetValue(key);
      FieldInfo bouncing = AccessTools.Field(keyType, "Bouncing");
      object oldBouncing = bouncing.GetValue(key);
      foreach (string fieldName in new[] { "BounceStart", "BounceBasePos", "LastShownKps", "LastShownTotal", "LastShownStatKps" })
      {
        FieldInfo field = AccessTools.Field(keyType, fieldName);
        object old = field.GetValue(key);
        restore.Add(() => field.SetValue(key, old));
      }
      FieldInfo animation = AccessTools.Field(keyType, "currentAnim");
      object oldAnimation = animation.GetValue(key);
      restore.Add(() => {
        if (animation.GetValue(key) is Coroutine created && !ReferenceEquals(created, oldAnimation))
          ((MonoBehaviour)instance).StopCoroutine(created);
        animation.SetValue(key, oldAnimation);
      });
      SnapshotTransform(Read(keyType, key, "visuals") as Transform);
      object label = Read(keyType, key, "value");
      PropertyInfo text = label == null ? null : AccessTools.Property(label.GetType(), "text");
      object oldText = text?.GetValue(label);
      SnapshotTransform((label as Component)?.transform);
      object node = Read(keyType, key, "CustomNode");
      int keyIndex = index;
      Action<bool> setColor = down => {
        if (node != null)
        {
          int nodeType = (int)Read(node.GetType(), node, "NodeType");
          (nodeType == 1 || nodeType == 2 ? customSpecialColors : customColors).Invoke(instance, new[] { key, node, (object)down });
        }
        else colors.Invoke(instance, new[] { (object)keyIndex, down, data });
      };
      restore.Add(() => { pressed.SetValue(key, oldPressed); bouncing.SetValue(key, oldBouncing);
        rains.SetValue(key, oldRains); setColor(oldPressed); text?.SetValue(label, oldText); });
      pressed.SetValue(key, false);
      bouncing.SetValue(key, false);
      rains.SetValue(key, Activator.CreateInstance(rains.FieldType));
      setColor(false);
      text?.SetValue(label, "0");
      SnapshotCollection(AccessTools.Field(keyType, "KpsLog")?.GetValue(key));
    }
  }

  private void SnapshotTransform(Transform transform)
  {
    if (transform == null) return;
    Vector3 scale = transform.localScale;
    restore.Add(() => { if (transform != null) transform.localScale = scale; });
    if (transform is RectTransform rect)
    {
      Vector2 position = rect.anchoredPosition;
      restore.Add(() => { if (rect != null) rect.anchoredPosition = position; });
    }
  }

  private void SnapshotBooleanArray(Type type, object instance, string name)
  {
    if (AccessTools.Field(type, name)?.GetValue(instance) is not bool[] values) return;
    bool[] saved = (bool[])values.Clone();
    restore.Add(() => Array.Copy(saved, values, values.Length));
    Array.Clear(values, 0, values.Length);
  }

  private void SnapshotCounters(object data)
  {
    foreach (FieldInfo field in data.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
    {
      if (field.Name != "Count" && field.Name != "TotalCount") continue;
      object old = field.GetValue(data);
      if (old is Array values)
      {
        Array saved = (Array)values.Clone();
        restore.Add(() => Array.Copy(saved, values, saved.Length));
        Array.Clear(values, 0, values.Length);
      }
      else if (old != null && (old.GetType() == typeof(int) || old.GetType() == typeof(long) || old.GetType() == typeof(uint)))
      {
        restore.Add(() => field.SetValue(data, old));
        field.SetValue(data, Activator.CreateInstance(old.GetType()));
      }
    }
    object customNodes = AccessTools.Property(data.GetType(), "CustomNodes")?.GetValue(data)
      ?? AccessTools.Field(data.GetType(), "CustomNodes")?.GetValue(data);
    if (customNodes is IEnumerable nodes)
      foreach (object node in nodes) if (node != null) SnapshotCounters(node);
  }

  private void StopKeyViewerBackup(Type type)
  {
    Thread worker = AccessTools.Field(type, "worker")?.GetValue(null) as Thread;
    if (worker == null) return;
    RequireMethod(type, "Stop").Invoke(null, null);
    if (worker.IsAlive) throw new InvalidOperationException("The key viewer is still writing a backup. Wait a moment and try rendering again.");
    restore.Add(() => RequireMethod(type, "Start").Invoke(null, null));
  }

  private static void SetCounterText(Type type, object key, object count)
  {
    object text = AccessTools.Property(type, "CountText")?.GetValue(key);
    if (text != null) AccessTools.Property(text.GetType(), "text")?.SetValue(text, count.ToString());
  }

  private void SnapshotCollection(object value)
  {
    if (value is IDictionary dictionary)
    {
      foreach (DictionaryEntry entry in dictionary) SnapshotCollection(entry.Value);
      SnapshotDictionary(dictionary);
      dictionary.Clear();
    }
    else if (value is Array array) { foreach (object item in array) SnapshotCollection(item); }
    else if (value is IList list)
    {
      object[] saved = list.Cast<object>().ToArray();
      restore.Add(() => { list.Clear(); foreach (object item in saved) list.Add(item); });
      list.Clear();
    }
    else if (value is IEnumerable queue && value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(Queue<>))
    {
      object[] saved = queue.Cast<object>().ToArray();
      MethodInfo clear = RequireMethod(value.GetType(), "Clear");
      MethodInfo enqueue = AccessTools.Method(value.GetType(), "Enqueue");
      restore.Add(() => { clear.Invoke(value, null); foreach (object item in saved) enqueue.Invoke(value, new[] { item }); });
      clear.Invoke(value, null);
    }
  }

  private void SnapshotDictionary(IDictionary dictionary)
  {
    var values = new List<DictionaryEntry>();
    foreach (DictionaryEntry value in dictionary) values.Add(value);
    restore.Add(() => { dictionary.Clear(); foreach (DictionaryEntry value in values) dictionary.Add(value.Key, value.Value); });
  }

  private void SnapshotKps(Type type)
  {
    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
    {
      object value = field.GetValue(null);
      if (value is IDictionary dictionary) { SnapshotDictionary(dictionary); dictionary.Clear(); }
      else if (value is IEnumerable entries && value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(Queue<>))
      {
        object[] saved = entries.Cast<object>().ToArray();
        MethodInfo clear = RequireMethod(value.GetType(), "Clear");
        MethodInfo enqueue = AccessTools.Method(value.GetType(), "Enqueue");
        restore.Add(() => { clear.Invoke(value, null); foreach (object entry in saved) enqueue.Invoke(value, new[] { entry }); });
        clear.Invoke(value, null);
      }
      else if (!field.IsInitOnly && field.FieldType.IsValueType)
      {
        restore.Add(() => field.SetValue(null, value));
        field.SetValue(null, Activator.CreateInstance(field.FieldType));
      }
    }
  }

  private void PatchSaveIfPresent(Type type, string name)
  {
    MethodInfo method = AccessTools.Method(type, name);
    if (method != null) harmony.Patch(method, prefix: new HarmonyMethod(typeof(OptionalModReplaySupport), nameof(PreventSave)));
  }

  private static bool PreventSave() => active == null;
  private static bool OneRainStepPerFrame() => active?.injectingInput != true;
  private static bool RecordedHeldKey(KeyCode code, ref bool __result)
  {
    if (active == null) return true;
    __result = active.driver.HasHeldKey(code);
    return false;
  }
  private static bool CountRecordedKps(KeyCode code)
  {
    if (active == null) return true;
    long sequence = active.driver.CurrentInputSequence;
    if (!active.lastKpsSequence.TryGetValue(code, out long previous) || sequence != previous)
    {
      active.lastKpsSequence[code] = sequence;
      active.keyViewerPress.Invoke(null, null);
    }
    return false;
  }
  private static void KpsRenderDelta(ref float deltaTime)
  {
    if (active == null) return;
    long now = active.driver.CurrentVideoTimeUs;
    deltaTime = Math.Max(0, now - active.previousKpsVideoUs) / 1_000_000f;
    active.previousKpsVideoUs = now;
  }
  private static void KeyViewerBindingInput(object __instance, out FlagState __state)
  {
    __state = default;
    if (active == null || !active.keyBindings.TryGetValue(__instance, out KeyBinding binding)) return;
    IDictionary flags = active.keyViewerFlags;
    bool existed = flags.Contains(binding.Primary);
    __state = new FlagState { Valid = true, Key = binding.Primary, Existed = existed,
      OldValue = existed && (bool)flags[binding.Primary] };
    // The viewer's public replay API polls only Code. Fold the configured spare per key instance,
    // so profiles sharing a primary key can retain different spare bindings.
    flags[binding.Primary] = active.driver.HasHeldKey(binding.Primary) || active.driver.HasHeldKey(binding.Spare);
  }
  private static void RestoreKeyViewerFlags(FlagState __state)
  {
    if (active == null || !__state.Valid) return;
    if (__state.Existed) active.keyViewerFlags[__state.Key] = __state.OldValue;
    else active.keyViewerFlags.Remove(__state.Key);
  }

  private readonly struct KeyBinding
  {
    internal readonly KeyCode Primary;
    internal readonly KeyCode Spare;
    internal KeyBinding(KeyCode primary, KeyCode spare) { Primary = primary; Spare = spare; }
  }
  private struct FlagState
  {
    internal bool Valid, Existed, OldValue;
    internal KeyCode Key;
  }

  internal void BeforeFrame() { }
  internal void AfterFrame(double deltaTime) => resourcePack?.AfterFrame(deltaTime);
  internal void Apply(RecordedKeyEvent input, KeyCode code)
  {
    injectingInput = true;
    try { foreach (var adapter in inputAdapters) adapter(input, code); resourcePack?.Apply(input, code); }
    finally { injectingInput = false; }
  }

  public void Dispose()
  {
    if (disposed) return;
    disposed = true;
    Exception failure = null;
    try { resourcePack?.Dispose(); } catch (Exception exception) { failure = exception; }
    for (int index = restore.Count - 1; index >= 0; index--)
      try { restore[index](); } catch (Exception exception) { failure ??= exception; }
    active = null;
    harmony.UnpatchAll(harmony.Id);
    if (failure != null) warning("A key viewer could not restore all temporary counters: " + failure.Message);
  }

  private static Type RequireType(Assembly assembly, string name) => assembly.GetType(name)
    ?? throw new TypeLoadException(name);
  private static MethodInfo RequireMethod(Type type, string name, params Type[] parameters) => AccessTools.Method(type, name, parameters)
    ?? throw new MissingMethodException(type.FullName, name);
  private static object Read(Type type, object instance, string name)
  {
    PropertyInfo property = AccessTools.Property(type, name);
    if (property != null) return property.GetValue(instance);
    return (AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name)).GetValue(instance);
  }
}
