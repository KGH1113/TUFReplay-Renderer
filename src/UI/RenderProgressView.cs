using System;
using System.IO;
using System.Collections.Generic;
using TUFReplayRenderer.Engine;
using TUFReplayRenderer.Jobs;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace TUFReplayRenderer.UI;

internal sealed class RenderProgressView : MonoBehaviour
{
    private GameObject view;
    private TMP_Text stage;
    private TMP_Text detail;
    private TMP_Text percent;
    private Image fill;
    private Button cancel;
    private RawImage preview;
    private AspectRatioFitter previewAspect;
    private string lastStage;
    private int lastPercent = -1;
    private RenderJobController jobs;
    private bool korean;

    internal void Initialize(string directory, RenderJobController controller)
    {
        jobs = controller; korean = Application.systemLanguage == SystemLanguage.Korean;
        string platform = Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor ? "mac"
            : Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor ? "win" : "linux";
        string path = Path.Combine(directory, "Assets", platform, "tufreplay_renderer_ui.bundle");
        if (!File.Exists(path)) throw new InvalidOperationException("The renderer progress UI bundle is missing. Reinstall the complete TUFReplay-Renderer package.");
        AssetBundle bundle = AssetBundle.LoadFromFile(path);
        if (bundle == null) throw new InvalidOperationException("The renderer progress UI bundle could not be loaded for this platform.");
        try {
            GameObject prefab = bundle.LoadAsset<GameObject>("Assets/Prefabs/RenderProgress.prefab");
            if (prefab == null) throw new InvalidOperationException("The progress UI prefab is missing from the renderer asset bundle.");
            view = Instantiate(prefab); view.name = "TUFReplayRendererProgress";
            DontDestroyOnLoad(view);
            Transform panel = view.transform.Find("Panel");
            stage = panel.Find("Stage").GetComponent<TMP_Text>(); detail = panel.Find("Detail").GetComponent<TMP_Text>();
            percent = panel.Find("Percent").GetComponent<TMP_Text>(); fill = panel.Find("Track/Fill").GetComponent<Image>();
            cancel = panel.Find("Cancel").GetComponent<Button>();
            preview = view.transform.Find("Preview").GetComponent<RawImage>();
            previewAspect = preview.GetComponent<AspectRatioFitter>();
            panel.Find("Cancel/Label").GetComponent<TMP_Text>().text = korean ? "취소" : "Cancel";
            cancel.onClick.AddListener(() => { if (jobs.CurrentJobId != null) jobs.Cancel(jobs.CurrentJobId); });
            view.SetActive(false);
        }
        finally { bundle.Unload(false); }
    }

    private void Update()
    {
        if (view == null || jobs == null) return;
        bool show = jobs.Busy && jobs.CurrentState != "completed" && jobs.CurrentState != "failed" && jobs.CurrentState != "cancelled";
        if (view.activeSelf != show) view.SetActive(show);
        if (!show) return;
        string state = jobs.CurrentState;
        bool waitingForFocus = jobs.WaitingForGameFocus;
        string displayState = waitingForFocus ? "waiting_for_game_focus" : state;
        if (lastStage != displayState) {
            lastStage = displayState;
            stage.text = waitingForFocus ? (korean ? "게임 창 확인 대기 중" : "Waiting for the game window")
                : state == "preparing" ? (korean ? "렌더 준비 중" : "Preparing render")
                : state == "compositing" ? (korean ? "영상 합성 중" : "Compositing video") : (korean ? "렌더링 중" : "Rendering");
            detail.text = waitingForFocus ? (korean ? "얼불춤 창을 복원하고 클릭해 주세요. 최대 30초 동안 기다리며 취소할 수 있어요." : "Restore and click the ADOFAI window. Waiting up to 30 seconds; you can cancel.")
                : state == "preparing" ? (korean ? "기록과 렌더 설정을 확인하고 있어요" : "Checking the recording and render settings")
                : state == "compositing" ? (korean ? "카메라·오디오·키뷰어를 합치고 있어요" : "Combining recorded media and overlays")
                : (korean ? "기록된 플레이를 영상으로 만들고 있어요" : "Turning the recorded play into video");
        }
        float progress = Mathf.Clamp01((float)jobs.CurrentProgress);
        fill.fillAmount = progress;
        int value = (int)(progress * 100);
        if (value != lastPercent) { lastPercent = value; percent.text = value + "%"; }
        cancel.interactable = jobs.CanCancelCurrent;
        Texture texture = EmbeddedRenderEngine.PreviewTexture;
        bool showPreview = jobs.ShowPreview && state == "rendering" && texture != null;
        if (preview.gameObject.activeSelf != showPreview) preview.gameObject.SetActive(showPreview);
        if (showPreview && preview.texture != texture) {
            preview.texture = texture;
            previewAspect.aspectRatio = (float)texture.width / texture.height;
        }
    }

    internal IEnumerable<Canvas> PresentationCanvases()
    {
        if (view != null) yield return view.GetComponent<Canvas>();
    }

    private void OnDestroy() { if (view != null) Destroy(view); }
}
