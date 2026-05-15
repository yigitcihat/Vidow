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
        private const int ResolveTimeoutSeconds = 20;
        private const int RequestTimeoutSeconds = 15;
        private const int MaxConcurrentDownloads = 2;

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
            AddImage(inputFrame.gameObject, Surface);
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

            var scrollRect = scrollRoot.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24;

            var viewport = CreateRect("Viewport", scrollRoot);
            Stretch(viewport);
            var viewportImage = AddImage(viewport.gameObject, Color.clear);
            var mask = viewport.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;
            scrollRect.viewport = viewport;

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
            scrollRect.content = _resultContent;

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

            using (var request = UnityWebRequest.Get(uri.AbsoluteUri))
            {
                request.timeout = RequestTimeoutSeconds;
                request.SetRequestHeader("User-Agent", "Vidow/1.0 Unity");
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.ConnectionError)
                {
                    complete(ResolveResult.Fail(ResolveStatus.NetworkError, "Could not reach this link. Check your connection and try again."));
                    yield break;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    complete(ResolveResult.Fail(ResolveStatus.UnsupportedSource, "This source is not supported for download."));
                    yield break;
                }

                var html = request.downloadHandler.text;
                var videos = HtmlVideoParser.Extract(uri, html).ToList();
                if (videos.Count == 0)
                {
                    complete(ResolveResult.Fail(ResolveStatus.NoVideosFound, "No downloadable videos were found."));
                    yield break;
                }

                complete(new ResolveResult
                {
                    Status = ResolveStatus.Success,
                    Videos = videos,
                    UserMessage = string.Empty
                });
            }
        }

        private void AddResult(VideoItem item)
        {
            _emptyState.SetActive(false);
            _videos.Add(item);

            var view = ResultItemView.Create(_resultContent, this, item);
            _resultViews.Add(view);
            UpdateResultsCount();
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
            var tempPath = finalPath + ".part";
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

            using (var request = UnityWebRequest.Get(job.Video.MediaUrl))
            {
                request.timeout = 0;
                request.downloadHandler = new DownloadHandlerFile(job.TempFilePath);
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

            try
            {
                if (File.Exists(job.FinalFilePath))
                {
                    File.Delete(job.FinalFilePath);
                }

                File.Move(job.TempFilePath, job.FinalFilePath);
                job.Status = DownloadStatus.Completed;
                job.Progress.Percent = 1f;
                job.View?.SetCompleted(job.FinalFilePath);
                ShowToast("Download complete.");
                FinishJob(job);
            }
            catch (Exception ex)
            {
                FailJob(job, "Vidow cannot write to this folder. Choose another location.", ex.Message);
                yield break;
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
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null)
            {
                return;
            }

            var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            UnityEngine.Object.DontDestroyOnLoad(eventSystem);
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
            AddImage(root.gameObject, SurfaceHover);

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
                _status.text = Item.IsDownloadable ? "Ready" : "Unsupported";
                _status.color = Item.IsDownloadable ? Success : Warning;
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
        public bool IsDownloadable = true;
        public string UnsupportedReason;

        public string SafeFileName => SafePath.SanitizeFileName($"{Title}.{(string.IsNullOrWhiteSpace(Extension) ? "mp4" : Extension.TrimStart('.'))}");

        public string Metadata
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(SourceDomain)) parts.Add(SourceDomain);
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

            return $"{ByteFormatter.Format(BytesDownloaded)} downloaded - {speed}";
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
