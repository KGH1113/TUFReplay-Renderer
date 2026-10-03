using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.TextCore.LowLevel;

namespace TUFReplayRenderer.Unity.Editor
{
    public static class RenderProgressBuilder
    {
        public const string PrefabPath = "Assets/Prefabs/RenderProgress.prefab";
        private const string ScenePath = "Assets/Scenes/RenderProgressPreview.unity";
        private const string BundleName = "tufreplay_renderer_ui.bundle";

        [MenuItem("Tools/TUFReplay-Renderer/Rebuild Progress UI")]
        public static void Rebuild()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode before rebuilding the progress UI.");
            Directory.CreateDirectory("Assets/Prefabs");
            Directory.CreateDirectory("Assets/Scenes");
            Directory.CreateDirectory("Assets/Generated");
            AssetDatabase.Refresh();
            Sprite rounded = MakeRoundedSprite();
            TMP_FontAsset font = EnsureFont();
            GameObject root = new GameObject("TUFReplayRendererProgress", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32745;
            CanvasScaler scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;
            RectTransform card = Rect("Panel", root.transform, new Vector2(540, 174), new Vector2(0, 1), new Vector2(24, -24));
            Image panel = card.gameObject.AddComponent<Image>();
            panel.sprite = rounded; panel.type = Image.Type.Sliced; panel.color = ColorHex("171A23", .97f);
            Label("Stage", card, font, "렌더링 중", 28, Color.white, new Vector2(410, 40), new Vector2(24, -18));
            Label("Detail", card, font, "기록된 플레이를 영상으로 만들고 있어요", 18, ColorHex("ADB7CA"), new Vector2(492, 38), new Vector2(24, -62));
            RectTransform track = Rect("Track", card, new Vector2(492, 5), new Vector2(0, 1), new Vector2(24, -112));
            Image trackImage = track.gameObject.AddComponent<Image>();
            trackImage.sprite = rounded; trackImage.type = Image.Type.Sliced; trackImage.color = ColorHex("303745");
            RectTransform fill = Rect("Fill", track, new Vector2(492, 5), new Vector2(0, 1), Vector2.zero);
            Image fillImage = fill.gameObject.AddComponent<Image>();
            fillImage.sprite = rounded; fillImage.type = Image.Type.Filled; fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillAmount = .42f; fillImage.color = ColorHex("69A8FF"); fillImage.raycastTarget = false;
            Label("Percent", card, font, "42%", 16, ColorHex("ADB7CA"), new Vector2(270, 28), new Vector2(24, -130));
            RectTransform cancel = Rect("Cancel", card, new Vector2(76, 30), new Vector2(1, 1), new Vector2(-24, -128));
            cancel.pivot = new Vector2(1, 1);
            Image buttonImage = cancel.gameObject.AddComponent<Image>(); buttonImage.sprite = rounded;
            buttonImage.type = Image.Type.Sliced; buttonImage.color = ColorHex("303745");
            Button button = cancel.gameObject.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f); colors.pressedColor = new Color(.82f, .82f, .82f);
            colors.fadeDuration = .08f; button.colors = colors;
            TMP_Text cancelText = Label("Label", cancel, font, "취소", 16, Color.white, new Vector2(76, 30), Vector2.zero);
            cancelText.alignment = TextAlignmentOptions.Center;
            RectTransform preview = Rect("Preview", root.transform, new Vector2(540, 304), new Vector2(0, 1), new Vector2(24, -210));
            RawImage previewImage = preview.gameObject.AddComponent<RawImage>();
            previewImage.raycastTarget = false;
            previewImage.color = Color.white;
            AspectRatioFitter previewAspect = preview.gameObject.AddComponent<AspectRatioFitter>();
            previewAspect.aspectMode = AspectRatioFitter.AspectMode.WidthControlsHeight;
            previewAspect.aspectRatio = 16f / 9;
            preview.gameObject.SetActive(false);
            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>()) text.raycastTarget = false;
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject background = new GameObject("Preview Camera", typeof(Camera));
            background.GetComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;
            background.GetComponent<Camera>().backgroundColor = ColorHex("0C1018");
            PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("[TUFReplay-Renderer] Progress prefab and preview scene rebuilt.");
        }

        [MenuItem("Tools/TUFReplay-Renderer/Build Runtime UI Bundles")]
        public static void BuildBundles()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null) Rebuild();
            var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = new[] { PrefabPath } };
            string repository = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            foreach (var platform in new[] { ("mac", BuildTarget.StandaloneOSX), ("win", BuildTarget.StandaloneWindows64), ("linux", BuildTarget.StandaloneLinux64) }) {
                string output = Path.Combine(repository, "Assets", platform.Item1);
                Directory.CreateDirectory(output);
                AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, platform.Item2);
                if (manifest == null || !File.Exists(Path.Combine(output, BundleName))) throw new InvalidOperationException("Progress UI bundle failed for " + platform.Item1);
            }
            Debug.Log("[TUFReplay-Renderer] Runtime progress UI bundles built for macOS, Windows and Linux.");
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 anchor, Vector2 position)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform));
            RectTransform rect = obj.GetComponent<RectTransform>(); rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = anchor; rect.pivot = new Vector2(0, 1);
            rect.sizeDelta = size; rect.anchoredPosition = position;
            return rect;
        }
        private static TMP_Text Label(string name, Transform parent, TMP_FontAsset font, string value, int size, Color color, Vector2 dimensions, Vector2 position)
        {
            TMP_Text text = Rect(name, parent, dimensions, new Vector2(0, 1), position).gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font; text.text = value; text.fontSize = size; text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.Normal; text.overflowMode = TextOverflowModes.Truncate;
            return text;
        }
        private static TMP_FontAsset EnsureFont()
        {
            const string path = "Assets/Generated/ProgressFont SDF.asset";
            TMP_FontAsset asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            if (asset == null) {
                Font source = AssetDatabase.LoadAssetAtPath<Font>("Assets/MAPLESTORY_OTF_BOLD.OTF");
                if (source == null) throw new InvalidOperationException("The Korean progress font is missing.");
                asset = TMP_FontAsset.CreateFontAsset(source, 72, 8, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
                if (asset == null) throw new InvalidOperationException("The SDF progress font could not be created. Import TMP Essential Resources first.");
                asset.name = "ProgressFont SDF"; asset.material.name = "ProgressFont Material";
                AssetDatabase.CreateAsset(asset, path); AssetDatabase.AddObjectToAsset(asset.material, asset);
            }
            string characters = "렌더 준비 중 기록과 설정을 확인하고 있어요 영상 합성 카메라·오디오·키뷰어를 합치고 기록된 플레이으로 만들취소";
            for (int value = 32; value <= 126; value++) characters += (char)value;
            if (!asset.TryAddCharacters(characters, out string missing) && !string.IsNullOrEmpty(missing))
                throw new InvalidOperationException("The progress font is missing required characters: " + missing);
            foreach (Texture2D atlas in asset.atlasTextures) {
                if (!AssetDatabase.Contains(atlas)) AssetDatabase.AddObjectToAsset(atlas, asset);
                EditorUtility.SetDirty(atlas);
            }
            EditorUtility.SetDirty(asset); AssetDatabase.SaveAssets();
            return asset;
        }
        private static Color ColorHex(string hex, float alpha = 1)
        { ColorUtility.TryParseHtmlString("#" + hex, out Color value); value.a = alpha; return value; }
        private static Sprite MakeRoundedSprite()
        {
            const int side = 64, radius = 16;
            string path = "Assets/Generated/ProgressRounded.png";
            Texture2D texture = new Texture2D(side, side, TextureFormat.RGBA32, false);
            for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) {
                float dx = Mathf.Max(radius - x - .5f, x + .5f - (side - radius));
                float dy = Mathf.Max(radius - y - .5f, y + .5f - (side - radius));
                float distance = new Vector2(Mathf.Max(0, dx), Mathf.Max(0, dy)).magnitude;
                texture.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(radius - distance + .5f)));
            }
            texture.Apply(); File.WriteAllBytes(path, texture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Single;
            TextureImporterSettings settings = new TextureImporterSettings(); importer.ReadTextureSettings(settings);
            settings.spriteMeshType = SpriteMeshType.FullRect; importer.SetTextureSettings(settings);
            importer.spriteBorder = new Vector4(radius, radius, radius, radius);
            importer.mipmapEnabled = false; importer.alphaIsTransparency = true; importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }
    }
}
