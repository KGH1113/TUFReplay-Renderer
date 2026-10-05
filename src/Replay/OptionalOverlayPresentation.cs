using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TUFReplayRenderer.Replay;

// Presentation-only compatibility: never turn the game's editor simulation
// into a gameworld. Only the inspected overlay visibility reads see playback.
internal sealed class OptionalOverlayPresentation : IDisposable
{
    private readonly Harmony harmony = new("KGH1113.TUFReplayRenderer.OverlayPresentation");
    private readonly List<Action> refresh = new();
    private readonly List<Action> restore = new();
    private static OptionalOverlayPresentation active;
    private static readonly FieldInfo WorldField = AccessTools.Field(typeof(scrController), "gameworld");
    private static readonly MethodInfo WorldGetter = AccessTools.PropertyGetter(typeof(scrController), "gameworld");
    private static readonly MethodInfo PlayGetter = AccessTools.PropertyGetter(typeof(scnEditor), "playMode");
    private static readonly FieldInfo PlayField = AccessTools.Field(typeof(scnEditor), "playMode");
    private Canvas ghostCanvas;
    private object ghostOverlay;
    private readonly Vector3[] meterCorners = new Vector3[4];

    internal void Begin()
    {
        active = this;
        try
        {
            Type ghost = AccessTools.TypeByName("DonQuixoteOverlay.OverlayController");
            if (ghost != null)
            {
                ghostOverlay = AccessTools.PropertyGetter(ghost, "Instance")?.Invoke(null, null);
                if (ghostOverlay != null)
                {
                    ghostCanvas = AccessTools.Field(ghost, "_canvas")?.GetValue(ghostOverlay) as Canvas;
                    RememberVisibility(ghostCanvas != null ? ghostCanvas.gameObject : null);
                    FieldInfo deadline = AccessTools.Field(ghost, "_metadataNextUpdate");
                    object previousDeadline = deadline.GetValue(ghostOverlay);
                    restore.Add(() => deadline.SetValue(ghostOverlay, previousDeadline));
                    deadline.SetValue(ghostOverlay, 0f);
                    MethodInfo update = AccessTools.Method(ghost, "Update");
                    PatchVisibility(update);
                    refresh.Add((Action)Delegate.CreateDelegate(typeof(Action), ghostOverlay, update));
                    harmony.Patch(AccessTools.Method(ghost, "ErrorMeterAnchor"), prefix: new HarmonyMethod(typeof(OptionalOverlayPresentation), nameof(MeterAnchor)));
                }
                Type viewer = AccessTools.TypeByName("DonQuixoteOverlay.KeyViewerContents.KeyViewer");
                if (viewer != null) PatchVisibility(AccessTools.Method(viewer, "Update"));
            }
            Type jipper = AccessTools.TypeByName("JipperResourcePack.OverlayContents.Overlay");
            object overlay = jipper == null ? null : AccessTools.Field(jipper, "Instance")?.GetValue(null);
            if (overlay != null)
            {
                GameObject root = AccessTools.Field(jipper, "GameObject").GetValue(overlay) as GameObject;
                RememberVisibility(root);
                bool enabled = false;
                foreach (string feature in new[] { "Status", "Bpm", "Combo", "Attempt", "TimingScale", "Judgement" })
                {
                    Type type = jipper.Assembly.GetType("JipperResourcePack.OverlayContents." + feature);
                    object instance = AccessTools.Field(type, "Instance")?.GetValue(null);
                    bool visible = instance != null && (bool)(AccessTools.PropertyGetter(type, "Enabled")?.Invoke(instance, null) ?? false);
                    enabled |= visible;
                    if (feature == "TimingScale" || feature == "Judgement")
                    {
                        var child = AccessTools.Field(type, feature == "TimingScale" ? "TimingScaleObject" : "JudgementObject")?.GetValue(null) as GameObject;
                        RememberVisibility(child);
                        if (child != null) child.SetActive(visible);
                    }
                }
                if (root != null && enabled) root.SetActive(true);
                if (enabled) refresh.Add(() => { if (root != null && !root.activeSelf) root.SetActive(true); });
                FieldInfo hit = AccessTools.Field(jipper, "Hit");
                object previousHit = hit.GetValue(overlay);
                restore.Add(() => hit.SetValue(overlay, previousHit));
                var hitCounts = (Func<int[]>)Delegate.CreateDelegate(typeof(Func<int[]>),
                    AccessTools.Method(AccessTools.TypeByName("JipperResourcePack.VersionSafe"), "GetHitMarginsCount"));
                refresh.Add(() => hit.SetValue(overlay, hitCounts()));
                FieldInfo manager = AccessTools.Field(jipper, "OverlayTextManager");
                if (manager.GetValue(overlay) == null)
                {
                    restore.Add(() => manager.SetValue(overlay, null));
                    AccessTools.Method(jipper, "SetupTextManager").Invoke(overlay, null);
                }
                var judgement = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), overlay, AccessTools.Method(jipper, "UpdateJudgement", new[] { typeof(int) }));
                refresh.Add(() => judgement(-1));
                foreach (string name in new[] { "UpdateTimingScale", "UpdateBpm", "UpdateTime", "RefreshTiming" })
                {
                    MethodInfo method = AccessTools.Method(jipper, name, Type.EmptyTypes);
                    if (method != null) refresh.Add((Action)Delegate.CreateDelegate(typeof(Action), overlay, method));
                }
            }
        }
        catch { Dispose(); throw; }
    }
    private void RememberVisibility(GameObject go)
    {
        if (go == null) return;
        bool original = go.activeSelf;
        restore.Add(() => { if (go != null) go.SetActive(original); });
    }
    private void PatchVisibility(MethodInfo method)
    {
        if (method == null) throw new MissingMethodException("The overlay's playback visibility method is unavailable.");
        harmony.Patch(method, transpiler: new HarmonyMethod(typeof(OptionalOverlayPresentation), nameof(Visibility)));
    }
    private static IEnumerable<CodeInstruction> Visibility(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if ((WorldField != null && Equals(instruction.operand, WorldField)) || (WorldGetter != null && Equals(instruction.operand, WorldGetter)))
            {
                instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(OptionalOverlayPresentation), nameof(GameWorld));
            }
            else if ((PlayGetter != null && Equals(instruction.operand, PlayGetter)) || (PlayField != null && Equals(instruction.operand, PlayField)))
            {
                instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(OptionalOverlayPresentation), nameof(EditorPlayback));
            }
            yield return instruction;
        }
    }
    private static bool GameWorld(scrController controller) => active != null || controller.gameworld;
    private static bool EditorPlayback(scnEditor editor) => active != null || editor.playMode;
    private static bool MeterAnchor(object __instance, scrController __0, ref Vector2 __result)
    {
        if (active == null || !ReferenceEquals(active.ghostOverlay, __instance) || active.ghostCanvas == null || __0?.errorMeter?.wrapperRectTransform == null) return true;
        RectTransform meter = __0.errorMeter.wrapperRectTransform;
        RectTransform root = active.ghostCanvas.transform as RectTransform;
        Canvas meterCanvas = meter.GetComponentInParent<Canvas>();
        if (root == null || meterCanvas == null) return true;
        var corners = active.meterCorners;
        meter.GetWorldCorners(corners);
        Camera meterCamera = meterCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : meterCanvas.worldCamera;
        Camera overlayCamera = active.ghostCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : active.ghostCanvas.worldCamera;
        Vector2 screen = RectTransformUtility.WorldToScreenPoint(meterCamera, (corners[1] + corners[2]) * .5f);
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, overlayCamera, out Vector2 local)) return true;
        __result = new Vector2(local.x, local.y + root.rect.height * .5f + 6f);
        return false;
    }
    internal void AfterFrame() { foreach (Action update in refresh) update(); }
    public void Dispose()
    {
        if (active != this) return;
        Exception failure = null;
        try { for (int i = restore.Count - 1; i >= 0; i--) try { restore[i](); } catch (Exception e) { failure ??= e; } }
        finally { harmony.UnpatchAll(harmony.Id); active = null; }
        if (failure != null) throw new InvalidOperationException("The overlay could not restore its visibility.", failure);
    }
}
