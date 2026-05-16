using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Vidow.EditorTools
{
    [InitializeOnLoad]
    public static class VidowSceneBuilder
    {
        private const string ScenePath = "Assets/Vidow/Scenes/VidowApp.unity";
        private const string PrefabPath = "Assets/Vidow/Prefabs/ResultItemView.prefab";
        private const string LogoPath = "Assets/Vidow/Art/Logo/vidow_logo_mark.png";
        private const string ThumbnailPath = "Assets/Vidow/Art/Placeholders/thumbnail_placeholder.png";
        private const string UnsupportedPath = "Assets/Vidow/Art/Placeholders/unsupported_thumbnail.png";
        private const string SearchIconPath = "Assets/Vidow/Art/Icons/icon_search.png";
        private const string DownloadIconPath = "Assets/Vidow/Art/Icons/icon_download.png";
        private const string FolderIconPath = "Assets/Vidow/Art/Icons/icon_folder.png";

        private static readonly Color Background = Hex("101316");
        private static readonly Color Surface = Hex("181D22");
        private static readonly Color SurfaceHover = Hex("20262D");
        private static readonly Color Border = Hex("303842");
        private static readonly Color TextPrimary = Hex("F1F5F9");
        private static readonly Color TextSecondary = Hex("A9B4C0");
        private static readonly Color TextMuted = Hex("6F7A86");
        private static readonly Color Accent = Hex("35C2FF");
        private static readonly Color Success = Hex("43D17A");
        private static readonly Color Warning = Hex("F4B740");
        private static readonly Color Danger = Hex("F35F5F");
        private static readonly Color ProgressTrack = Hex("27313A");

        static VidowSceneBuilder()
        {
            EditorApplication.delayCall += AutoBuildIfMissing;
        }

        [MenuItem("Vidow/Rebuild App Scene")]
        public static void RebuildFromMenu()
        {
            BuildAll(force: true);
        }

        [MenuItem("Vidow/Open App Scene")]
        public static void OpenAppScene()
        {
            if (!File.Exists(ScenePath))
            {
                BuildAll(force: true);
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        private static void AutoBuildIfMissing()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            if (!File.Exists(ScenePath) || !File.Exists(LogoPath) || !File.Exists(PrefabPath))
            {
                BuildAll(force: false);
            }
        }

        private static void BuildAll(bool force)
        {
            EnsureFolders();
            GenerateSprites(force);
            BuildResultItemPrefab(force);
            BuildScene(force);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Vidow] Generated scene, prefab, and UI art assets.");
        }

        private static void EnsureFolders()
        {
            EnsureFolder("Assets/Vidow", "Scenes");
            EnsureFolder("Assets/Vidow", "Prefabs");
            EnsureFolder("Assets/Vidow", "Art");
            EnsureFolder("Assets/Vidow/Art", "Icons");
            EnsureFolder("Assets/Vidow/Art", "Logo");
            EnsureFolder("Assets/Vidow/Art", "Placeholders");
        }

        private static void EnsureFolder(string parent, string child)
        {
            var path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        private static void GenerateSprites(bool force)
        {
            SaveSpritePng(LogoPath, CreateLogoTexture(256), force);
            SaveSpritePng(ThumbnailPath, CreateThumbnailTexture(512, 288, false), force);
            SaveSpritePng(UnsupportedPath, CreateThumbnailTexture(512, 288, true), force);
            SaveSpritePng(SearchIconPath, CreateIconTexture("search"), force);
            SaveSpritePng(DownloadIconPath, CreateIconTexture("download"), force);
            SaveSpritePng(FolderIconPath, CreateIconTexture("folder"), force);
        }

        private static void SaveSpritePng(string assetPath, Texture2D texture, bool force)
        {
            if (!force && File.Exists(assetPath))
            {
                Object.DestroyImmediate(texture);
                return;
            }

            File.WriteAllBytes(assetPath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        private static void BuildResultItemPrefab(bool force)
        {
            if (!force && File.Exists(PrefabPath))
            {
                return;
            }

            var root = new GameObject("ResultItemView", typeof(RectTransform), typeof(Image));
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(520, 94);
            Paint(root, Surface);

            var layout = root.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 10, 10);
            layout.spacing = 10;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            var thumb = Rect("Thumbnail", root.transform, new Vector2(104, 58));
            Paint(thumb.gameObject, Color.white).sprite = LoadSprite(ThumbnailPath);
            Layout(thumb.gameObject, 104, 58);

            var textColumn = Rect("Text Column", root.transform, new Vector2(260, 74));
            Layout(textColumn.gameObject, -1, 74, 1);
            var textLayout = textColumn.gameObject.AddComponent<VerticalLayoutGroup>();
            textLayout.spacing = 3;
            textLayout.childControlWidth = true;
            textLayout.childControlHeight = true;
            textLayout.childForceExpandWidth = true;

            Text("Video title appears here", textColumn, 14, FontStyles.Bold, TextPrimary, 22);
            Text("source.com - 03:21 - MP4 - 1080p", textColumn, 11, FontStyles.Normal, TextSecondary, 18);
            Text("Ready", textColumn, 11, FontStyles.Bold, Success, 18);
            var progress = Rect("Progress Track", textColumn, new Vector2(260, 5));
            Paint(progress.gameObject, ProgressTrack);
            Layout(progress.gameObject, -1, 5);

            var actions = Rect("Actions", root.transform, new Vector2(112, 72));
            Layout(actions.gameObject, 112, 72);
            var actionLayout = actions.gameObject.AddComponent<VerticalLayoutGroup>();
            actionLayout.spacing = 7;
            actionLayout.childControlWidth = true;
            actionLayout.childControlHeight = false;
            actionLayout.childForceExpandWidth = true;
            Button("Download", actions, Accent, Background, 34);
            Button("Copy", actions, SurfaceHover, TextSecondary, 30);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
        }

        private static void BuildScene(bool force)
        {
            if (!force && File.Exists(ScenePath))
            {
                SetBuildSettings();
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "VidowApp";

            var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraGo.tag = "MainCamera";
            cameraGo.transform.position = new Vector3(0, 0, -10);
            var camera = cameraGo.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            camera.orthographic = true;
            camera.orthographicSize = 5;

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var runtime = new GameObject("Vidow Runtime");
            runtime.AddComponent<VidowApp>();

            var canvasGo = new GameObject("Vidow Design Preview Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(VidowPreviewOnly));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(560, 720);
            scaler.matchWidthOrHeight = 0.5f;

            Paint(canvasGo, Background);
            var root = Rect("Safe Area Root", canvasGo.transform, Vector2.zero);
            Stretch(root, 16, 16, 16, 14);
            var mainLayout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            mainLayout.spacing = 12;
            mainLayout.childControlWidth = true;
            mainLayout.childControlHeight = true;
            mainLayout.childForceExpandWidth = true;
            mainLayout.childForceExpandHeight = false;

            BuildHeader(root);
            BuildSearch(root);
            Text("Paste a link, press Enter, then choose a folder when you download.", root, 12, FontStyles.Normal, TextMuted, 26);
            BuildResultsHeader(root);
            BuildResultsPreview(root);
            BuildFooter(root);

            EditorSceneManager.SaveScene(scene, ScenePath);
            SetBuildSettings();
        }

        private static void BuildHeader(Transform parent)
        {
            var header = Rect("Header", parent, new Vector2(528, 58));
            Layout(header.gameObject, -1, 58);
            var layout = header.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = true;

            var logo = Rect("Logo", header, new Vector2(48, 48));
            var logoImage = Paint(logo.gameObject, Color.white);
            logoImage.sprite = LoadSprite(LogoPath);
            logoImage.type = Image.Type.Simple;
            logoImage.preserveAspect = true;
            Layout(logo.gameObject, 48, 48);

            var titleColumn = Rect("Title Group", header, new Vector2(420, 52));
            Layout(titleColumn.gameObject, -1, 52, 1);
            var titleLayout = titleColumn.gameObject.AddComponent<VerticalLayoutGroup>();
            titleLayout.childControlWidth = true;
            titleLayout.childControlHeight = true;
            titleLayout.childForceExpandWidth = true;
            Text("Vidow", titleColumn, 20, FontStyles.Bold, TextPrimary, 28);
            Text("Ready", titleColumn, 12, FontStyles.Normal, TextSecondary, 20);
        }

        private static void BuildSearch(Transform parent)
        {
            var row = Rect("Search Panel", parent, new Vector2(528, 48));
            Layout(row.gameObject, -1, 48);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;

            var input = Rect("URL Input Frame", row, new Vector2(410, 44));
            Paint(input.gameObject, Surface);
            Layout(input.gameObject, -1, 44, 1);
            var inputLayout = input.gameObject.AddComponent<HorizontalLayoutGroup>();
            inputLayout.padding = new RectOffset(12, 10, 0, 0);
            inputLayout.spacing = 8;
            inputLayout.childAlignment = TextAnchor.MiddleCenter;
            inputLayout.childControlWidth = true;
            inputLayout.childControlHeight = true;

            Text("link", input, 12, FontStyles.Bold, Accent, 40, 34);
            Text("Paste a video page or direct media URL", input, 14, FontStyles.Normal, TextMuted, 40, -1, 1);
            Text("x", input, 12, FontStyles.Bold, TextSecondary, 36, 34);

            Button("Search", row, Accent, Background, 44, 106);
        }

        private static void BuildResultsHeader(Transform parent)
        {
            var row = Rect("Results Header", parent, new Vector2(528, 30));
            Layout(row.gameObject, -1, 30);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            Text("3 videos found", row, 13, FontStyles.Bold, TextSecondary, 26, -1, 1);
            Button("Clear", row, SurfaceHover, TextSecondary, 28, 74);
        }

        private static void BuildResultsPreview(Transform parent)
        {
            var panel = Rect("Results Scroll View", parent, new Vector2(528, 430));
            Layout(panel.gameObject, -1, -1, 1, 1);
            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            BuildResultBar(panel, "Ocean training clip", "cdn.example.com - 02:14 - MP4 - 1080p", "Ready", Success, "Download", ThumbnailPath, 0);
            BuildResultBar(panel, "Course lesson preview", "learning.example.org - 42% - 8.4 MB / 20.0 MB", "Downloading", Accent, "Cancel", ThumbnailPath, 0.42f);
            BuildResultBar(panel, "Protected platform sample", "private.example.net", "Unsupported", Warning, "N/A", UnsupportedPath, 0);
        }

        private static void BuildResultBar(Transform parent, string title, string meta, string status, Color statusColor, string action, string thumbPath, float progress)
        {
            var root = Rect("Result Bar - " + title, parent, new Vector2(528, 94));
            Paint(root.gameObject, Surface);
            Layout(root.gameObject, -1, 94);
            var layout = root.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 10, 10);
            layout.spacing = 10;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            var thumb = Rect("Thumbnail", root, new Vector2(104, 58));
            Paint(thumb.gameObject, Color.white).sprite = LoadSprite(thumbPath);
            Layout(thumb.gameObject, 104, 58);

            var textColumn = Rect("Text Column", root, new Vector2(280, 74));
            Layout(textColumn.gameObject, -1, 74, 1);
            var textLayout = textColumn.gameObject.AddComponent<VerticalLayoutGroup>();
            textLayout.spacing = 3;
            textLayout.childControlWidth = true;
            textLayout.childControlHeight = true;
            Text(title, textColumn, 14, FontStyles.Bold, TextPrimary, 22);
            Text(meta, textColumn, 11, FontStyles.Normal, TextSecondary, 18);
            Text(status, textColumn, 11, FontStyles.Bold, statusColor, 18);
            var track = Rect("Progress Track", textColumn, new Vector2(280, 5));
            Paint(track.gameObject, ProgressTrack);
            Layout(track.gameObject, -1, 5);
            if (progress > 0)
            {
                var fill = Rect("Progress Fill", track, Vector2.zero);
                Paint(fill.gameObject, Accent);
                fill.anchorMin = new Vector2(0, 0);
                fill.anchorMax = new Vector2(progress, 1);
                fill.offsetMin = Vector2.zero;
                fill.offsetMax = Vector2.zero;
            }

            var actions = Rect("Actions", root, new Vector2(112, 72));
            Layout(actions.gameObject, 112, 72);
            var actionLayout = actions.gameObject.AddComponent<VerticalLayoutGroup>();
            actionLayout.spacing = 7;
            Button(action, actions, action == "N/A" ? SurfaceHover : Accent, action == "N/A" ? TextMuted : Background, 34);
            Button("Copy", actions, SurfaceHover, TextSecondary, 30);
        }

        private static void BuildFooter(Transform parent)
        {
            var footer = Rect("Footer", parent, new Vector2(528, 36));
            Layout(footer.gameObject, -1, 36);
            var layout = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            var path = Rect("Path Chip", footer, new Vector2(440, 30));
            Paint(path.gameObject, Surface);
            Layout(path.gameObject, -1, 30, 1);
            var pathLayout = path.gameObject.AddComponent<HorizontalLayoutGroup>();
            pathLayout.padding = new RectOffset(10, 10, 0, 0);
            pathLayout.spacing = 6;
            Text("folder", path, 11, FontStyles.Bold, Accent, 24, 42);
            Text("Downloads", path, 11, FontStyles.Normal, TextMuted, 24, -1, 1);
            Text("Online", footer, 11, FontStyles.Bold, Success, 30, 70);
        }

        private static Button Button(string label, Transform parent, Color background, Color textColor, float height, float width = -1)
        {
            var root = Rect(label + " Button", parent, new Vector2(width > 0 ? width : 110, height));
            Paint(root.gameObject, background);
            Layout(root.gameObject, width, height);
            var button = root.gameObject.AddComponent<Button>();
            button.targetGraphic = root.GetComponent<Image>();
            var text = Text(label, root, 13, FontStyles.Bold, textColor, height);
            Stretch(text.rectTransform, 8, 8, 2, 2);
            text.alignment = TextAlignmentOptions.Center;
            return button;
        }

        private static TextMeshProUGUI Text(string value, Transform parent, float size, FontStyles style, Color color, float height, float width = -1, float flexibleWidth = 0)
        {
            var rect = Rect("Text - " + value, parent, new Vector2(width > 0 ? width : 120, height));
            Layout(rect.gameObject, width, height, flexibleWidth);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            return rect;
        }

        private static Image Paint(GameObject go, Color color)
        {
            var image = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static void Layout(GameObject go, float width = -1, float height = -1, float flexibleWidth = 0, float flexibleHeight = 0)
        {
            var layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            if (width >= 0)
            {
                layout.preferredWidth = width;
            }

            if (height >= 0)
            {
                layout.preferredHeight = height;
            }

            layout.flexibleWidth = flexibleWidth;
            layout.flexibleHeight = flexibleHeight;
        }

        private static void Stretch(RectTransform rect, float left = 0, float right = 0, float top = 0, float bottom = 0)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        private static void SetBuildSettings()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true)
            };
        }

        private static Sprite LoadSprite(string path)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static Texture2D CreateLogoTexture(int size)
        {
            var tex = ClearTexture(size, size);
            var center = new Vector2(size / 2f, size / 2f);
            var shadowCenter = center + new Vector2(0, -size * 0.04f);
            var radius = size * 0.41f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var shadowDistance = Vector2.Distance(p, shadowCenter);
                    if (shadowDistance <= size * 0.44f)
                    {
                        var shadowAlpha = Mathf.Clamp01((size * 0.44f - shadowDistance) / (size * 0.08f)) * 0.26f;
                        tex.SetPixel(x, y, new Color(0, 0, 0, shadowAlpha));
                    }

                    var distance = Vector2.Distance(p, center);
                    var edgeAlpha = Mathf.Clamp01(radius + 0.5f - distance);
                    if (edgeAlpha <= 0)
                    {
                        continue;
                    }

                    var gradient = Mathf.Clamp01((x + y) / (float)(size * 2));
                    var rim = Color.Lerp(Accent, Success, gradient);
                    var inner = Color.Lerp(new Color(0.07f, 0.13f, 0.16f, 1), new Color(0.10f, 0.18f, 0.20f, 1), y / (float)size);
                    var fill = distance < size * 0.30f ? inner : Color.Lerp(rim, new Color(0.13f, 0.25f, 0.27f, 1), Mathf.InverseLerp(size * 0.30f, radius, distance));

                    if (distance < size * 0.36f && y > center.y + size * 0.09f)
                    {
                        fill = Color.Lerp(fill, Color.white, 0.10f);
                    }

                    fill.a = edgeAlpha;
                    tex.SetPixel(x, y, fill);
                }
            }

            DrawTriangle(tex, new Vector2(size * 0.43f, size * 0.32f), new Vector2(size * 0.43f, size * 0.68f), new Vector2(size * 0.71f, size * 0.50f), new Color(0.88f, 0.98f, 1f, 1));
            DrawTriangle(tex, new Vector2(size * 0.47f, size * 0.40f), new Vector2(size * 0.47f, size * 0.60f), new Vector2(size * 0.63f, size * 0.50f), Accent);
            tex.Apply();
            return tex;
        }

        private static Texture2D CreateThumbnailTexture(int width, int height, bool unsupported)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var top = unsupported ? new Color(0.22f, 0.18f, 0.12f, 1) : new Color(0.08f, 0.12f, 0.15f, 1);
            var bottom = unsupported ? new Color(0.14f, 0.12f, 0.1f, 1) : new Color(0.12f, 0.16f, 0.19f, 1);
            for (var y = 0; y < height; y++)
            {
                var t = y / (height - 1f);
                var c = Color.Lerp(bottom, top, t);
                for (var x = 0; x < width; x++)
                {
                    var stripe = ((x + y) % 48) < 2 ? 0.04f : 0f;
                    tex.SetPixel(x, y, c + new Color(stripe, stripe, stripe, 0));
                }
            }

            DrawTriangle(tex, new Vector2(width * 0.43f, height * 0.34f), new Vector2(width * 0.43f, height * 0.66f), new Vector2(width * 0.62f, height * 0.50f), unsupported ? Warning : Accent);
            tex.Apply();
            return tex;
        }

        private static Texture2D CreateIconTexture(string type)
        {
            var tex = ClearTexture(128, 128);
            var color = Color.white;
            if (type == "search")
            {
                DrawCircleOutline(tex, new Vector2(54, 54), 28, 5, color);
                DrawLine(tex, new Vector2(75, 75), new Vector2(102, 102), 6, color);
            }
            else if (type == "download")
            {
                DrawLine(tex, new Vector2(64, 24), new Vector2(64, 78), 7, color);
                DrawLine(tex, new Vector2(40, 56), new Vector2(64, 82), 7, color);
                DrawLine(tex, new Vector2(88, 56), new Vector2(64, 82), 7, color);
                DrawLine(tex, new Vector2(34, 100), new Vector2(94, 100), 7, color);
            }
            else
            {
                FillRect(tex, 22, 42, 84, 50, color);
                FillRect(tex, 28, 32, 38, 16, color);
            }

            tex.Apply();
            return tex;
        }

        private static Texture2D ClearTexture(int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    tex.SetPixel(x, y, Color.clear);
                }
            }

            return tex;
        }

        private static void DrawTriangle(Texture2D tex, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            var minX = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
            var maxX = Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
            var minY = Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)));
            var maxY = Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)));
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    if (PointInTriangle(new Vector2(x, y), a, b, c))
                    {
                        tex.SetPixel(x, y, color);
                    }
                }
            }
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var s = a.y * c.x - a.x * c.y + (c.y - a.y) * p.x + (a.x - c.x) * p.y;
            var t = a.x * b.y - a.y * b.x + (a.y - b.y) * p.x + (b.x - a.x) * p.y;
            if ((s < 0) != (t < 0))
            {
                return false;
            }

            var area = -b.y * c.x + a.y * (c.x - b.x) + a.x * (b.y - c.y) + b.x * c.y;
            return area < 0 ? (s <= 0 && s + t >= area) : (s >= 0 && s + t <= area);
        }

        private static void DrawCircleOutline(Texture2D tex, Vector2 center, float radius, float thickness, Color color)
        {
            for (var y = 0; y < tex.height; y++)
            {
                for (var x = 0; x < tex.width; x++)
                {
                    var d = Vector2.Distance(new Vector2(x, y), center);
                    if (Mathf.Abs(d - radius) <= thickness)
                    {
                        tex.SetPixel(x, y, color);
                    }
                }
            }
        }

        private static void DrawLine(Texture2D tex, Vector2 start, Vector2 end, float thickness, Color color)
        {
            var minX = Mathf.FloorToInt(Mathf.Min(start.x, end.x) - thickness);
            var maxX = Mathf.CeilToInt(Mathf.Max(start.x, end.x) + thickness);
            var minY = Mathf.FloorToInt(Mathf.Min(start.y, end.y) - thickness);
            var maxY = Mathf.CeilToInt(Mathf.Max(start.y, end.y) + thickness);
            var line = end - start;
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var p = new Vector2(x, y);
                    var t = Mathf.Clamp01(Vector2.Dot(p - start, line) / line.sqrMagnitude);
                    var closest = start + line * t;
                    if (Vector2.Distance(p, closest) <= thickness)
                    {
                        tex.SetPixel(x, y, color);
                    }
                }
            }
        }

        private static void FillRect(Texture2D tex, int x, int y, int width, int height, Color color)
        {
            for (var yy = y; yy < y + height; yy++)
            {
                for (var xx = x; xx < x + width; xx++)
                {
                    if (xx >= 0 && yy >= 0 && xx < tex.width && yy < tex.height)
                    {
                        tex.SetPixel(xx, yy, color);
                    }
                }
            }
        }

        private static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out var color);
            return color;
        }
    }
}
