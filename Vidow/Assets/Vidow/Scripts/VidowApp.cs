using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Vidow
{
    public static class VidowRuntimeBootstrapper
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (UnityEngine.Object.FindFirstObjectByType<VidowApp>() != null)
            {
                return;
            }

            var go = new GameObject("Vidow App");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<VidowApp>();
        }
    }

    public sealed class VidowApp : MonoBehaviour
    {
        private const int ResolveTimeoutSeconds = 120;
        private const int RequestTimeoutSeconds = 15;
        private const int MaxConcurrentDownloads = 2;
        private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36";

        private readonly List<VideoItem> _videos = new List<VideoItem>();
        private readonly List<ResultItemView> _resultViews = new List<ResultItemView>();
        private readonly Queue<DownloadJob> _downloadQueue = new Queue<DownloadJob>();
        private readonly List<DownloadJob> _activeDownloads = new List<DownloadJob>();
        private readonly Dictionary<string, Coroutine> _downloadRoutines = new Dictionary<string, Coroutine>();

        private Sprite _roundedSprite;
        private Sprite _circleSprite;
        private Sprite _thumbnailPlaceholder;
        private Sprite _unsupportedPlaceholder;
        private Sprite _logoSprite;

        private Canvas _canvas;
        private RectTransform _appRoot;
        private TMP_InputField _urlInput;
        private Button _searchButton;
        private TextMeshProUGUI _searchButtonLabel;
        private TextMeshProUGUI _statusText;
        private TextMeshProUGUI _inlineMessage;
        private TextMeshProUGUI _resultCountText;
        private ScrollRect _resultsScrollRect;
        private RectTransform _resultContent;
        private GameObject _emptyState;
        private GameObject _skeletonState;
        private TextMeshProUGUI _footerPathText;
        private TextMeshProUGUI _networkStatusText;
        private GameObject _modalOverlay;
        private TMP_InputField _pathInput;
        private TextMeshProUGUI _modalMessage;
        private TextMeshProUGUI _toastText;
        private CanvasGroup _toastGroup;

        private Coroutine _resolveRoutine;
        private Coroutine _toastRoutine;
        private string _lastDirectory;
        private VideoItem _pendingFolderVideo;
        private float _lastNetworkProbeTime;
        private bool _networkReachable = true;

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

        private void Awake()
        {
            Application.runInBackground = true;

            if (!Application.isEditor)
            {
                Screen.SetResolution(560, 720, false);
            }

            _lastDirectory = PlayerPrefs.GetString("Vidow.LastDownloadDirectory", GetDefaultDownloadDirectory());
            CreateSprites();
            EnsureEventSystem();
            var designPreview = GameObject.Find("Vidow Design Preview Canvas");
            if (designPreview != null)
            {
                designPreview.SetActive(false);
            }

            BuildInterface();
            SetStatus("Ready");
            SetInlineMessage(string.Empty, TextMuted);
            UpdateFooterPath();
            StartCoroutine(FocusUrlInputNextFrame());
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (_modalOverlay != null && _modalOverlay.activeSelf)
                {
                    ConfirmFallbackPath();
                }
                else
                {
                    StartSearch();
                }
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_modalOverlay != null && _modalOverlay.activeSelf)
                {
                    ClosePathModal();
                }
                else if (_resolveRoutine != null)
                {
                    StopCoroutine(_resolveRoutine);
                    _resolveRoutine = null;
                    SetResolvingUi(false);
                    SetStatus("Ready");
                    SetInlineMessage("Search cancelled.", TextMuted);
                }
            }

            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.L))
            {
                _urlInput.Select();
                _urlInput.ActivateInputField();
            }

            if (Time.unscaledTime - _lastNetworkProbeTime > 2.5f)
            {
                _lastNetworkProbeTime = Time.unscaledTime;
                _networkReachable = Application.internetReachability != NetworkReachability.NotReachable;
                _networkStatusText.text = _networkReachable ? "Online" : "Offline";
                _networkStatusText.color = _networkReachable ? Success : Warning;
            }
        }

        private void OnDestroy()
        {
            foreach (var job in _activeDownloads)
            {
                job.CancelRequested = true;
            }
        }

        private void BuildInterface()
        {
            var canvasGo = new GameObject("Vidow Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 1000;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(560, 720);
            scaler.matchWidthOrHeight = 0.5f;

            _appRoot = CreateRect("App Root", canvasGo.transform);
            Stretch(_appRoot);
            AddImage(_appRoot.gameObject, Background);

            var main = CreateRect("Main Column", _appRoot);
            Stretch(main, 16, 16, 16, 14);
            var mainLayout = main.gameObject.AddComponent<VerticalLayoutGroup>();
            mainLayout.padding = new RectOffset(0, 0, 0, 0);
            mainLayout.spacing = 12;
            mainLayout.childControlWidth = true;
            mainLayout.childControlHeight = true;
            mainLayout.childForceExpandWidth = true;
            mainLayout.childForceExpandHeight = false;

            CreateHeader(main);
            CreateSearchPanel(main);
            CreateInlineMessage(main);
            CreateResultsHeader(main);
            CreateResultsArea(main);
            CreateFooter(main);
            CreatePathModal(canvasGo.transform);
            CreateToast(canvasGo.transform);
        }

        private void CreateHeader(Transform parent)
        {
            var header = CreateRect("Header", parent);
            AddLayoutElement(header.gameObject, -1, 58);
            var layout = header.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var logo = CreateRect("Logo", header);
            AddLayoutElement(logo.gameObject, 42, 42);
            var logoImage = AddImage(logo.gameObject, Color.white);
            logoImage.sprite = _logoSprite;

            var titleGroup = CreateRect("Title Group", header);
            AddLayoutElement(titleGroup.gameObject, -1, 52, 1);
            var titleLayout = titleGroup.gameObject.AddComponent<VerticalLayoutGroup>();
            titleLayout.spacing = 0;
            titleLayout.childAlignment = TextAnchor.MiddleLeft;
            titleLayout.childControlHeight = true;
            titleLayout.childControlWidth = true;
            titleLayout.childForceExpandHeight = false;
            titleLayout.childForceExpandWidth = true;

            var title = CreateText("Vidow", titleGroup, 20, FontStyles.Bold, TextPrimary);
            title.alignment = TextAlignmentOptions.Left;
            AddLayoutElement(title.gameObject, -1, 27);

            _statusText = CreateText("Ready", titleGroup, 12, FontStyles.Normal, TextSecondary);
            _statusText.alignment = TextAlignmentOptions.Left;
            AddLayoutElement(_statusText.gameObject, -1, 20);
        }

        private void CreateSearchPanel(Transform parent)
        {
            var searchPanel = CreateRect("Search Panel", parent);
            AddLayoutElement(searchPanel.gameObject, -1, 48);
            var layout = searchPanel.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            var inputFrame = CreateRect("URL Input Frame", searchPanel);
            AddLayoutElement(inputFrame.gameObject, -1, 44, 1);
            AddImage(inputFrame.gameObject, Surface).raycastTarget = false;
            var inputFrameLayout = inputFrame.gameObject.AddComponent<HorizontalLayoutGroup>();
            inputFrameLayout.padding = new RectOffset(12, 6, 0, 0);
            inputFrameLayout.spacing = 8;
            inputFrameLayout.childAlignment = TextAnchor.MiddleCenter;
            inputFrameLayout.childControlWidth = true;
            inputFrameLayout.childControlHeight = true;
            inputFrameLayout.childForceExpandWidth = false;
            inputFrameLayout.childForceExpandHeight = true;

            var linkIcon = CreateText("link", inputFrame, 12, FontStyles.Bold, Accent);
            linkIcon.text = "link";
            linkIcon.alignment = TextAlignmentOptions.Center;
            AddLayoutElement(linkIcon.gameObject, 34, 40);

            _urlInput = CreateInput(inputFrame, "Paste a video page or direct media URL");
            AddLayoutElement(_urlInput.gameObject, -1, 40, 1);
            _urlInput.onValueChanged.AddListener(_ => UpdateSearchButtonState());
            _urlInput.onSubmit.AddListener(_ => StartSearch());
            _urlInput.gameObject.AddComponent<InputFocusForwarder>().Bind(_urlInput);

            var clearButton = CreateButton(inputFrame, "x", "Clear", SurfaceHover, TextSecondary, () =>
            {
                _urlInput.text = string.Empty;
                _urlInput.ActivateInputField();
            });
            AddLayoutElement(clearButton.gameObject, 34, 36);

            _searchButton = CreateButton(searchPanel, "Search", "Search", Accent, Background, StartSearch);
            AddLayoutElement(_searchButton.gameObject, 106, 44);
            _searchButtonLabel = _searchButton.GetComponentInChildren<TextMeshProUGUI>();
            UpdateSearchButtonState();
        }

        private void CreateInlineMessage(Transform parent)
        {
            var messageFrame = CreateRect("Inline Message", parent);
            AddLayoutElement(messageFrame.gameObject, -1, 26);
            _inlineMessage = CreateText(string.Empty, messageFrame, 12, FontStyles.Normal, TextMuted);
            Stretch(_inlineMessage.rectTransform);
            _inlineMessage.alignment = TextAlignmentOptions.Left;
        }

        private void CreateResultsHeader(Transform parent)
        {
            var header = CreateRect("Results Header", parent);
            AddLayoutElement(header.gameObject, -1, 30);
            var layout = header.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.spacing = 8;

            _resultCountText = CreateText("No videos yet", header, 13, FontStyles.Bold, TextSecondary);
            _resultCountText.alignment = TextAlignmentOptions.Left;
            AddLayoutElement(_resultCountText.gameObject, -1, 26, 1);

            var clear = CreateButton(header, "Clear", "Clear results", SurfaceHover, TextSecondary, ClearResults);
            AddLayoutElement(clear.gameObject, 74, 28);
        }

        private void CreateResultsArea(Transform parent)
        {
            var scrollRoot = CreateRect("Results Scroll View", parent);
            AddLayoutElement(scrollRoot.gameObject, -1, -1, 1, 1);
            AddImage(scrollRoot.gameObject, Color.clear);

            _resultsScrollRect = scrollRoot.gameObject.AddComponent<ScrollRect>();
            _resultsScrollRect.horizontal = false;
            _resultsScrollRect.vertical = true;
            _resultsScrollRect.movementType = ScrollRect.MovementType.Clamped;
            _resultsScrollRect.scrollSensitivity = 24;

            var viewport = CreateRect("Viewport", scrollRoot);
            Stretch(viewport);
            var viewportImage = AddImage(viewport.gameObject, Color.clear);
            viewportImage.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();
            _resultsScrollRect.viewport = viewport;

            _resultContent = CreateRect("Content", viewport);
            _resultContent.anchorMin = new Vector2(0, 1);
            _resultContent.anchorMax = new Vector2(1, 1);
            _resultContent.pivot = new Vector2(0.5f, 1);
            _resultContent.anchoredPosition = Vector2.zero;
            _resultContent.sizeDelta = new Vector2(0, 0);
            var contentLayout = _resultContent.gameObject.AddComponent<VerticalLayoutGroup>();
            contentLayout.spacing = 8;
            contentLayout.padding = new RectOffset(0, 0, 2, 16);
            contentLayout.childControlWidth = true;
            contentLayout.childControlHeight = true;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            _resultContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _resultsScrollRect.content = _resultContent;

            _emptyState = CreateEmptyState(_resultContent);
            _skeletonState = CreateSkeletonState(_resultContent);
            _skeletonState.SetActive(false);
        }

        private void CreateFooter(Transform parent)
        {
            var footer = CreateRect("Footer", parent);
            AddLayoutElement(footer.gameObject, -1, 36);
            var layout = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;

            var pathChip = CreateRect("Path Chip", footer);
            AddImage(pathChip.gameObject, Surface);
            AddLayoutElement(pathChip.gameObject, -1, 30, 1);
            var pathLayout = pathChip.gameObject.AddComponent<HorizontalLayoutGroup>();
            pathLayout.padding = new RectOffset(10, 10, 0, 0);
            pathLayout.spacing = 6;
            pathLayout.childAlignment = TextAnchor.MiddleLeft;

            var folder = CreateText("folder", pathChip, 11, FontStyles.Bold, Accent);
            folder.text = "folder";
            folder.alignment = TextAlignmentOptions.Center;
            AddLayoutElement(folder.gameObject, 42, 24);

            _footerPathText = CreateText(string.Empty, pathChip, 11, FontStyles.Normal, TextMuted);
            _footerPathText.alignment = TextAlignmentOptions.Left;
            _footerPathText.overflowMode = TextOverflowModes.Ellipsis;
            AddLayoutElement(_footerPathText.gameObject, -1, 24, 1);

            _networkStatusText = CreateText("Online", footer, 11, FontStyles.Bold, Success);
            _networkStatusText.alignment = TextAlignmentOptions.Center;
            AddLayoutElement(_networkStatusText.gameObject, 70, 30);
        }

        private GameObject CreateEmptyState(Transform parent)
        {
            var frame = CreateRect("Empty State", parent);
            AddLayoutElement(frame.gameObject, -1, 280);
            var layout = frame.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 42, 20);
            layout.spacing = 12;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            var thumb = CreateRect("Empty Thumbnail", frame);
            AddLayoutElement(thumb.gameObject, 180, 102);
            var image = AddImage(thumb.gameObject, Color.white);
            image.sprite = _thumbnailPlaceholder;

            var title = CreateText("Paste a link to find downloadable videos.", frame, 16, FontStyles.Bold, TextPrimary);
            title.alignment = TextAlignmentOptions.Center;
            AddLayoutElement(title.gameObject, -1, 28);

            var subtitle = CreateText("Direct media URLs and permitted public video sources work best.", frame, 12, FontStyles.Normal, TextMuted);
            subtitle.alignment = TextAlignmentOptions.Center;
            subtitle.textWrappingMode = TextWrappingModes.Normal;
            AddLayoutElement(subtitle.gameObject, 360, 48);
            return frame.gameObject;
        }

        private GameObject CreateSkeletonState(Transform parent)
        {
            var group = CreateRect("Skeleton State", parent);
            AddLayoutElement(group.gameObject, -1, 292);
            var layout = group.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;

            for (var i = 0; i < 3; i++)
            {
                var bar = CreateRect("Skeleton Result", group);
                AddLayoutElement(bar.gameObject, -1, 88);
                AddImage(bar.gameObject, Surface);
                var text = CreateText("Analyzing link...", bar, 13, FontStyles.Normal, TextMuted);
                Stretch(text.rectTransform, 18, 18, 18, 18);
                text.alignment = TextAlignmentOptions.MidlineLeft;
            }

            return group.gameObject;
        }

        private void CreatePathModal(Transform parent)
        {
            _modalOverlay = CreateRect("Path Modal Overlay", parent).gameObject;
            Stretch(_modalOverlay.GetComponent<RectTransform>());
            AddImage(_modalOverlay, new Color(0, 0, 0, 0.64f));
            _modalOverlay.SetActive(false);

            var modal = CreateRect("Path Modal", _modalOverlay.transform);
            modal.anchorMin = new Vector2(0.5f, 0.5f);
            modal.anchorMax = new Vector2(0.5f, 0.5f);
            modal.pivot = new Vector2(0.5f, 0.5f);
            modal.sizeDelta = new Vector2(460, 238);
            AddImage(modal.gameObject, Surface);
            var layout = modal.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(18, 18, 18, 18);
            layout.spacing = 12;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText("Choose download folder", modal, 18, FontStyles.Bold, TextPrimary);
            title.alignment = TextAlignmentOptions.Left;
            AddLayoutElement(title.gameObject, -1, 30);

            _modalMessage = CreateText("Paste or type the folder path where Vidow should save this file.", modal, 12, FontStyles.Normal, TextSecondary);
            _modalMessage.textWrappingMode = TextWrappingModes.Normal;
            AddLayoutElement(_modalMessage.gameObject, -1, 38);

            _pathInput = CreateInput(modal, "C:\\Users\\You\\Downloads");
            AddLayoutElement(_pathInput.gameObject, -1, 44);

            var actions = CreateRect("Modal Actions", modal);
            AddLayoutElement(actions.gameObject, -1, 44);
            var actionLayout = actions.gameObject.AddComponent<HorizontalLayoutGroup>();
            actionLayout.spacing = 8;
            actionLayout.childAlignment = TextAnchor.MiddleRight;
            actionLayout.childControlWidth = false;
            actionLayout.childControlHeight = true;
            actionLayout.childForceExpandWidth = false;

            var spacer = CreateRect("Spacer", actions);
            AddLayoutElement(spacer.gameObject, -1, 40, 1);
            var cancel = CreateButton(actions, "Cancel", "Cancel", SurfaceHover, TextSecondary, ClosePathModal);
            AddLayoutElement(cancel.gameObject, 94, 40);
            var choose = CreateButton(actions, "Choose", "Choose folder", Accent, Background, ConfirmFallbackPath);
            AddLayoutElement(choose.gameObject, 104, 40);
        }

        private void CreateToast(Transform parent)
        {
            var toast = CreateRect("Toast", parent);
            toast.anchorMin = new Vector2(0.5f, 0);
            toast.anchorMax = new Vector2(0.5f, 0);
            toast.pivot = new Vector2(0.5f, 0);
            toast.anchoredPosition = new Vector2(0, 24);
            toast.sizeDelta = new Vector2(360, 42);
            AddImage(toast.gameObject, SurfaceHover);
            _toastGroup = toast.gameObject.AddComponent<CanvasGroup>();
            _toastGroup.alpha = 0;
            _toastGroup.interactable = false;
            _toastGroup.blocksRaycasts = false;
            _toastText = CreateText(string.Empty, toast, 12, FontStyles.Bold, TextPrimary);
            Stretch(_toastText.rectTransform, 14, 14, 8, 8);
            _toastText.alignment = TextAlignmentOptions.Center;
        }

        private void StartSearch()
        {
            var normalized = UrlUtility.Normalize(_urlInput.text);
            if (!UrlUtility.TryValidateHttpUrl(normalized, out var uri, out var error))
            {
                SetInlineMessage(error, Danger);
                SetStatus("Ready");
                _urlInput.ActivateInputField();
                return;
            }

            if (!_networkReachable)
            {
                SetInlineMessage("You appear to be offline. Connect to the internet and try again.", Warning);
                return;
            }

            _urlInput.text = uri.AbsoluteUri;

            if (_resolveRoutine != null)
            {
                StopCoroutine(_resolveRoutine);
            }

            _resolveRoutine = StartCoroutine(ResolveRoutine(uri));
        }

        private IEnumerator ResolveRoutine(Uri uri)
        {
            ClearResults();
            SetResolvingUi(true);
            SetStatus("Analyzing link...");
            SetInlineMessage(string.Empty, TextMuted);

            var deadline = Time.realtimeSinceStartup + ResolveTimeoutSeconds;
            var resolved = false;
            var failed = false;
            var message = string.Empty;
            List<VideoItem> items = null;

            yield return StartCoroutine(ResolveVideos(uri, result =>
            {
                resolved = true;
                failed = result.Status != ResolveStatus.Success;
                message = result.UserMessage;
                items = result.Videos;
            }));

            if (!resolved || Time.realtimeSinceStartup > deadline)
            {
                failed = true;
                message = "This link took too long to analyze. Check the address or try again.";
            }

            SetResolvingUi(false);
            _resolveRoutine = null;

            if (failed || items == null || items.Count == 0)
            {
                _emptyState.SetActive(true);
                SetStatus("Ready");
                SetInlineMessage(string.IsNullOrWhiteSpace(message) ? "No downloadable videos were found." : message, Warning);
                _resultCountText.text = "No videos found";
                yield break;
            }

            foreach (var item in items)
            {
                AddResult(item);
            }

            SetStatus("Ready");
            SetInlineMessage($"{items.Count} video{(items.Count == 1 ? string.Empty : "s")} found.", Success);
            UpdateResultsCount();
        }

        private IEnumerator ResolveVideos(Uri uri, Action<ResolveResult> complete)
        {
            var directByExtension = UrlUtility.IsLikelyDirectVideo(uri.AbsoluteUri);
            var directMimeKnown = false;
            long? directSize = null;
            string directMime = null;

            using (var head = UnityWebRequest.Head(uri.AbsoluteUri))
            {
                head.timeout = 8;
                yield return head.SendWebRequest();

                if (head.result == UnityWebRequest.Result.Success)
                {
                    directMime = head.GetResponseHeader("Content-Type");
                    directSize = ParseNullableLong(head.GetResponseHeader("Content-Length"));
                    directMimeKnown = !string.IsNullOrWhiteSpace(directMime) &&
                                      directMime.IndexOf("video/", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }

            if (directByExtension || directMimeKnown)
            {
                complete(new ResolveResult
                {
                    Status = ResolveStatus.Success,
                    UserMessage = string.Empty,
                    Videos = new List<VideoItem>
                    {
                        VideoItem.FromUrl(uri.AbsoluteUri, directMime, directSize)
                    }
                });
                yield break;
            }

            if (DailymotionResolver.TryGetVideoId(uri, out _))
            {
                yield return StartCoroutine(ResolveDailymotionVideos(uri, complete));
                yield break;
            }

            using (var request = UnityWebRequest.Get(uri.AbsoluteUri))
            {
                request.timeout = RequestTimeoutSeconds;
                TrySetRequestHeader(request, "User-Agent", BrowserUserAgent);
                yield return request.SendWebRequest();

                ResolveResult fallback;
                if (request.result == UnityWebRequest.Result.ConnectionError)
                {
                    fallback = ResolveResult.Fail(ResolveStatus.NetworkError, "Could not reach this link. Check your connection and try again.");
                }
                else if (request.result != UnityWebRequest.Result.Success)
                {
                    fallback = ResolveResult.Fail(ResolveStatus.UnsupportedSource, "This source is not supported for download.");
                }
                else
                {
                    var html = request.downloadHandler.text;
                    var videos = HtmlVideoParser.Extract(uri, html).ToList();
                    if (videos.Count > 0)
                    {
                        complete(new ResolveResult
                        {
                            Status = ResolveStatus.Success,
                            Videos = videos,
                            UserMessage = string.Empty
                        });
                        yield break;
                    }

                    fallback = ResolveResult.Fail(ResolveStatus.NoVideosFound, "No downloadable videos were found.");
                }

                SetStatus("Checking extended sources...");
                yield return StartCoroutine(ResolveExternalVideos(uri, fallback, complete));
            }
        }

        private IEnumerator ResolveExternalVideos(Uri uri, ResolveResult fallback, Action<ResolveResult> complete)
        {
            SetInlineMessage("Preparing YouTube and extended-site support...", TextMuted);
            ExternalToolResult tool = null;
            yield return StartCoroutine(YtDlpBridge.EnsureAvailable(result => tool = result));

            if (tool == null || !tool.Available)
            {
                complete(ResolveResult.Fail(
                    ResolveStatus.UnsupportedSource,
                    tool == null || string.IsNullOrWhiteSpace(tool.Message)
                        ? fallback.UserMessage
                        : tool.Message));
                yield break;
            }

            SetInlineMessage("Reading formats with yt-dlp...", TextMuted);
            ExternalProcessRun run;
            try
            {
                run = YtDlpBridge.StartMetadata(tool.ExecutablePath, uri.AbsoluteUri);
            }
            catch (Exception ex)
            {
                complete(ResolveResult.Fail(ResolveStatus.UnsupportedSource, "yt-dlp could not start: " + ex.Message));
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + 75f;
            while (!run.IsDone)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    run.Cancel();
                    complete(ResolveResult.Fail(ResolveStatus.Timeout, "yt-dlp took too long to analyze this link."));
                    yield break;
                }

                yield return null;
            }

            if (run.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(run.ErrorTail) ? fallback.UserMessage : run.ErrorTail;
                complete(ResolveResult.Fail(ResolveStatus.UnsupportedSource, message));
                yield break;
            }

            var videos = YtDlpBridge.ParseVideoItems(run.StandardOutput, uri.AbsoluteUri);
            if (videos.Count == 0)
            {
                complete(fallback ?? ResolveResult.Fail(ResolveStatus.NoVideosFound, "No downloadable videos were found."));
                yield break;
            }

            complete(new ResolveResult
                {
                    Status = ResolveStatus.Success,
                    Videos = videos,
                    UserMessage = string.Empty
                });
        }

        private IEnumerator ResolveDailymotionVideos(Uri uri, Action<ResolveResult> complete)
        {
            if (!DailymotionResolver.TryGetVideoId(uri, out var videoId))
            {
                complete(ResolveResult.Fail(ResolveStatus.UnsupportedSource, "This Dailymotion link is not supported."));
                yield break;
            }

            var metadataUrl = $"https://www.dailymotion.com/player/metadata/video/{videoId}";
            var requestHeaders = DailymotionResolver.CreateRequestHeaders(uri.AbsoluteUri);
            string metadataJson = null;
            string metadataError = null;
            DailymotionMetadata metadata;
            using (var request = UnityWebRequest.Get(metadataUrl))
            {
                request.timeout = RequestTimeoutSeconds;
                ApplyRequestHeaders(request, requestHeaders);
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.ConnectionError)
                {
                    metadataError = request.error;
                }
                else if (request.result == UnityWebRequest.Result.Success && !string.IsNullOrWhiteSpace(request.downloadHandler.text))
                {
                    metadataJson = request.downloadHandler.text;
                }
                else
                {
                    metadataError = request.error;
                }
            }

            if (string.IsNullOrWhiteSpace(metadataJson) &&
                !CurlUtility.TryGetText(metadataUrl, requestHeaders, RequestTimeoutSeconds, out metadataJson, out metadataError))
            {
                complete(ResolveResult.Fail(ResolveStatus.UnsupportedSource, string.IsNullOrWhiteSpace(metadataError) ? "Dailymotion did not return playable metadata for this link." : metadataError));
                yield break;
            }

            metadata = DailymotionResolver.ParseMetadata(videoId, uri.AbsoluteUri, metadataJson);

            if (metadata.IsPrivate || metadata.IsPasswordProtected || metadata.IsProtectedDelivery)
            {
                complete(ResolveResult.Fail(ResolveStatus.UnsupportedSource, "This Dailymotion video is private, protected, paid, or DRM restricted."));
                yield break;
            }

            if (metadata.Sources.Count == 0)
            {
                complete(ResolveResult.Fail(ResolveStatus.NoVideosFound, "Dailymotion did not expose downloadable streams for this video."));
                yield break;
            }

            var videos = new List<VideoItem>();
            foreach (var source in metadata.Sources)
            {
                if (source.IsHls)
                {
                    List<VideoItem> variants = null;
                    yield return StartCoroutine(ResolveHlsVideoItems(source, metadata, result => variants = result));
                    if (variants != null)
                    {
                        videos.AddRange(variants);
                    }
                }
                else
                {
                    videos.Add(DailymotionResolver.CreateDirectVideoItem(source, metadata));
                }
            }

            videos = videos
                .GroupBy(video => video.MediaUrl, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(video => HlsPlaylistParser.QualityRank(video.QualityLabel))
                .Take(8)
                .ToList();

            if (videos.Count == 0)
            {
                complete(ResolveResult.Fail(ResolveStatus.NoVideosFound, "This Dailymotion video was found, but Vidow could not read its downloadable quality list."));
                yield break;
            }

            complete(new ResolveResult
            {
                Status = ResolveStatus.Success,
                Videos = videos,
                UserMessage = string.Empty
            });
        }

        private IEnumerator ResolveHlsVideoItems(DailymotionMediaSource source, DailymotionMetadata metadata, Action<List<VideoItem>> complete)
        {
            string playlist = null;
            using (var request = UnityWebRequest.Get(source.Url))
            {
                request.timeout = RequestTimeoutSeconds;
                ApplyRequestHeaders(request, source.RequestHeaders);
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success && !string.IsNullOrWhiteSpace(request.downloadHandler.text))
                {
                    playlist = request.downloadHandler.text;
                }
            }

            if (string.IsNullOrWhiteSpace(playlist) &&
                !CurlUtility.TryGetText(source.Url, source.RequestHeaders, RequestTimeoutSeconds, out playlist, out _))
            {
                complete(new List<VideoItem>());
                yield break;
            }

            if (HlsPlaylistParser.ContainsEncryption(playlist))
            {
                complete(new List<VideoItem>());
                yield break;
            }

            var variants = HlsPlaylistParser.ExtractVariants(source.Url, playlist).ToList();
            if (variants.Count == 0)
            {
                complete(new List<VideoItem> { DailymotionResolver.CreateHlsVideoItem(source, metadata, source.QualityLabel) });
                yield break;
            }

            var items = variants
                .Select(variant => DailymotionResolver.CreateHlsVideoItem(source, metadata, variant.QualityLabel, variant.Url, variant.Bandwidth))
                .ToList();
            complete(items);
        }

        private void AddResult(VideoItem item)
        {
            _emptyState.SetActive(false);
            _videos.Add(item);

            var view = ResultItemView.Create(_resultContent, this, item);
            _resultViews.Add(view);
            UpdateResultsCount();
            RefreshResultsLayout(true);
        }

        private void ClearResults()
        {
            foreach (var view in _resultViews)
            {
                if (view != null)
                {
                    Destroy(view.gameObject);
                }
            }

            _resultViews.Clear();
            _videos.Clear();
            _emptyState.SetActive(true);
            _skeletonState.SetActive(false);
            UpdateResultsCount();
            RefreshResultsLayout(true);
        }

        private void BeginDownload(VideoItem item)
        {
            _pendingFolderVideo = item;

#if UNITY_EDITOR
            var folder = EditorUtility.OpenFolderPanel("Choose download folder", _lastDirectory, string.Empty);
            if (string.IsNullOrWhiteSpace(folder))
            {
                ShowToast("Download cancelled.");
                return;
            }

            StartDownloadInFolder(item, folder);
#else
            var pickStatus = WindowsFolderPicker.PickFolder("Choose download folder", _lastDirectory, out var folder);
            if (pickStatus == FolderPickStatus.Success)
            {
                StartDownloadInFolder(item, folder);
            }
            else if (pickStatus == FolderPickStatus.Unavailable || pickStatus == FolderPickStatus.Failed)
            {
                ShowPathModal(item);
            }
            else
            {
                ShowToast("Download cancelled.");
            }
#endif
        }

        private void ShowPathModal(VideoItem item)
        {
            _pendingFolderVideo = item;
            _pathInput.text = string.IsNullOrWhiteSpace(_lastDirectory) ? GetDefaultDownloadDirectory() : _lastDirectory;
            _modalMessage.color = TextSecondary;
            _modalMessage.text = "Paste or type the folder path where Vidow should save this file.";
            _modalOverlay.SetActive(true);
            _pathInput.Select();
            _pathInput.ActivateInputField();
        }

        private void ConfirmFallbackPath()
        {
            if (_pendingFolderVideo == null)
            {
                ClosePathModal();
                return;
            }

            var folder = _pathInput.text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                _modalMessage.color = Danger;
                _modalMessage.text = "This folder does not exist. Choose another location.";
                return;
            }

            if (!SafePath.CanWriteToFolder(folder))
            {
                _modalMessage.color = Danger;
                _modalMessage.text = "Vidow cannot write to this folder. Choose another location.";
                return;
            }

            ClosePathModal();
            StartDownloadInFolder(_pendingFolderVideo, folder);
        }

        private void ClosePathModal()
        {
            _modalOverlay.SetActive(false);
            _pendingFolderVideo = null;
        }

        private void StartDownloadInFolder(VideoItem item, string folder)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.MediaUrl))
            {
                SetInlineMessage("This video cannot be downloaded by Vidow. It may be protected, private, paid, or unsupported.", Warning);
                return;
            }

            Directory.CreateDirectory(folder);
            _lastDirectory = folder;
            PlayerPrefs.SetString("Vidow.LastDownloadDirectory", folder);
            PlayerPrefs.Save();
            UpdateFooterPath();

            var finalPath = SafePath.GetUniqueFilePath(folder, item.SafeFileName);
            var tempPath = item.IsExternal ? YtDlpBridge.GetStagingFilePath(finalPath) : finalPath + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath));
            var job = new DownloadJob(item, folder, finalPath, tempPath);
            var view = _resultViews.FirstOrDefault(v => v.Item.Id == item.Id);
            if (view != null)
            {
                job.View = view;
                view.BindJob(job);
            }

            _downloadQueue.Enqueue(job);
            PumpDownloadQueue();
            ShowToast("Download started.");
        }

        private void PumpDownloadQueue()
        {
            while (_activeDownloads.Count < MaxConcurrentDownloads && _downloadQueue.Count > 0)
            {
                var job = _downloadQueue.Dequeue();
                _activeDownloads.Add(job);
                job.Status = DownloadStatus.Downloading;
                job.View?.SetDownloading(job.Progress);
                var routine = StartCoroutine(DownloadRoutine(job));
                _downloadRoutines[job.JobId] = routine;
            }
        }

        private IEnumerator DownloadRoutine(DownloadJob job)
        {
            var startTime = Time.realtimeSinceStartup;
            long lastBytes = 0;
            var lastSample = startTime;

            try
            {
                if (File.Exists(job.TempFilePath))
                {
                    File.Delete(job.TempFilePath);
                }
            }
            catch (Exception ex)
            {
                FailJob(job, "Vidow cannot write to this folder. Choose another location.", ex.Message);
                yield break;
            }

            if (job.Video.IsHls)
            {
                yield return StartCoroutine(DownloadHlsRoutine(job, startTime, lastBytes, lastSample));
                yield break;
            }

            if (job.Video.IsExternal)
            {
                yield return StartCoroutine(DownloadExternalRoutine(job, startTime, lastBytes, lastSample));
                yield break;
            }

            using (var request = UnityWebRequest.Get(job.Video.MediaUrl))
            {
                request.timeout = 0;
                request.downloadHandler = new DownloadHandlerFile(job.TempFilePath);
                ApplyRequestHeaders(request, job.Video.RequestHeaders);
                var op = request.SendWebRequest();

                while (!op.isDone)
                {
                    if (job.CancelRequested)
                    {
                        request.Abort();
                        CancelJob(job);
                        yield break;
                    }

                    UpdateDownloadProgress(job, request, ref lastBytes, ref lastSample);
                    yield return null;
                }

                UpdateDownloadProgress(job, request, ref lastBytes, ref lastSample);

                if (job.CancelRequested)
                {
                    CancelJob(job);
                    yield break;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    FailJob(job, "The download failed. Check your connection and try again.", request.error);
                    yield break;
                }
            }

            CompleteDownloadedFile(job);
        }

        private IEnumerator DownloadExternalRoutine(DownloadJob job, float startTime, long lastBytes, float lastSample)
        {
            ExternalToolResult tool = null;
            yield return StartCoroutine(YtDlpBridge.EnsureAvailable(result => tool = result));

            if (tool == null || !tool.Available)
            {
                FailJob(job, "yt-dlp is required for this source but could not be installed.", tool?.Message);
                yield break;
            }

            string ffmpegLocation = null;
            if (job.Video.RequiresExternalMuxer)
            {
                SetInlineMessage("Preparing FFmpeg for high-quality merge...", Warning);
                ExternalToolResult ffmpeg = null;
                yield return StartCoroutine(FfmpegBridge.EnsureAvailable(result => ffmpeg = result));

                if (job.CancelRequested)
                {
                    CancelJob(job);
                    yield break;
                }

                if (ffmpeg == null || !ffmpeg.Available)
                {
                    FailJob(job, "FFmpeg is required for this quality but could not be installed.", ffmpeg?.Message);
                    yield break;
                }

                ffmpegLocation = FfmpegBridge.GetLocationArgument(ffmpeg.ExecutablePath);
            }

            ExternalProcessRun run;
            try
            {
                run = YtDlpBridge.StartDownload(tool.ExecutablePath, job.Video, job.TempFilePath, ffmpegLocation);
            }
            catch (Exception ex)
            {
                FailJob(job, "yt-dlp could not start this download.", ex.Message);
                yield break;
            }

            while (!run.IsDone)
            {
                if (job.CancelRequested)
                {
                    run.Cancel();
                    CancelJob(job);
                    yield break;
                }

                UpdateExternalDownloadProgress(job, run, ref lastBytes, ref lastSample);
                yield return null;
            }

            UpdateExternalDownloadProgress(job, run, ref lastBytes, ref lastSample);

            if (job.CancelRequested)
            {
                CancelJob(job);
                yield break;
            }

            if (run.ExitCode != 0)
            {
                FailJob(job, ExternalDownloadFailureMessage(run.ErrorTail), run.ErrorTail);
                yield break;
            }

            var externalOutputPath = ResolveExternalOutputPath(job.TempFilePath, run);
            if (string.IsNullOrWhiteSpace(externalOutputPath))
            {
                FailJob(job, ExternalDownloadFailureMessage(run.ErrorTail, "The external downloader finished but did not create a video file."), run.ErrorTail);
                yield break;
            }

            CompleteDownloadedFile(job, externalOutputPath);
        }

        private IEnumerator DownloadHlsRoutine(DownloadJob job, float startTime, long lastBytes, float lastSample)
        {
            List<string> segments = null;
            var playlistError = string.Empty;
            yield return StartCoroutine(ResolveHlsSegments(job.Video, (resolvedSegments, error) =>
            {
                segments = resolvedSegments;
                playlistError = error;
            }));

            if (job.CancelRequested)
            {
                CancelJob(job);
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(playlistError) || segments == null || segments.Count == 0)
            {
                FailJob(job, "Vidow could not read this HLS video stream.", playlistError);
                yield break;
            }

            var cancelled = false;
            var failed = false;
            var technicalError = string.Empty;
            long bytesWritten = 0;
            FileStream output;

            try
            {
                output = new FileStream(job.TempFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex)
            {
                FailJob(job, "Vidow cannot write to this folder. Choose another location.", ex.Message);
                yield break;
            }

            using (output)
            {
                for (var i = 0; i < segments.Count; i++)
                {
                    if (job.CancelRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    using (var request = UnityWebRequest.Get(segments[i]))
                    {
                        request.timeout = 0;
                        request.downloadHandler = new DownloadHandlerBuffer();
                        ApplyRequestHeaders(request, job.Video.RequestHeaders);
                        var op = request.SendWebRequest();

                        while (!op.isDone)
                        {
                            if (job.CancelRequested)
                            {
                                request.Abort();
                                cancelled = true;
                                break;
                            }

                            UpdateHlsDownloadProgress(job, i, segments.Count, bytesWritten + unchecked((long)request.downloadedBytes), ref lastBytes, ref lastSample);
                            yield return null;
                        }

                        if (cancelled)
                        {
                            break;
                        }

                        if (request.result != UnityWebRequest.Result.Success)
                        {
                            failed = true;
                            technicalError = request.error;
                            break;
                        }

                        var data = request.downloadHandler.data;
                        if (data == null || data.Length == 0)
                        {
                            failed = true;
                            technicalError = "An HLS media segment was empty.";
                            break;
                        }

                        try
                        {
                            output.Write(data, 0, data.Length);
                        }
                        catch (Exception ex)
                        {
                            failed = true;
                            technicalError = ex.Message;
                            break;
                        }

                        bytesWritten += data.LongLength;
                        UpdateHlsDownloadProgress(job, i + 1, segments.Count, bytesWritten, ref lastBytes, ref lastSample);
                    }
                }
            }

            if (cancelled)
            {
                CancelJob(job);
                yield break;
            }

            if (failed)
            {
                FailJob(job, "The HLS download failed. Check your connection and try again.", technicalError);
                yield break;
            }

            job.Progress.Percent = 1f;
            job.Progress.BytesDownloaded = bytesWritten;
            CompleteDownloadedFile(job);
        }

        private IEnumerator ResolveHlsSegments(VideoItem video, Action<List<string>, string> complete)
        {
            var playlistUrl = video.MediaUrl;

            for (var depth = 0; depth < 3; depth++)
            {
                string playlist = null;
                string requestError = null;
                using (var request = UnityWebRequest.Get(playlistUrl))
                {
                    request.timeout = RequestTimeoutSeconds;
                    ApplyRequestHeaders(request, video.RequestHeaders);
                    yield return request.SendWebRequest();

                    if (request.result == UnityWebRequest.Result.Success && !string.IsNullOrWhiteSpace(request.downloadHandler.text))
                    {
                        playlist = request.downloadHandler.text;
                    }
                    else
                    {
                        requestError = request.error;
                    }
                }

                if (string.IsNullOrWhiteSpace(playlist) &&
                    !CurlUtility.TryGetText(playlistUrl, video.RequestHeaders, RequestTimeoutSeconds, out playlist, out requestError))
                {
                    complete(null, requestError);
                    yield break;
                }

                if (HlsPlaylistParser.ContainsEncryption(playlist))
                {
                    complete(null, "Encrypted HLS streams are not supported.");
                    yield break;
                }

                var variants = HlsPlaylistParser.ExtractVariants(playlistUrl, playlist).ToList();
                if (variants.Count > 0)
                {
                    playlistUrl = variants.OrderByDescending(variant => variant.Bandwidth ?? 0).First().Url;
                    continue;
                }

                var segments = HlsPlaylistParser.ExtractSegments(playlistUrl, playlist).ToList();
                complete(segments, segments.Count == 0 ? "No HLS media segments were found." : string.Empty);
                yield break;
            }

            complete(null, "The HLS playlist redirects too deeply.");
        }

        private void UpdateHlsDownloadProgress(DownloadJob job, int completedSegments, int totalSegments, long bytes, ref long lastBytes, ref float lastSample)
        {
            var now = Time.realtimeSinceStartup;

            if (now - lastSample >= 0.25f)
            {
                var deltaBytes = bytes - lastBytes;
                var deltaTime = Mathf.Max(0.01f, now - lastSample);
                job.Progress.BytesPerSecond = Math.Max(0, deltaBytes / deltaTime);
                lastBytes = bytes;
                lastSample = now;
            }

            job.Progress.BytesDownloaded = Math.Max(0, bytes);
            job.Progress.TotalBytes = job.Video.SizeBytes;
            job.Progress.Percent = totalSegments > 0 ? Mathf.Clamp01((float)completedSegments / totalSegments) : (float?)null;
            job.View?.SetDownloading(job.Progress);
        }

        private void UpdateExternalDownloadProgress(DownloadJob job, ExternalProcessRun run, ref long lastBytes, ref float lastSample)
        {
            var now = Time.realtimeSinceStartup;
            var bytes = FindDownloadBytes(job.TempFilePath);

            if (now - lastSample >= 0.25f)
            {
                var deltaBytes = bytes - lastBytes;
                var deltaTime = Mathf.Max(0.01f, now - lastSample);
                job.Progress.BytesPerSecond = Math.Max(0, deltaBytes / deltaTime);
                lastBytes = bytes;
                lastSample = now;
            }

            job.Progress.BytesDownloaded = Math.Max(0, bytes);
            job.Progress.TotalBytes = job.Video.SizeBytes;
            if (run != null && run.Percent.HasValue)
            {
                job.Progress.Percent = run.Percent.Value;
            }
            else
            {
                job.Progress.Percent = job.Video.SizeBytes.HasValue && job.Video.SizeBytes.Value > 0
                    ? Mathf.Clamp01((float)bytes / job.Video.SizeBytes.Value)
                    : (float?)null;
            }

            job.View?.SetDownloading(job.Progress);
        }

        private static long FindDownloadBytes(string tempFilePath)
        {
            try
            {
                if (File.Exists(tempFilePath))
                {
                    return new FileInfo(tempFilePath).Length;
                }

                var partPath = tempFilePath + ".part";
                if (File.Exists(partPath))
                {
                    return new FileInfo(partPath).Length;
                }
            }
            catch
            {
                return 0;
            }

            return 0;
        }

        private static string ResolveExternalOutputPath(string requestedOutputPath, ExternalProcessRun run)
        {
            if (File.Exists(requestedOutputPath))
            {
                return requestedOutputPath;
            }

            var log = ((run?.StandardOutput ?? string.Empty) + "\n" + (run?.StandardError ?? string.Empty)).Trim();
            foreach (Match match in Regex.Matches(log, "(?:Destination:|Merging formats into)\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase))
            {
                var path = match.Groups["path"].Value;
                if (File.Exists(path))
                {
                    return path;
                }
            }

            try
            {
                var directory = Path.GetDirectoryName(requestedOutputPath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return null;
                }

                var requestedName = Path.GetFileName(requestedOutputPath);
                var requestedBaseName = Path.GetFileNameWithoutExtension(requestedOutputPath);
                return Directory.GetFiles(directory, requestedName + ".*")
                    .Concat(Directory.GetFiles(directory, requestedBaseName + ".*"))
                    .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    .Where(path => new FileInfo(path).Length > 0)
                    .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static string ExternalDownloadFailureMessage(string details, string fallback = "The external downloader failed for this source.")
        {
            details = Regex.Replace(details ?? string.Empty, "\\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(details))
            {
                return fallback;
            }

            if (details.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                details.IndexOf("ffprobe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                details.IndexOf("merg", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "FFmpeg could not finish the merge for this format. Try again or choose a ready MP4 option.";
            }

            if (details.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0 ||
                details.IndexOf("access is denied", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Windows blocked the external downloader from writing this file. Try another folder.";
            }

            if (details.IndexOf("private", StringComparison.OrdinalIgnoreCase) >= 0 ||
                details.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                details.IndexOf("sign in", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "This source needs sign-in or is not publicly downloadable.";
            }

            if (details.IndexOf("copyright", StringComparison.OrdinalIgnoreCase) >= 0 ||
                details.IndexOf("drm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                details.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "This source is protected and cannot be downloaded by Vidow.";
            }

            return details.Length > 150 ? details.Substring(0, 150) + "..." : details;
        }

        private void CompleteDownloadedFile(DownloadJob job, string sourceFilePath = null)
        {
            try
            {
                var sourcePath = string.IsNullOrWhiteSpace(sourceFilePath) ? job.TempFilePath : sourceFilePath;
                if (File.Exists(job.FinalFilePath))
                {
                    File.Delete(job.FinalFilePath);
                }

                File.Move(sourcePath, job.FinalFilePath);
                job.Status = DownloadStatus.Completed;
                job.Progress.Percent = 1f;
                job.View?.SetCompleted(job.FinalFilePath);
                ShowToast("Download complete.");
                FinishJob(job);
            }
            catch (Exception ex)
            {
                FailJob(job, "Vidow cannot write to this folder. Choose another location.", ex.Message);
            }
        }

        private void UpdateDownloadProgress(DownloadJob job, UnityWebRequest request, ref long lastBytes, ref float lastSample)
        {
            var now = Time.realtimeSinceStartup;
            var bytes = unchecked((long)request.downloadedBytes);
            var total = job.Video.SizeBytes;

            if (!total.HasValue)
            {
                var header = request.GetResponseHeader("Content-Length");
                total = ParseNullableLong(header);
                if (total.HasValue && total.Value > 0)
                {
                    job.Video.SizeBytes = total;
                }
            }

            if (now - lastSample >= 0.25f)
            {
                var deltaBytes = bytes - lastBytes;
                var deltaTime = Mathf.Max(0.01f, now - lastSample);
                job.Progress.BytesPerSecond = Math.Max(0, deltaBytes / deltaTime);
                lastBytes = bytes;
                lastSample = now;
            }

            job.Progress.BytesDownloaded = Math.Max(0, bytes);
            job.Progress.TotalBytes = total;
            job.Progress.Percent = total.HasValue && total.Value > 0 ? Mathf.Clamp01((float)bytes / total.Value) : (float?)null;
            if (job.Progress.Percent.HasValue && job.Progress.BytesPerSecond > 1)
            {
                var remaining = total.Value - bytes;
                job.Progress.EstimatedTimeRemaining = TimeSpan.FromSeconds(Math.Max(0, remaining / job.Progress.BytesPerSecond));
            }

            job.View?.SetDownloading(job.Progress);
        }

        private void FailJob(DownloadJob job, string userMessage, string technicalMessage)
        {
            job.Status = DownloadStatus.Failed;
            job.ErrorMessage = userMessage;
            job.TechnicalMessage = technicalMessage;
            CleanupTemp(job);
            job.View?.SetFailed(userMessage);
            ShowToast("Download failed.");
            FinishJob(job);
        }

        private void CancelJob(DownloadJob job)
        {
            job.Status = DownloadStatus.Cancelled;
            CleanupTemp(job);
            job.View?.SetCancelled();
            ShowToast("Download cancelled.");
            FinishJob(job);
        }

        private void FinishJob(DownloadJob job)
        {
            _activeDownloads.Remove(job);
            _downloadRoutines.Remove(job.JobId);
            PumpDownloadQueue();
        }

        private static void CleanupTemp(DownloadJob job)
        {
            try
            {
                if (File.Exists(job.TempFilePath))
                {
                    File.Delete(job.TempFilePath);
                }

                var partPath = job.TempFilePath + ".part";
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        private void SetResolvingUi(bool resolving)
        {
            _searchButton.interactable = !resolving;
            _searchButtonLabel.text = resolving ? "..." : "Search";
            _skeletonState.SetActive(resolving);
            _emptyState.SetActive(!resolving && _resultViews.Count == 0);
            RefreshResultsLayout(false);
        }

        private void RefreshResultsLayout(bool scrollToTop)
        {
            if (_resultContent == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_resultContent);

            if (scrollToTop && _resultsScrollRect != null)
            {
                _resultsScrollRect.verticalNormalizedPosition = 1f;
            }
        }

        private IEnumerator FocusUrlInputNextFrame()
        {
            yield return null;
            _urlInput.Select();
            _urlInput.ActivateInputField();
        }

        private void UpdateSearchButtonState()
        {
            if (_searchButton == null)
            {
                return;
            }

            _searchButton.interactable = !string.IsNullOrWhiteSpace(_urlInput.text);
        }

        private void UpdateResultsCount()
        {
            if (_videos.Count == 0)
            {
                _resultCountText.text = "No videos yet";
            }
            else
            {
                _resultCountText.text = $"{_videos.Count} video{(_videos.Count == 1 ? string.Empty : "s")} found";
            }
        }

        private void SetStatus(string status)
        {
            _statusText.text = status;
        }

        private void SetInlineMessage(string message, Color color)
        {
            _inlineMessage.text = message;
            _inlineMessage.color = color;
        }

        private void UpdateFooterPath()
        {
            _footerPathText.text = string.IsNullOrWhiteSpace(_lastDirectory) ? "No folder selected" : AbbreviatePath(_lastDirectory, 48);
        }

        private void ShowToast(string message)
        {
            if (_toastRoutine != null)
            {
                StopCoroutine(_toastRoutine);
            }

            _toastRoutine = StartCoroutine(ToastRoutine(message));
        }

        private IEnumerator ToastRoutine(string message)
        {
            _toastText.text = message;
            _toastGroup.alpha = 0;

            for (var t = 0f; t < 1f; t += Time.unscaledDeltaTime / 0.16f)
            {
                _toastGroup.alpha = Mathf.SmoothStep(0, 1, t);
                yield return null;
            }

            _toastGroup.alpha = 1;
            yield return new WaitForSecondsRealtime(1.7f);

            for (var t = 0f; t < 1f; t += Time.unscaledDeltaTime / 0.16f)
            {
                _toastGroup.alpha = Mathf.SmoothStep(1, 0, t);
                yield return null;
            }

            _toastGroup.alpha = 0;
        }

        private void CreateSprites()
        {
            _roundedSprite = RuntimeSprites.CreateRoundedRect(64, 64, 12);
            _circleSprite = RuntimeSprites.CreateCircle(64);
            _thumbnailPlaceholder = RuntimeSprites.CreateThumbnailPlaceholder(false);
            _unsupportedPlaceholder = RuntimeSprites.CreateThumbnailPlaceholder(true);
            _logoSprite = RuntimeSprites.CreateLogo();
        }

        private static void EnsureEventSystem()
        {
            var eventSystem = UnityEngine.Object.FindFirstObjectByType<EventSystem>();
            if (eventSystem == null)
            {
                var eventSystemGo = new GameObject("EventSystem", typeof(EventSystem));
                UnityEngine.Object.DontDestroyOnLoad(eventSystemGo);
                eventSystem = eventSystemGo.GetComponent<EventSystem>();
            }

#if ENABLE_INPUT_SYSTEM
            var inputSystemModule = eventSystem.GetComponent<InputSystemUIInputModule>();
            if (inputSystemModule == null)
            {
                inputSystemModule = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
            }

            inputSystemModule.AssignDefaultActions();

            foreach (var legacyModule in eventSystem.GetComponents<StandaloneInputModule>())
            {
                legacyModule.enabled = false;
            }
#else
            if (eventSystem.GetComponent<StandaloneInputModule>() == null)
            {
                eventSystem.gameObject.AddComponent<StandaloneInputModule>();
            }
#endif
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private Image AddImage(GameObject go, Color color)
        {
            var image = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            image.color = color;
            image.sprite = _roundedSprite;
            image.type = Image.Type.Sliced;
            return image;
        }

        private TextMeshProUGUI CreateText(string text, Transform parent, float size, FontStyles style, Color color)
        {
            var rect = CreateRect("Text", parent);
            var tmp = rect.gameObject.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.color = color;
            tmp.raycastTarget = false;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            return tmp;
        }

        private TMP_InputField CreateInput(Transform parent, string placeholder)
        {
            var root = CreateRect("Input", parent);
            var inputGraphic = AddImage(root.gameObject, SurfaceHover);

            var viewport = CreateRect("Text Area", root);
            Stretch(viewport, 12, 12, 4, 4);
            var mask = viewport.gameObject.AddComponent<RectMask2D>();

            var text = CreateText(string.Empty, viewport, 14, FontStyles.Normal, TextPrimary);
            Stretch(text.rectTransform);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;

            var placeholderText = CreateText(placeholder, viewport, 14, FontStyles.Normal, TextMuted);
            Stretch(placeholderText.rectTransform);
            placeholderText.alignment = TextAlignmentOptions.MidlineLeft;

            var input = root.gameObject.AddComponent<TMP_InputField>();
            input.targetGraphic = inputGraphic;
            input.textViewport = viewport;
            input.textComponent = text;
            input.placeholder = placeholderText;
            input.caretColor = Accent;
            input.selectionColor = new Color(0.2f, 0.76f, 1f, 0.25f);
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.characterValidation = TMP_InputField.CharacterValidation.None;
            return input;
        }

        private Button CreateButton(Transform parent, string label, string tooltip, Color background, Color textColor, Action clicked)
        {
            var rect = CreateRect(label + " Button", parent);
            var image = AddImage(rect.gameObject, background);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = background;
            colors.highlightedColor = Color.Lerp(background, Color.white, 0.08f);
            colors.pressedColor = Color.Lerp(background, Color.black, 0.1f);
            colors.disabledColor = new Color(background.r, background.g, background.b, 0.42f);
            colors.fadeDuration = 0.1f;
            button.colors = colors;
            button.onClick.AddListener(() => clicked?.Invoke());

            var text = CreateText(label, rect, 13, FontStyles.Bold, textColor);
            Stretch(text.rectTransform, 8, 8, 2, 2);
            text.alignment = TextAlignmentOptions.Center;
            return button;
        }

        private static void AddLayoutElement(GameObject go, float width = -1, float height = -1, float flexibleWidth = 0, float flexibleHeight = 0)
        {
            var layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            if (width >= 0) layout.preferredWidth = width;
            if (height >= 0) layout.preferredHeight = height;
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

        private static Color Hex(string hex)
        {
            if (ColorUtility.TryParseHtmlString("#" + hex, out var color))
            {
                return color;
            }

            return Color.white;
        }

        private static long? ParseNullableLong(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            return null;
        }

        private static void ApplyRequestHeaders(UnityWebRequest request, IDictionary<string, string> headers)
        {
            if (request == null || headers == null)
            {
                return;
            }

            foreach (var header in headers)
            {
                TrySetRequestHeader(request, header.Key, header.Value);
            }
        }

        private static void TrySetRequestHeader(UnityWebRequest request, string name, string value)
        {
            if (request == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            try
            {
                request.SetRequestHeader(name, value);
            }
            catch
            {
                // Unity blocks a few browser-owned headers on some platforms.
            }
        }

        private static string GetDefaultDownloadDirectory()
        {
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var downloads = Path.Combine(user, "Downloads");
            return Directory.Exists(downloads) ? downloads : Application.persistentDataPath;
        }

        private static string AbbreviatePath(string path, int max)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length <= max)
            {
                return path;
            }

            var file = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var root = Path.GetPathRoot(path);
            var tail = string.IsNullOrWhiteSpace(file) ? path.Substring(Math.Max(0, path.Length - max + 3)) : file;
            return $"{root}...{Path.DirectorySeparatorChar}{tail}";
        }

        public sealed class ResultItemView : MonoBehaviour
        {
            public VideoItem Item { get; private set; }

            private VidowApp _app;
            private Image _thumbnail;
            private TextMeshProUGUI _title;
            private TextMeshProUGUI _metadata;
            private TextMeshProUGUI _status;
            private TextMeshProUGUI _progressText;
            private Image _progressFill;
            private Button _primaryButton;
            private TextMeshProUGUI _primaryButtonText;
            private Button _secondaryButton;
            private TextMeshProUGUI _secondaryButtonText;
            private DownloadJob _job;

            public static ResultItemView Create(Transform parent, VidowApp app, VideoItem item)
            {
                var root = CreateRect("Result Item", parent);
                AddLayoutElement(root.gameObject, -1, 94);
                app.AddImage(root.gameObject, Surface);
                var view = root.gameObject.AddComponent<ResultItemView>();
                view._app = app;
                view.Item = item;
                view.Build(root);
                view.SetReady();
                return view;
            }

            private void Build(RectTransform root)
            {
                var layout = root.gameObject.AddComponent<HorizontalLayoutGroup>();
                layout.padding = new RectOffset(10, 10, 10, 10);
                layout.spacing = 10;
                layout.childAlignment = TextAnchor.MiddleCenter;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = false;

                var thumbnailRect = CreateRect("Thumbnail", root);
                AddLayoutElement(thumbnailRect.gameObject, 104, 58);
                _thumbnail = _app.AddImage(thumbnailRect.gameObject, Color.white);
                _thumbnail.sprite = Item.IsDownloadable ? _app._thumbnailPlaceholder : _app._unsupportedPlaceholder;
                if (!string.IsNullOrWhiteSpace(Item.ThumbnailUrl))
                {
                    StartCoroutine(LoadThumbnailRoutine(Item.ThumbnailUrl));
                }

                var textColumn = CreateRect("Text Column", root);
                AddLayoutElement(textColumn.gameObject, -1, 74, 1);
                var textLayout = textColumn.gameObject.AddComponent<VerticalLayoutGroup>();
                textLayout.spacing = 3;
                textLayout.childControlWidth = true;
                textLayout.childControlHeight = true;
                textLayout.childForceExpandWidth = true;
                textLayout.childForceExpandHeight = false;

                _title = _app.CreateText(Item.Title, textColumn, 14, FontStyles.Bold, TextPrimary);
                _title.alignment = TextAlignmentOptions.Left;
                AddLayoutElement(_title.gameObject, -1, 22);

                _metadata = _app.CreateText(Item.Metadata, textColumn, 11, FontStyles.Normal, TextSecondary);
                _metadata.alignment = TextAlignmentOptions.Left;
                AddLayoutElement(_metadata.gameObject, -1, 18);

                _status = _app.CreateText("Ready", textColumn, 11, FontStyles.Bold, Success);
                _status.alignment = TextAlignmentOptions.Left;
                AddLayoutElement(_status.gameObject, -1, 18);

                var progressTrack = CreateRect("Progress Track", textColumn);
                AddLayoutElement(progressTrack.gameObject, -1, 5);
                _app.AddImage(progressTrack.gameObject, ProgressTrack);
                _progressFill = _app.AddImage(CreateRect("Progress Fill", progressTrack).gameObject, Accent);
                _progressFill.rectTransform.anchorMin = new Vector2(0, 0);
                _progressFill.rectTransform.anchorMax = new Vector2(0, 1);
                _progressFill.rectTransform.pivot = new Vector2(0, 0.5f);
                _progressFill.rectTransform.offsetMin = Vector2.zero;
                _progressFill.rectTransform.offsetMax = Vector2.zero;
                progressTrack.gameObject.SetActive(false);

                _progressText = _app.CreateText(string.Empty, textColumn, 11, FontStyles.Normal, TextMuted);
                _progressText.alignment = TextAlignmentOptions.Left;
                AddLayoutElement(_progressText.gameObject, -1, 16);
                _progressText.gameObject.SetActive(false);

                var actions = CreateRect("Actions", root);
                AddLayoutElement(actions.gameObject, 112, 72);
                var actionLayout = actions.gameObject.AddComponent<VerticalLayoutGroup>();
                actionLayout.spacing = 7;
                actionLayout.childControlWidth = true;
                actionLayout.childControlHeight = false;
                actionLayout.childForceExpandWidth = true;
                actionLayout.childForceExpandHeight = false;

                _primaryButton = _app.CreateButton(actions, "Download", "Download", Accent, Background, () =>
                {
                    if (_job != null && _job.Status == DownloadStatus.Downloading)
                    {
                        _job.CancelRequested = true;
                    }
                    else if (_job != null && _job.Status == DownloadStatus.Completed)
                    {
                        PlatformActions.OpenFolder(_job.TargetDirectory);
                    }
                    else
                    {
                        _app.BeginDownload(Item);
                    }
                });
                _primaryButtonText = _primaryButton.GetComponentInChildren<TextMeshProUGUI>();
                AddLayoutElement(_primaryButton.gameObject, -1, 34);

                _secondaryButton = _app.CreateButton(actions, "Copy", "Copy path", SurfaceHover, TextSecondary, () =>
                {
                    if (_job != null && _job.Status == DownloadStatus.Completed)
                    {
                        GUIUtility.systemCopyBuffer = _job.FinalFilePath;
                        _app.ShowToast("Path copied.");
                    }
                });
                _secondaryButtonText = _secondaryButton.GetComponentInChildren<TextMeshProUGUI>();
                AddLayoutElement(_secondaryButton.gameObject, -1, 30);
                _secondaryButton.gameObject.SetActive(false);
            }

            public void BindJob(DownloadJob job)
            {
                _job = job;
            }

            public void SetReady()
            {
                _status.text = Item.RequiresExternalMuxer ? "Merge" : Item.IsDownloadable ? "Ready" : "Unsupported";
                _status.color = Item.RequiresExternalMuxer ? Warning : Item.IsDownloadable ? Success : Warning;
                _primaryButton.interactable = Item.IsDownloadable;
                _primaryButtonText.text = Item.IsDownloadable ? "Download" : "N/A";
                _secondaryButton.gameObject.SetActive(false);
                _progressText.gameObject.SetActive(false);
                _progressFill.transform.parent.gameObject.SetActive(false);
            }

            public void SetDownloading(DownloadProgress progress)
            {
                _status.text = "Downloading";
                _status.color = Accent;
                _primaryButtonText.text = "Cancel";
                _primaryButton.interactable = true;
                _secondaryButton.gameObject.SetActive(false);
                _progressText.gameObject.SetActive(true);
                _progressFill.transform.parent.gameObject.SetActive(true);

                if (progress.Percent.HasValue)
                {
                    _progressFill.rectTransform.anchorMax = new Vector2(progress.Percent.Value, 1);
                }
                else
                {
                    _progressFill.rectTransform.anchorMax = new Vector2(0.34f + Mathf.PingPong(Time.unscaledTime * 0.25f, 0.32f), 1);
                }

                _progressText.text = progress.Format();
            }

            public void SetCompleted(string path)
            {
                _status.text = "Completed";
                _status.color = Success;
                _progressFill.rectTransform.anchorMax = new Vector2(1, 1);
                _progressText.gameObject.SetActive(true);
                _progressText.text = Path.GetFileName(path);
                _primaryButtonText.text = "Open";
                _primaryButton.interactable = true;
                _secondaryButtonText.text = "Copy";
                _secondaryButton.gameObject.SetActive(true);
            }

            public void SetFailed(string message)
            {
                _status.text = "Failed";
                _status.color = Danger;
                _progressText.gameObject.SetActive(true);
                _progressText.text = message;
                _primaryButtonText.text = "Retry";
                _secondaryButton.gameObject.SetActive(false);
            }

            public void SetCancelled()
            {
                _status.text = "Cancelled";
                _status.color = Warning;
                _progressText.gameObject.SetActive(false);
                _progressFill.transform.parent.gameObject.SetActive(false);
                _primaryButtonText.text = "Download";
                _secondaryButton.gameObject.SetActive(false);
            }

            private IEnumerator LoadThumbnailRoutine(string url)
            {
                using (var request = UnityWebRequestTexture.GetTexture(url))
                {
                    request.timeout = 8;
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        yield break;
                    }

                    var texture = DownloadHandlerTexture.GetContent(request);
                    if (texture == null || texture.width <= 0 || texture.height <= 0)
                    {
                        yield break;
                    }

                    _thumbnail.sprite = Sprite.Create(
                        texture,
                        new Rect(0, 0, texture.width, texture.height),
                        new Vector2(0.5f, 0.5f),
                        100);
                }
            }
        }
    }

    public sealed class InputFocusForwarder : MonoBehaviour, IPointerClickHandler
    {
        private TMP_InputField _input;

        public void Bind(TMP_InputField input)
        {
            _input = input;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_input == null)
            {
                _input = GetComponent<TMP_InputField>();
            }

            if (_input == null)
            {
                return;
            }

            _input.Select();
            _input.ActivateInputField();
            _input.MoveTextEnd(false);
        }
    }

    public sealed class VideoItem
    {
        public string Id;
        public string PageUrl;
        public string MediaUrl;
        public string Title;
        public string SourceDomain;
        public string ThumbnailUrl;
        public string Extension;
        public string MimeType;
        public string QualityLabel;
        public long? SizeBytes;
        public TimeSpan? Duration;
        public bool IsHls;
        public bool IsExternal;
        public bool RequiresExternalMuxer;
        public bool IsDownloadable = true;
        public string UnsupportedReason;
        public Dictionary<string, string> RequestHeaders = new Dictionary<string, string>();
        public string ExternalSourceUrl;
        public string ExternalFormatSelector;
        public string ExternalToolName;

        public string SafeFileName => SafePath.SanitizeFileName($"{Title}.{(string.IsNullOrWhiteSpace(Extension) ? "mp4" : Extension.TrimStart('.'))}");

        public string Metadata
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(SourceDomain)) parts.Add(SourceDomain);
                if (!string.IsNullOrWhiteSpace(ExternalToolName)) parts.Add(ExternalToolName);
                if (RequiresExternalMuxer) parts.Add("Needs FFmpeg");
                if (Duration.HasValue) parts.Add(TimeFormatter.Format(Duration.Value));
                if (!string.IsNullOrWhiteSpace(Extension)) parts.Add(Extension.TrimStart('.').ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(QualityLabel)) parts.Add(QualityLabel);
                if (SizeBytes.HasValue) parts.Add(ByteFormatter.Format(SizeBytes.Value));
                return string.Join(" - ", parts);
            }
        }

        public static VideoItem FromUrl(string url, string mimeType, long? sizeBytes)
        {
            var uri = new Uri(url);
            var fileName = Path.GetFileNameWithoutExtension(uri.LocalPath);
            var extension = Path.GetExtension(uri.LocalPath);
            return new VideoItem
            {
                Id = Guid.NewGuid().ToString("N"),
                PageUrl = url,
                MediaUrl = url,
                Title = string.IsNullOrWhiteSpace(fileName) ? uri.Host : Uri.UnescapeDataString(fileName).Replace('-', ' ').Replace('_', ' '),
                SourceDomain = uri.Host,
                MimeType = mimeType,
                SizeBytes = sizeBytes,
                Extension = string.IsNullOrWhiteSpace(extension) ? MimeToExtension(mimeType) : extension.TrimStart('.'),
                QualityLabel = "Source"
            };
        }

        private static string MimeToExtension(string mimeType)
        {
            if (string.IsNullOrWhiteSpace(mimeType)) return "mp4";
            if (mimeType.IndexOf("webm", StringComparison.OrdinalIgnoreCase) >= 0) return "webm";
            if (mimeType.IndexOf("ogg", StringComparison.OrdinalIgnoreCase) >= 0) return "ogv";
            if (mimeType.IndexOf("quicktime", StringComparison.OrdinalIgnoreCase) >= 0) return "mov";
            return "mp4";
        }
    }

    public sealed class ResolveResult
    {
        public ResolveStatus Status;
        public List<VideoItem> Videos = new List<VideoItem>();
        public string UserMessage;
        public string TechnicalMessage;

        public static ResolveResult Fail(ResolveStatus status, string message)
        {
            return new ResolveResult
            {
                Status = status,
                UserMessage = message,
                Videos = new List<VideoItem>()
            };
        }
    }

    public enum ResolveStatus
    {
        Success,
        InvalidUrl,
        NoVideosFound,
        UnsupportedSource,
        NetworkError,
        Timeout,
        Cancelled,
        Failed
    }

    public enum DownloadStatus
    {
        Queued,
        Downloading,
        Completed,
        Failed,
        Cancelled
    }

    public sealed class DownloadJob
    {
        public readonly string JobId = Guid.NewGuid().ToString("N");
        public readonly VideoItem Video;
        public readonly string TargetDirectory;
        public readonly string FinalFilePath;
        public readonly string TempFilePath;
        public readonly DownloadProgress Progress = new DownloadProgress();
        public VidowApp.ResultItemView View;
        public DownloadStatus Status = DownloadStatus.Queued;
        public bool CancelRequested;
        public string ErrorMessage;
        public string TechnicalMessage;

        public DownloadJob(VideoItem video, string targetDirectory, string finalFilePath, string tempFilePath)
        {
            Video = video;
            TargetDirectory = targetDirectory;
            FinalFilePath = finalFilePath;
            TempFilePath = tempFilePath;
        }
    }

    public sealed class DownloadProgress
    {
        public long BytesDownloaded;
        public long? TotalBytes;
        public float? Percent;
        public double BytesPerSecond;
        public TimeSpan? EstimatedTimeRemaining;

        public string Format()
        {
            var speed = ByteFormatter.Format((long)Math.Max(0, BytesPerSecond)) + "/s";
            if (Percent.HasValue && TotalBytes.HasValue)
            {
                var eta = EstimatedTimeRemaining.HasValue ? $" - ~{TimeFormatter.FormatShort(EstimatedTimeRemaining.Value)} left" : string.Empty;
                return $"{Mathf.RoundToInt(Percent.Value * 100)}% - {ByteFormatter.Format(BytesDownloaded)} / {ByteFormatter.Format(TotalBytes.Value)} - {speed}{eta}";
            }

            if (Percent.HasValue)
            {
                return $"{Mathf.RoundToInt(Percent.Value * 100)}% - {ByteFormatter.Format(BytesDownloaded)} downloaded - {speed}";
            }

            return $"{ByteFormatter.Format(BytesDownloaded)} downloaded - {speed}";
        }
    }

    public sealed class DailymotionMetadata
    {
        public string VideoId;
        public string PageUrl;
        public string Title;
        public string ThumbnailUrl;
        public TimeSpan? Duration;
        public bool IsPrivate;
        public bool IsPasswordProtected;
        public bool IsProtectedDelivery;
        public List<DailymotionMediaSource> Sources = new List<DailymotionMediaSource>();
    }

    public sealed class DailymotionMediaSource
    {
        public string VideoId;
        public string PageUrl;
        public string Url;
        public string MimeType;
        public string QualityLabel;
        public bool IsHls;
        public Dictionary<string, string> RequestHeaders = new Dictionary<string, string>();
    }

    public static class CurlUtility
    {
        public static bool TryGetText(string url, IDictionary<string, string> headers, int timeoutSeconds, out string text, out string error)
        {
            text = null;
            error = null;

            if (string.IsNullOrWhiteSpace(url))
            {
                error = "Missing URL.";
                return false;
            }

            try
            {
                var arguments = new List<string>
                {
                    "-sSLf",
                    "--max-time",
                    Mathf.Max(1, timeoutSeconds).ToString(CultureInfo.InvariantCulture)
                };

                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(header.Value))
                        {
                            arguments.Add("-H");
                            arguments.Add($"{header.Key}: {header.Value}");
                        }
                    }
                }

                arguments.Add(url);

                var processInfo = new ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "curl.exe" : "curl",
                    Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processInfo))
                {
                    if (process == null)
                    {
                        error = "curl.exe could not be started.";
                        return false;
                    }

                    text = process.StandardOutput.ReadToEnd();
                    error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit((Mathf.Max(1, timeoutSeconds) + 3) * 1000))
                    {
                        TryKill(process);
                        error = "curl.exe timed out.";
                        return false;
                    }

                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(text))
                    {
                        if (string.IsNullOrWhiteSpace(error))
                        {
                            error = $"curl.exe exited with code {process.ExitCode}.";
                        }

                        return false;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    public static class DailymotionResolver
    {
        private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36";
        private static readonly Regex VideoPathRegex = new Regex(@"^/(?:video|embed/video)/(?<id>[A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryGetVideoId(Uri uri, out string videoId)
        {
            videoId = null;
            if (uri == null)
            {
                return false;
            }

            var host = uri.Host.ToLowerInvariant();
            if (host == "dai.ly" || host.EndsWith(".dai.ly", StringComparison.OrdinalIgnoreCase))
            {
                var id = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    videoId = id;
                    return true;
                }
            }

            if (!host.EndsWith("dailymotion.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var match = VideoPathRegex.Match(uri.AbsolutePath);
            if (match.Success)
            {
                videoId = match.Groups["id"].Value;
                return true;
            }

            return false;
        }

        public static DailymotionMetadata ParseMetadata(string videoId, string pageUrl, string json)
        {
            var metadata = new DailymotionMetadata
            {
                VideoId = videoId,
                PageUrl = pageUrl,
                Title = NormalizeTitle(JsonValue(json, "title")) ?? $"Dailymotion {videoId}",
                ThumbnailUrl = ExtractBestThumbnail(json),
                IsPrivate = JsonBool(json, "private"),
                IsPasswordProtected = JsonBool(json, "is_password_protected"),
                IsProtectedDelivery = JsonBool(json, "protected_delivery")
            };

            var durationSeconds = JsonNumber(json, "duration");
            if (durationSeconds.HasValue && durationSeconds.Value > 0)
            {
                metadata.Duration = TimeSpan.FromSeconds(durationSeconds.Value);
            }

            foreach (var source in ExtractSources(videoId, pageUrl, json))
            {
                metadata.Sources.Add(source);
            }

            return metadata;
        }

        public static Dictionary<string, string> CreateRequestHeaders(string pageUrl)
        {
            return new Dictionary<string, string>
            {
                { "User-Agent", BrowserUserAgent },
                { "Accept", "*/*" },
                { "Referer", string.IsNullOrWhiteSpace(pageUrl) ? "https://www.dailymotion.com/" : pageUrl },
                { "Origin", "https://www.dailymotion.com" },
                { "X-Requested-With", "XMLHttpRequest" }
            };
        }

        public static VideoItem CreateDirectVideoItem(DailymotionMediaSource source, DailymotionMetadata metadata)
        {
            var item = VideoItem.FromUrl(source.Url, source.MimeType, null);
            item.Id = Guid.NewGuid().ToString("N");
            item.PageUrl = metadata.PageUrl;
            item.Title = AppendQuality(metadata.Title, source.QualityLabel);
            item.SourceDomain = "dailymotion.com";
            item.ThumbnailUrl = metadata.ThumbnailUrl;
            item.Duration = metadata.Duration;
            item.QualityLabel = NormalizeQualityLabel(source.QualityLabel);
            item.RequestHeaders = new Dictionary<string, string>(source.RequestHeaders);
            return item;
        }

        public static VideoItem CreateHlsVideoItem(DailymotionMediaSource source, DailymotionMetadata metadata, string qualityLabel, string mediaUrl = null, int? bandwidth = null)
        {
            var normalizedQuality = NormalizeQualityLabel(qualityLabel);
            var displayQuality = normalizedQuality.IndexOf("hls", StringComparison.OrdinalIgnoreCase) >= 0 ? normalizedQuality : $"{normalizedQuality} HLS";
            var item = new VideoItem
            {
                Id = Guid.NewGuid().ToString("N"),
                PageUrl = metadata.PageUrl,
                MediaUrl = string.IsNullOrWhiteSpace(mediaUrl) ? source.Url : mediaUrl,
                Title = AppendQuality(metadata.Title, normalizedQuality),
                SourceDomain = "dailymotion.com",
                ThumbnailUrl = metadata.ThumbnailUrl,
                Extension = "ts",
                MimeType = "video/mp2t",
                QualityLabel = displayQuality,
                Duration = metadata.Duration,
                IsHls = true,
                RequestHeaders = new Dictionary<string, string>(source.RequestHeaders)
            };

            if (bandwidth.HasValue && metadata.Duration.HasValue)
            {
                item.SizeBytes = (long)Math.Max(0, metadata.Duration.Value.TotalSeconds * bandwidth.Value / 8d);
            }

            return item;
        }

        private static IEnumerable<DailymotionMediaSource> ExtractSources(string videoId, string pageUrl, string json)
        {
            var qualities = ExtractJsonObject(json, "qualities");
            if (string.IsNullOrWhiteSpace(qualities))
            {
                yield break;
            }

            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in ExtractQualityBlocks(qualities))
            {
                foreach (var itemJson in ExtractJsonObjects(block.ArrayJson))
                {
                    var url = JsonValue(itemJson, "url");
                    if (string.IsNullOrWhiteSpace(url) || !urls.Add(url))
                    {
                        continue;
                    }

                    var mimeType = JsonValue(itemJson, "type");
                    yield return new DailymotionMediaSource
                    {
                        VideoId = videoId,
                        PageUrl = pageUrl,
                        Url = url,
                        MimeType = mimeType,
                        IsHls = IsHls(mimeType, url),
                        QualityLabel = NormalizeQualityLabel(block.Quality),
                        RequestHeaders = CreateRequestHeaders(pageUrl)
                    };
                }
            }
        }

        private static bool IsHls(string mimeType, string url)
        {
            return (!string.IsNullOrWhiteSpace(mimeType) && mimeType.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (!string.IsNullOrWhiteSpace(url) && url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string AppendQuality(string title, string quality)
        {
            quality = NormalizeQualityLabel(quality);
            if (string.IsNullOrWhiteSpace(quality) || quality == "Source")
            {
                return title;
            }

            return $"{title} ({quality})";
        }

        private static string NormalizeQualityLabel(string quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
            {
                return "Source";
            }

            quality = quality.Trim();
            if (quality.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return "Auto";
            }

            return Regex.IsMatch(quality, @"^\d+$") ? quality + "p" : quality;
        }

        private static string NormalizeTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Regex.Replace(value, "\\s+", " ").Trim();
        }

        private static string ExtractBestThumbnail(string json)
        {
            var thumbnails = ExtractJsonObject(json, "thumbnails");
            if (string.IsNullOrWhiteSpace(thumbnails))
            {
                return null;
            }

            var bestSize = -1;
            string bestUrl = null;
            foreach (Match match in Regex.Matches(thumbnails, "\"(?<size>\\d+)\"\\s*:\\s*\"(?<url>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Singleline))
            {
                var url = JsonUnescape(match.Groups["url"].Value);
                if (int.TryParse(match.Groups["size"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size > bestSize)
                {
                    bestSize = size;
                    bestUrl = url;
                }
            }

            return bestUrl;
        }

        private static IEnumerable<QualityBlock> ExtractQualityBlocks(string qualitiesJson)
        {
            var index = 0;
            while (index < qualitiesJson.Length)
            {
                var match = Regex.Match(qualitiesJson.Substring(index), "\"(?<quality>[^\"\\\\]+)\"\\s*:");
                if (!match.Success)
                {
                    yield break;
                }

                var quality = JsonUnescape(match.Groups["quality"].Value);
                var cursor = SkipWhitespace(qualitiesJson, index + match.Index + match.Length);
                if (cursor >= qualitiesJson.Length || qualitiesJson[cursor] != '[')
                {
                    index = cursor + 1;
                    continue;
                }

                var end = FindMatching(qualitiesJson, cursor, '[', ']');
                if (end <= cursor)
                {
                    yield break;
                }

                yield return new QualityBlock
                {
                    Quality = quality,
                    ArrayJson = qualitiesJson.Substring(cursor + 1, end - cursor - 1)
                };
                index = end + 1;
            }
        }

        private static IEnumerable<string> ExtractJsonObjects(string arrayJson)
        {
            var index = 0;
            while (index < arrayJson.Length)
            {
                var start = arrayJson.IndexOf('{', index);
                if (start < 0)
                {
                    yield break;
                }

                var end = FindMatching(arrayJson, start, '{', '}');
                if (end <= start)
                {
                    yield break;
                }

                yield return arrayJson.Substring(start, end - start + 1);
                index = end + 1;
            }
        }

        private static string JsonValue(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Singleline);
            return match.Success ? JsonUnescape(match.Groups["value"].Value) : null;
        }

        private static bool JsonBool(string json, string key)
        {
            var match = Regex.Match(json ?? string.Empty, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<value>true|false)", RegexOptions.IgnoreCase);
            return match.Success && match.Groups["value"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static double? JsonNumber(string json, string key)
        {
            var match = Regex.Match(json ?? string.Empty, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)", RegexOptions.IgnoreCase);
            if (match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return null;
        }

        private static string ExtractJsonObject(string json, string key)
        {
            var match = Regex.Match(json ?? string.Empty, "\"" + Regex.Escape(key) + "\"\\s*:");
            if (!match.Success)
            {
                return null;
            }

            var start = SkipWhitespace(json, match.Index + match.Length);
            if (start >= json.Length || json[start] != '{')
            {
                return null;
            }

            var end = FindMatching(json, start, '{', '}');
            return end > start ? json.Substring(start + 1, end - start - 1) : null;
        }

        private static int SkipWhitespace(string value, int index)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }

            return index;
        }

        private static int FindMatching(string value, int start, char open, char close)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = start; i < value.Length; i++)
            {
                var c = value[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == open)
                {
                    depth++;
                }
                else if (c == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static string JsonUnescape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(c);
                    continue;
                }

                var next = value[++i];
                switch (next)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(next);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (i + 4 < value.Length &&
                            int.TryParse(value.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            builder.Append((char)code);
                            i += 4;
                        }
                        break;
                    default:
                        builder.Append(next);
                        break;
                }
            }

            return builder.ToString();
        }

        private sealed class QualityBlock
        {
            public string Quality;
            public string ArrayJson;
        }
    }

    public sealed class HlsVariant
    {
        public string Url;
        public string QualityLabel;
        public int? Bandwidth;
    }

    public static class HlsPlaylistParser
    {
        public static IEnumerable<HlsVariant> ExtractVariants(string playlistUrl, string playlist)
        {
            string pendingInfo = null;
            foreach (var rawLine in ReadLines(playlist))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                {
                    pendingInfo = line.Substring(line.IndexOf(':') + 1);
                    continue;
                }

                if (pendingInfo == null || string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var attributes = ParseAttributes(pendingInfo);
                var bandwidth = GetInt(attributes, "BANDWIDTH");
                yield return new HlsVariant
                {
                    Url = ResolveUrl(playlistUrl, line),
                    QualityLabel = BuildQualityLabel(attributes),
                    Bandwidth = bandwidth
                };
                pendingInfo = null;
            }
        }

        public static IEnumerable<string> ExtractSegments(string playlistUrl, string playlist)
        {
            foreach (var rawLine in ReadLines(playlist))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
                {
                    var attributes = ParseAttributes(line.Substring(line.IndexOf(':') + 1));
                    if (attributes.TryGetValue("URI", out var mapUri) && !string.IsNullOrWhiteSpace(mapUri))
                    {
                        yield return ResolveUrl(playlistUrl, mapUri);
                    }

                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return ResolveUrl(playlistUrl, line);
            }
        }

        public static bool ContainsEncryption(string playlist)
        {
            foreach (var rawLine in ReadLines(playlist))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase) &&
                    line.IndexOf("METHOD=NONE", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return true;
                }
            }

            return false;
        }

        public static int QualityRank(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return 0;
            }

            var match = Regex.Match(label, @"(?<height>\d{3,4})p", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups["height"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            {
                return height;
            }

            return 0;
        }

        private static IEnumerable<string> ReadLines(string value)
        {
            using (var reader = new StringReader(value ?? string.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    yield return line;
                }
            }
        }

        private static string ResolveUrl(string baseUrl, string value)
        {
            try
            {
                return new Uri(new Uri(baseUrl), value.Trim()).AbsoluteUri;
            }
            catch
            {
                return value.Trim();
            }
        }

        private static string BuildQualityLabel(Dictionary<string, string> attributes)
        {
            if (attributes.TryGetValue("RESOLUTION", out var resolution))
            {
                var match = Regex.Match(resolution, @"x(?<height>\d+)$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups["height"].Value + "p";
                }
            }

            if (attributes.TryGetValue("NAME", out var name) && !string.IsNullOrWhiteSpace(name))
            {
                return Regex.IsMatch(name, @"^\d+$") ? name + "p" : name;
            }

            var bandwidth = GetInt(attributes, "BANDWIDTH");
            if (bandwidth.HasValue)
            {
                return Mathf.RoundToInt(bandwidth.Value / 1000f) + "kbps";
            }

            return "HLS";
        }

        private static int? GetInt(Dictionary<string, string> attributes, string key)
        {
            if (attributes.TryGetValue(key, out var value) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static Dictionary<string, string> ParseAttributes(string value)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            while (index < value.Length)
            {
                while (index < value.Length && (char.IsWhiteSpace(value[index]) || value[index] == ','))
                {
                    index++;
                }

                var keyStart = index;
                while (index < value.Length && value[index] != '=' && value[index] != ',')
                {
                    index++;
                }

                if (index >= value.Length || value[index] != '=')
                {
                    break;
                }

                var key = value.Substring(keyStart, index - keyStart).Trim();
                index++;

                string attributeValue;
                if (index < value.Length && value[index] == '"')
                {
                    index++;
                    var builder = new StringBuilder();
                    var escaped = false;
                    while (index < value.Length)
                    {
                        var c = value[index++];
                        if (escaped)
                        {
                            builder.Append(c);
                            escaped = false;
                        }
                        else if (c == '\\')
                        {
                            escaped = true;
                        }
                        else if (c == '"')
                        {
                            break;
                        }
                        else
                        {
                            builder.Append(c);
                        }
                    }

                    attributeValue = builder.ToString();
                }
                else
                {
                    var valueStart = index;
                    while (index < value.Length && value[index] != ',')
                    {
                        index++;
                    }

                    attributeValue = value.Substring(valueStart, index - valueStart).Trim();
                }

                if (!string.IsNullOrWhiteSpace(key))
                {
                    result[key] = attributeValue;
                }
            }

            return result;
        }
    }

    public static class HtmlVideoParser
    {
        private static readonly Regex SourceRegex = new Regex("<(?:video|source)[^>]+src\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnchorRegex = new Regex("<a[^>]+href\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MetaVideoRegex = new Regex("<meta[^>]+(?:property|name)\\s*=\\s*[\"'](?:og:video|og:video:url|twitter:player:stream)[\"'][^>]+content\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MetaVideoReverseRegex = new Regex("<meta[^>]+content\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]+(?:property|name)\\s*=\\s*[\"'](?:og:video|og:video:url|twitter:player:stream)[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PosterRegex = new Regex("<video[^>]+poster\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TitleRegex = new Regex("<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        public static IEnumerable<VideoItem> Extract(Uri pageUri, string html)
        {
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<VideoItem>();
            var title = ExtractTitle(html) ?? pageUri.Host;
            var poster = ResolveOptional(pageUri, FirstMatch(PosterRegex, html));

            foreach (var url in ExtractUrls(pageUri, html))
            {
                if (!urls.Add(url))
                {
                    continue;
                }

                if (!UrlUtility.IsLikelyDirectVideo(url))
                {
                    continue;
                }

                var item = VideoItem.FromUrl(url, null, null);
                item.PageUrl = pageUri.AbsoluteUri;
                item.Title = result.Count == 0 ? title : $"{title} #{result.Count + 1}";
                item.ThumbnailUrl = poster;
                result.Add(item);
            }

            return result.Take(30);
        }

        private static IEnumerable<string> ExtractUrls(Uri pageUri, string html)
        {
            foreach (Match match in SourceRegex.Matches(html))
            {
                var resolved = ResolveOptional(pageUri, match.Groups["url"].Value);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    yield return resolved;
                }
            }

            foreach (Match match in MetaVideoRegex.Matches(html))
            {
                var resolved = ResolveOptional(pageUri, match.Groups["url"].Value);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    yield return resolved;
                }
            }

            foreach (Match match in MetaVideoReverseRegex.Matches(html))
            {
                var resolved = ResolveOptional(pageUri, match.Groups["url"].Value);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    yield return resolved;
                }
            }

            foreach (Match match in AnchorRegex.Matches(html))
            {
                var resolved = ResolveOptional(pageUri, match.Groups["url"].Value);
                if (!string.IsNullOrWhiteSpace(resolved) && UrlUtility.IsLikelyDirectVideo(resolved))
                {
                    yield return resolved;
                }
            }
        }

        private static string ExtractTitle(string html)
        {
            var value = FirstMatch(TitleRegex, html);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Regex.Replace(UnityWebRequest.UnEscapeURL(value), "\\s+", " ").Trim();
        }

        private static string FirstMatch(Regex regex, string html)
        {
            var match = regex.Match(html ?? string.Empty);
            return match.Success ? match.Groups["url"].Success ? match.Groups["url"].Value : match.Groups["title"].Value : null;
        }

        private static string ResolveOptional(Uri pageUri, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                return new Uri(pageUri, value.Trim()).AbsoluteUri;
            }
            catch
            {
                return null;
            }
        }
    }

    public static class UrlUtility
    {
        private static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mov", ".m4v", ".ogg", ".ogv" };

        public static string Normalize(string input)
        {
            input = (input ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return input;
            }

            if (!input.Contains("://", StringComparison.Ordinal) && input.Contains(".", StringComparison.Ordinal))
            {
                return "https://" + input;
            }

            return input;
        }

        public static bool TryValidateHttpUrl(string input, out Uri uri, out string error)
        {
            uri = null;
            error = null;

            if (string.IsNullOrWhiteSpace(input))
            {
                error = "Enter a valid http or https URL.";
                return false;
            }

            if (!Uri.TryCreate(input, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "Enter a valid http or https URL.";
                return false;
            }

            return true;
        }

        public static bool IsLikelyDirectVideo(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var path = uri.AbsolutePath.ToLowerInvariant();
            return VideoExtensions.Any(path.EndsWith);
        }
    }

    public static class ByteFormatter
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        public static string Format(long bytes)
        {
            double value = Math.Max(0, bytes);
            var unit = 0;
            while (value >= 1024 && unit < Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{value:0} {Units[unit]}" : $"{value:0.0} {Units[unit]}";
        }
    }

    public static class TimeFormatter
    {
        public static string Format(TimeSpan time)
        {
            return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
        }

        public static string FormatShort(TimeSpan time)
        {
            if (time.TotalHours >= 1) return $"{Mathf.CeilToInt((float)time.TotalHours)}h";
            if (time.TotalMinutes >= 1) return $"{Mathf.CeilToInt((float)time.TotalMinutes)}m";
            return $"{Mathf.CeilToInt((float)Math.Max(1, time.TotalSeconds))}s";
        }
    }

    public static class SafePath
    {
        public static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string((value ?? "video").Select(c => invalid.Contains(c) ? '-' : c).ToArray());
            sanitized = Regex.Replace(sanitized, "\\s+", " ").Trim(' ', '.', '-');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "video";
            }

            return sanitized.Length > 120 ? sanitized.Substring(0, 120) : sanitized;
        }

        public static string GetUniqueFilePath(string folder, string fileName)
        {
            var path = Path.Combine(folder, fileName);
            if (!File.Exists(path))
            {
                return path;
            }

            var name = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            for (var i = 1; i < 1000; i++)
            {
                path = Path.Combine(folder, $"{name} ({i}){extension}");
                if (!File.Exists(path))
                {
                    return path;
                }
            }

            return Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMddHHmmss}{extension}");
        }

        public static bool CanWriteToFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var test = Path.Combine(folder, ".vidow-write-test");
                File.WriteAllText(test, "ok");
                File.Delete(test);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static class PlatformActions
    {
        public static void OpenFolder(string folder)
        {
            try
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folder}\"",
                    UseShellExecute = true
                });
#else
                Application.OpenURL("file://" + folder);
#endif
            }
            catch
            {
                Application.OpenURL("file://" + folder);
            }
        }
    }

    public static class WindowsFolderPicker
    {
        private const uint BifReturnOnlyFileSystemDirs = 0x0001;
        private const uint BifNewDialogStyle = 0x0040;
        private const uint BffmInitialized = 1;
        private const uint BffmSetSelectionW = 0x0467;

        private delegate int BrowseCallbackProc(IntPtr hwnd, uint message, IntPtr lParam, IntPtr lpData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BrowseInfo
        {
            public IntPtr Owner;
            public IntPtr Root;
            public IntPtr DisplayName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string Title;
            public uint Flags;
            public BrowseCallbackProc Callback;
            public IntPtr Param;
            public int Image;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHBrowseForFolder(ref BrowseInfo browseInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder path);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pointer);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, string lParam);

        public static FolderPickStatus PickFolder(string title, string initialDirectory, out string folder)
        {
            folder = null;

            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor)
            {
                return FolderPickStatus.Unavailable;
            }

            var displayName = IntPtr.Zero;
            var initialPath = IntPtr.Zero;
            var pidl = IntPtr.Zero;
            BrowseCallbackProc callback = BrowseCallback;

            try
            {
                displayName = Marshal.AllocHGlobal(520);
                initialPath = Marshal.StringToHGlobalUni(initialDirectory ?? string.Empty);

                var browseInfo = new BrowseInfo
                {
                    Owner = IntPtr.Zero,
                    Root = IntPtr.Zero,
                    DisplayName = displayName,
                    Title = title,
                    Flags = BifReturnOnlyFileSystemDirs | BifNewDialogStyle,
                    Callback = callback,
                    Param = initialPath,
                    Image = 0
                };

                pidl = SHBrowseForFolder(ref browseInfo);
                if (pidl == IntPtr.Zero)
                {
                    return FolderPickStatus.Cancelled;
                }

                var path = new StringBuilder(1024);
                if (!SHGetPathFromIDList(pidl, path))
                {
                    return FolderPickStatus.Failed;
                }

                folder = path.ToString();
                return Directory.Exists(folder) && SafePath.CanWriteToFolder(folder)
                    ? FolderPickStatus.Success
                    : FolderPickStatus.Failed;
            }
            catch
            {
                return FolderPickStatus.Failed;
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                {
                    CoTaskMemFree(pidl);
                }

                if (displayName != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(displayName);
                }

                if (initialPath != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(initialPath);
                }
            }
        }

        private static int BrowseCallback(IntPtr hwnd, uint message, IntPtr lParam, IntPtr lpData)
        {
            if (message == BffmInitialized && lpData != IntPtr.Zero)
            {
                var path = Marshal.PtrToStringUni(lpData);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    SendMessage(hwnd, BffmSetSelectionW, new IntPtr(1), path);
                }
            }

            return 0;
        }
    }

    public enum FolderPickStatus
    {
        Success,
        Cancelled,
        Unavailable,
        Failed
    }

    public static class RuntimeSprites
    {
        public static Sprite CreateRoundedRect(int width, int height, int radius)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.name = "Vidow Rounded Rect";
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var alpha = RoundedAlpha(x, y, width, height, radius);
                    tex.SetPixel(x, y, new Color(1, 1, 1, alpha));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        }

        public static Sprite CreateCircle(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = new Vector2(size / 2f, size / 2f);
            var radius = size / 2f - 1;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Vector2.Distance(new Vector2(x, y), center);
                    var alpha = Mathf.Clamp01(radius + 0.5f - distance);
                    tex.SetPixel(x, y, new Color(1, 1, 1, alpha));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100);
        }

        public static Sprite CreateThumbnailPlaceholder(bool unsupported)
        {
            const int width = 256;
            const int height = 144;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var top = unsupported ? new Color(0.22f, 0.18f, 0.12f) : new Color(0.08f, 0.12f, 0.15f);
            var bottom = unsupported ? new Color(0.14f, 0.12f, 0.1f) : new Color(0.12f, 0.16f, 0.19f);
            for (var y = 0; y < height; y++)
            {
                var t = y / (height - 1f);
                var c = Color.Lerp(bottom, top, t);
                for (var x = 0; x < width; x++)
                {
                    var stripe = ((x + y) % 32) < 2 ? 0.035f : 0f;
                    tex.SetPixel(x, y, c + new Color(stripe, stripe, stripe, 0));
                }
            }

            var play = unsupported ? new Color(0.96f, 0.72f, 0.25f, 1) : new Color(0.21f, 0.76f, 1f, 1);
            DrawTriangle(tex, new Vector2(108, 48), new Vector2(108, 96), new Vector2(154, 72), play);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100);
        }

        public static Sprite CreateLogo()
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    tex.SetPixel(x, y, Color.clear);
                }
            }

            var center = new Vector2(64, 64);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Vector2.Distance(new Vector2(x, y), center);
                    if (distance <= 52)
                    {
                        var t = Mathf.Clamp01(distance / 52f);
                        tex.SetPixel(x, y, Color.Lerp(new Color(0.21f, 0.76f, 1f, 1), new Color(0.26f, 0.82f, 0.48f, 1), t));
                    }
                }
            }

            DrawTriangle(tex, new Vector2(54, 42), new Vector2(54, 86), new Vector2(88, 64), Color.white);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100);
        }

        private static float RoundedAlpha(int x, int y, int width, int height, int radius)
        {
            var px = x < radius ? radius - x : x >= width - radius ? x - (width - radius - 1) : 0;
            var py = y < radius ? radius - y : y >= height - radius ? y - (height - radius - 1) : 0;
            if (px == 0 || py == 0)
            {
                return 1;
            }

            var distance = Mathf.Sqrt(px * px + py * py);
            return Mathf.Clamp01(radius + 0.5f - distance);
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
    }
}
