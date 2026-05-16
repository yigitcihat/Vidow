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
using UnityEngine;
using UnityEngine.Networking;

namespace Vidow
{
    public sealed class ExternalToolResult
    {
        public bool Available;
        public string ExecutablePath;
        public string Version;
        public string Message;
    }

    public sealed class ExternalProcessRun
    {
        private readonly object _gate = new object();
        private readonly StringBuilder _stdout = new StringBuilder();
        private readonly StringBuilder _stderr = new StringBuilder();
        private Process _process;

        public bool IsDone { get; private set; }
        public int ExitCode { get; private set; } = -1;
        public float? Percent { get; private set; }
        public string LastLine { get; private set; }

        public string StandardOutput
        {
            get
            {
                lock (_gate)
                {
                    return _stdout.ToString();
                }
            }
        }

        public string StandardError
        {
            get
            {
                lock (_gate)
                {
                    return _stderr.ToString();
                }
            }
        }

        public string ErrorTail
        {
            get
            {
                var value = StandardError;
                if (string.IsNullOrWhiteSpace(value))
                {
                    value = StandardOutput;
                }

                value = Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();
                return value.Length > 240 ? value.Substring(value.Length - 240) : value;
            }
        }

        public static ExternalProcessRun Start(string fileName, IEnumerable<string> arguments, string workingDirectory = null, IDictionary<string, string> environment = null)
        {
            var run = new ExternalProcessRun();
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                info.WorkingDirectory = workingDirectory;
            }

            if (environment != null)
            {
                foreach (var pair in environment)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                    {
                        info.EnvironmentVariables[pair.Key] = pair.Value;
                    }
                }
            }

            run._process = new Process
            {
                StartInfo = info,
                EnableRaisingEvents = true
            };

            run._process.OutputDataReceived += (_, e) => run.CaptureLine(e.Data, false);
            run._process.ErrorDataReceived += (_, e) => run.CaptureLine(e.Data, true);
            run._process.Exited += (_, __) =>
            {
                try
                {
                    run._process.WaitForExit();
                    run.ExitCode = run._process.ExitCode;
                }
                catch
                {
                    run.ExitCode = -1;
                }

                run.IsDone = true;
            };

            run._process.Start();
            run._process.BeginOutputReadLine();
            run._process.BeginErrorReadLine();
            return run;
        }

        public void Cancel()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch
            {
                // Best-effort cancellation.
            }
        }

        private void CaptureLine(string line, bool error)
        {
            if (line == null)
            {
                return;
            }

            lock (_gate)
            {
                if (error)
                {
                    _stderr.AppendLine(line);
                }
                else
                {
                    _stdout.AppendLine(line);
                }

                LastLine = line;
                var match = Regex.Match(line, @"(?<percent>\d+(?:\.\d+)?)%");
                if (match.Success &&
                    float.TryParse(match.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    Percent = Mathf.Clamp01(percent / 100f);
                }
            }
        }

        public static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }

    public static class YtDlpBridge
    {
        private const string WindowsDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        private const string DefaultFormatSelector = "best[ext=mp4][vcodec!=none][acodec!=none]/best[vcodec!=none][acodec!=none]/best";
        private static readonly Regex JsonWarningPrefix = new Regex("^WARNING:.*$", RegexOptions.Multiline | RegexOptions.Compiled);

        public static IEnumerator EnsureAvailable(Action<ExternalToolResult> complete)
        {
            if (TryProbe(GetLocalExecutablePath(), out var localVersion))
            {
                complete(new ExternalToolResult
                {
                    Available = true,
                    ExecutablePath = GetLocalExecutablePath(),
                    Version = localVersion,
                    Message = "yt-dlp ready"
                });
                yield break;
            }

            if (TryProbe(GetPathExecutableName(), out var pathVersion))
            {
                complete(new ExternalToolResult
                {
                    Available = true,
                    ExecutablePath = GetPathExecutableName(),
                    Version = pathVersion,
                    Message = "yt-dlp ready"
                });
                yield break;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                complete(new ExternalToolResult
                {
                    Available = false,
                    Message = "Install yt-dlp and make it available on PATH for extended source support."
                });
                yield break;
            }

            var toolPath = GetLocalExecutablePath();
            var tempPath = toolPath + ".download";
            Directory.CreateDirectory(Path.GetDirectoryName(toolPath));

            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // The download handler will report the real write error below.
            }

            using (var request = UnityWebRequest.Get(WindowsDownloadUrl))
            {
                request.timeout = 60;
                request.downloadHandler = new DownloadHandlerFile(tempPath);
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    complete(new ExternalToolResult
                    {
                        Available = false,
                        Message = "Could not download yt-dlp: " + request.error
                    });
                    yield break;
                }
            }

            try
            {
                if (File.Exists(toolPath))
                {
                    File.Delete(toolPath);
                }

                File.Move(tempPath, toolPath);
            }
            catch (Exception ex)
            {
                complete(new ExternalToolResult
                {
                    Available = false,
                    Message = "Could not install yt-dlp: " + ex.Message
                });
                yield break;
            }

            complete(new ExternalToolResult
            {
                Available = TryProbe(toolPath, out var downloadedVersion),
                ExecutablePath = toolPath,
                Version = downloadedVersion,
                Message = string.IsNullOrWhiteSpace(downloadedVersion) ? "yt-dlp was installed but did not start." : "yt-dlp installed"
            });
        }

        public static ExternalProcessRun StartMetadata(string executablePath, string url)
        {
            var toolDirectory = GetToolDirectory(executablePath);
            return ExternalProcessRun.Start(executablePath, new[]
            {
                "--dump-single-json",
                "--no-warnings",
                "--skip-download",
                "--no-playlist",
                url
            }, toolDirectory, CreateToolEnvironment(executablePath));
        }

        public static ExternalProcessRun StartDownload(string executablePath, VideoItem video, string outputPath)
        {
            var format = string.IsNullOrWhiteSpace(video.ExternalFormatSelector) ? DefaultFormatSelector : video.ExternalFormatSelector;
            var toolDirectory = GetToolDirectory(executablePath);
            return ExternalProcessRun.Start(executablePath, new[]
            {
                "--newline",
                "--no-warnings",
                "--no-playlist",
                "--no-mtime",
                "--force-overwrites",
                "-f",
                format,
                "-o",
                outputPath,
                string.IsNullOrWhiteSpace(video.ExternalSourceUrl) ? video.PageUrl : video.ExternalSourceUrl
            }, toolDirectory, CreateToolEnvironment(executablePath));
        }

        public static List<VideoItem> ParseVideoItems(string json, string originalUrl)
        {
            json = JsonWarningPrefix.Replace(json ?? string.Empty, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<VideoItem>();
            }

            var parsed = SimpleJson.Parse(json);
            var root = parsed as Dictionary<string, object>;
            if (root == null)
            {
                return new List<VideoItem>();
            }

            if (TryGetList(root, "entries", out var entries) && entries.Count > 0 && !TryGetList(root, "formats", out _))
            {
                return ParsePlaylistEntries(entries, originalUrl);
            }

            return ParseSingleVideo(root, originalUrl);
        }

        private static List<VideoItem> ParseSingleVideo(Dictionary<string, object> root, string originalUrl)
        {
            var title = CleanTitle(GetString(root, "title")) ?? "video";
            var pageUrl = GetString(root, "webpage_url") ?? originalUrl;
            var extractor = GetString(root, "extractor_key") ?? GetString(root, "extractor") ?? HostFromUrl(pageUrl);
            var thumbnail = GetString(root, "thumbnail") ?? BestThumbnail(root);
            var duration = GetDouble(root, "duration");
            var durationValue = duration.HasValue && duration.Value > 0 ? TimeSpan.FromSeconds(duration.Value) : (TimeSpan?)null;

            if (!TryGetList(root, "formats", out var formats))
            {
                return new List<VideoItem>
                {
                    CreateBestFallback(title, pageUrl, extractor, thumbnail, durationValue)
                };
            }

            var candidates = new List<VideoItem>();
            foreach (var entry in formats)
            {
                var format = entry as Dictionary<string, object>;
                if (format == null)
                {
                    continue;
                }

                var formatId = GetString(format, "format_id");
                if (string.IsNullOrWhiteSpace(formatId))
                {
                    continue;
                }

                var vcodec = GetString(format, "vcodec");
                var acodec = GetString(format, "acodec");
                if (string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var extension = GetString(format, "ext") ?? "mp4";
                if (extension.Equals("mhtml", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals("storyboard", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var height = GetInt(format, "height");
                var quality = BuildQualityLabel(format, height);
                var fileSize = GetLong(format, "filesize") ?? GetLong(format, "filesize_approx");
                var item = new VideoItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    PageUrl = pageUrl,
                    MediaUrl = "external://" + formatId,
                    Title = AppendQuality(title, quality),
                    SourceDomain = extractor,
                    ThumbnailUrl = thumbnail,
                    Extension = extension,
                    MimeType = GetString(format, "mime_type"),
                    QualityLabel = quality,
                    SizeBytes = fileSize,
                    Duration = durationValue,
                    IsExternal = true,
                    ExternalSourceUrl = pageUrl,
                    ExternalFormatSelector = formatId,
                    ExternalToolName = "yt-dlp"
                };

                candidates.Add(item);
            }

            var selected = candidates
                .GroupBy(item => QualityKey(item), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.Extension.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(item => item.SizeBytes ?? 0)
                    .First())
                .OrderByDescending(item => HlsPlaylistParser.QualityRank(item.QualityLabel))
                .ThenByDescending(item => item.Extension.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .ToList();

            if (selected.Count == 0)
            {
                selected.Add(CreateBestFallback(title, pageUrl, extractor, thumbnail, durationValue));
            }

            return selected;
        }

        private static List<VideoItem> ParsePlaylistEntries(List<object> entries, string originalUrl)
        {
            var items = new List<VideoItem>();
            foreach (var entry in entries.Take(50))
            {
                var itemObject = entry as Dictionary<string, object>;
                if (itemObject == null)
                {
                    continue;
                }

                var url = GetString(itemObject, "webpage_url") ?? GetString(itemObject, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    url = new Uri(new Uri(originalUrl), url).AbsoluteUri;
                }

                items.Add(CreateBestFallback(
                    CleanTitle(GetString(itemObject, "title")) ?? HostFromUrl(url),
                    url,
                    GetString(itemObject, "extractor_key") ?? HostFromUrl(url),
                    GetString(itemObject, "thumbnail"),
                    null));
            }

            return items;
        }

        private static VideoItem CreateBestFallback(string title, string pageUrl, string extractor, string thumbnail, TimeSpan? duration)
        {
            return new VideoItem
            {
                Id = Guid.NewGuid().ToString("N"),
                PageUrl = pageUrl,
                MediaUrl = "external://best",
                Title = AppendQuality(title, "Best"),
                SourceDomain = extractor,
                ThumbnailUrl = thumbnail,
                Extension = "mp4",
                QualityLabel = "Best",
                Duration = duration,
                IsExternal = true,
                ExternalSourceUrl = pageUrl,
                ExternalFormatSelector = DefaultFormatSelector,
                ExternalToolName = "yt-dlp"
            };
        }

        private static string BuildQualityLabel(Dictionary<string, object> format, int? height)
        {
            var note = GetString(format, "format_note");
            if (!string.IsNullOrWhiteSpace(note) && !note.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                return note;
            }

            var resolution = GetString(format, "resolution");
            if (!string.IsNullOrWhiteSpace(resolution) && !resolution.Equals("audio only", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(resolution, @"x(?<height>\d+)$");
                if (match.Success)
                {
                    return match.Groups["height"].Value + "p";
                }

                return resolution;
            }

            return height.HasValue && height.Value > 0 ? height.Value + "p" : "Source";
        }

        private static string QualityKey(VideoItem item)
        {
            var rank = HlsPlaylistParser.QualityRank(item.QualityLabel);
            return rank > 0 ? rank + "-" + item.Extension : item.QualityLabel + "-" + item.Extension;
        }

        private static string AppendQuality(string title, string quality)
        {
            if (string.IsNullOrWhiteSpace(quality) || quality == "Source")
            {
                return title;
            }

            return $"{title} ({quality})";
        }

        private static string CleanTitle(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value, "\\s+", " ").Trim();
        }

        private static string BestThumbnail(Dictionary<string, object> root)
        {
            if (!TryGetList(root, "thumbnails", out var thumbnails))
            {
                return null;
            }

            return thumbnails
                .OfType<Dictionary<string, object>>()
                .Select(item => new { Url = GetString(item, "url"), Preference = GetInt(item, "preference") ?? 0, Width = GetInt(item, "width") ?? 0 })
                .Where(item => !string.IsNullOrWhiteSpace(item.Url))
                .OrderByDescending(item => item.Preference)
                .ThenByDescending(item => item.Width)
                .Select(item => item.Url)
                .FirstOrDefault();
        }

        private static bool TryProbe(string executablePath, out string version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            try
            {
                if (Path.IsPathRooted(executablePath) && !File.Exists(executablePath))
                {
                    return false;
                }

                var info = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                foreach (var pair in CreateToolEnvironment(executablePath))
                {
                    info.EnvironmentVariables[pair.Key] = pair.Value;
                }

                var toolDirectory = GetToolDirectory(executablePath);
                if (!string.IsNullOrWhiteSpace(toolDirectory))
                {
                    info.WorkingDirectory = toolDirectory;
                }

                using (var process = Process.Start(info))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    if (!process.WaitForExit(4000))
                    {
                        TryKill(process);
                        return false;
                    }

                    version = process.StandardOutput.ReadToEnd().Trim();
                    return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(version);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetLocalExecutablePath()
        {
            var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
            return Path.Combine(Application.persistentDataPath, "VidowTools", name);
        }

        private static string GetToolDirectory(string executablePath)
        {
            if (!string.IsNullOrWhiteSpace(executablePath) && Path.IsPathRooted(executablePath))
            {
                return Path.GetDirectoryName(executablePath);
            }

            return Path.Combine(Application.persistentDataPath, "VidowTools");
        }

        private static Dictionary<string, string> CreateToolEnvironment(string executablePath)
        {
            var tempDirectory = Path.Combine(GetToolDirectory(executablePath), "Temp");
            Directory.CreateDirectory(tempDirectory);
            return new Dictionary<string, string>
            {
                { "TEMP", tempDirectory },
                { "TMP", tempDirectory },
                { "TMPDIR", tempDirectory },
                { "PYTHONUTF8", "1" }
            };
        }

        private static string GetPathExecutableName()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
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

        private static string HostFromUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "external";
        }

        private static bool TryGetList(Dictionary<string, object> item, string key, out List<object> list)
        {
            list = null;
            if (item.TryGetValue(key, out var value))
            {
                list = value as List<object>;
            }

            return list != null;
        }

        private static string GetString(Dictionary<string, object> item, string key)
        {
            return item.TryGetValue(key, out var value) ? value as string : null;
        }

        private static double? GetDouble(Dictionary<string, object> item, string key)
        {
            if (!item.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is double number)
            {
                return number;
            }

            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : (double?)null;
        }

        private static int? GetInt(Dictionary<string, object> item, string key)
        {
            var value = GetDouble(item, key);
            return value.HasValue ? Mathf.RoundToInt((float)value.Value) : (int?)null;
        }

        private static long? GetLong(Dictionary<string, object> item, string key)
        {
            var value = GetDouble(item, key);
            return value.HasValue && value.Value > 0 ? (long)value.Value : (long?)null;
        }
    }

    public sealed class SimpleJson
    {
        private readonly string _json;
        private int _index;

        private SimpleJson(string json)
        {
            _json = json ?? string.Empty;
        }

        public static object Parse(string json)
        {
            var parser = new SimpleJson(json);
            return parser.ParseValue();
        }

        private object ParseValue()
        {
            SkipWhitespace();
            if (_index >= _json.Length)
            {
                return null;
            }

            var c = _json[_index];
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') return ParseString();
            if (c == 't') return ReadLiteral("true", true);
            if (c == 'f') return ReadLiteral("false", false);
            if (c == 'n') return ReadLiteral("null", null);
            return ParseNumber();
        }

        private Dictionary<string, object> ParseObject()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            _index++;
            SkipWhitespace();
            if (TryConsume('}'))
            {
                return result;
            }

            while (_index < _json.Length)
            {
                SkipWhitespace();
                var key = ParseString();
                SkipWhitespace();
                TryConsume(':');
                result[key] = ParseValue();
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    break;
                }

                TryConsume(',');
            }

            return result;
        }

        private List<object> ParseArray()
        {
            var result = new List<object>();
            _index++;
            SkipWhitespace();
            if (TryConsume(']'))
            {
                return result;
            }

            while (_index < _json.Length)
            {
                result.Add(ParseValue());
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    break;
                }

                TryConsume(',');
            }

            return result;
        }

        private string ParseString()
        {
            var builder = new StringBuilder();
            if (!TryConsume('"'))
            {
                return string.Empty;
            }

            while (_index < _json.Length)
            {
                var c = _json[_index++];
                if (c == '"')
                {
                    break;
                }

                if (c != '\\' || _index >= _json.Length)
                {
                    builder.Append(c);
                    continue;
                }

                var escape = _json[_index++];
                switch (escape)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(escape);
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
                        if (_index + 4 <= _json.Length &&
                            int.TryParse(_json.Substring(_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            builder.Append((char)code);
                            _index += 4;
                        }
                        break;
                    default:
                        builder.Append(escape);
                        break;
                }
            }

            return builder.ToString();
        }

        private object ParseNumber()
        {
            var start = _index;
            while (_index < _json.Length && "-+0123456789.eE".IndexOf(_json[_index]) >= 0)
            {
                _index++;
            }

            var raw = _json.Substring(start, _index - start);
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0d;
        }

        private object ReadLiteral(string literal, object value)
        {
            if (_index + literal.Length <= _json.Length &&
                string.Equals(_json.Substring(_index, literal.Length), literal, StringComparison.Ordinal))
            {
                _index += literal.Length;
            }

            return value;
        }

        private bool TryConsume(char value)
        {
            if (_index < _json.Length && _json[_index] == value)
            {
                _index++;
                return true;
            }

            return false;
        }

        private void SkipWhitespace()
        {
            while (_index < _json.Length && char.IsWhiteSpace(_json[_index]))
            {
                _index++;
            }
        }
    }
}
