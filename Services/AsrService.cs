using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MyMediaPlayer.Models;
using Whisper.net;

namespace MyMediaPlayer.Services;

/// <summary>
/// 语音识别服务 (Whisper.net)：
/// 1. 负责检测本地 Assets/ggml-base.bin 是否存在，并在用户同意后按需下载（带进度回报）
/// 2. 接收 MediaService 切出的 16kHz 单声道 float[] 音频切片，在后台推理并返回带时间戳的 SubtitleModel
/// </summary>
public class AsrService : IDisposable
{
    private const string DefaultModelFileName = "ggml-base.bin";
    private static readonly string[] PreferredModelFileNames =
    {
        "ggml-large-v3-turbo-q5_0.bin",
        "ggml-large-v3-turbo.bin",
        "ggml-medium.bin",
        "ggml-small.bin",
        "ggml-base.bin"
    };
    private const long MinValidModelBytes = 50L * 1024 * 1024;

    // 优先使用国内高速镜像，失败时自动回退到 HuggingFace 官方源
    private static readonly string[] ModelDownloadUrls =
    {
        "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin"
    };

    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _processor;
    private string _currentLanguage = "auto";
    private string? _autoLockedLanguage;
    private string _loadedModelPath = string.Empty;
    private readonly SemaphoreSlim _inferLock = new(1, 1);

    public bool IsModelReady => _processor != null;
    public string ModelFilePath { get; private set; }
    public string EffectiveLanguage => _autoLockedLanguage ?? _currentLanguage;

    public AsrService()
    {
        ModelFilePath = ResolveModelPath();
    }

    public void ResetAutoLockedLanguage()
    {
        _autoLockedLanguage = null;
    }

    public void SetModelRootDirectory(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return;
        }

        Directory.CreateDirectory(rootDirectory);
        string newModelPath = ResolveBestModelInDirectory(rootDirectory);
        if (!string.Equals(ModelFilePath, newModelPath, StringComparison.OrdinalIgnoreCase))
        {
            ModelFilePath = newModelPath;
        }
    }

    private static string ResolveBestModelInDirectory(string directory)
    {
        foreach (string candidate in PreferredModelFileNames)
        {
            string fullPath = Path.Combine(directory, candidate);
            if (File.Exists(fullPath) && new FileInfo(fullPath).Length >= MinValidModelBytes)
            {
                return fullPath;
            }
        }

        return Path.Combine(directory, DefaultModelFileName);
    }

    private static string ResolveModelPath()
    {
        // 1. 优先检查开发目录 d:\vedioPlayer\Assets
        string devAssetsDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets"));
        if (Directory.Exists(devAssetsDir))
        {
            return ResolveBestModelInDirectory(devAssetsDir);
        }

        // 2. 检查程序运行目录 Assets
        string localAssetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
        Directory.CreateDirectory(localAssetsDir);
        return ResolveBestModelInDirectory(localAssetsDir);
    }

    /// <summary>
    /// 检测本地 Assets/ggml-base.bin 模型文件是否已存在且完整
    /// </summary>
    public bool IsModelFilePresent()
    {
        return File.Exists(ModelFilePath) && new FileInfo(ModelFilePath).Length >= MinValidModelBytes;
    }

    /// <summary>
    /// 确保本地存在 ggml-base.bin 模型并完成 WhisperProcessor 初始化
    /// </summary>
    /// <param name="allowDownload">若本地不存在模型，是否允许从云端下载（由用户在弹窗中选择决定）</param>
    public async Task<bool> EnsureModelReadyAsync(
        bool allowDownload,
        string language = "auto",
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        await _inferLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processor != null &&
                _currentLanguage == language &&
                string.Equals(_loadedModelPath, ModelFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!IsModelFilePresent())
            {
                if (!allowDownload)
                {
                    statusProgress?.Report("普通播放模式 (未启用 AI 字幕)");
                    return false;
                }

                bool downloaded = await DownloadModelAsync(ModelFilePath, statusProgress, cancellationToken).ConfigureAwait(false);
                if (!downloaded)
                {
                    return false;
                }
            }

            string modelFileName = Path.GetFileName(ModelFilePath);
            statusProgress?.Report($"正在加载 Whisper 语音识别模型 ({modelFileName})...");

            if (_processor != null)
            {
                await _processor.DisposeAsync().ConfigureAwait(false);
                _processor = null;
            }

            if (_whisperFactory == null || !string.Equals(_loadedModelPath, ModelFilePath, StringComparison.OrdinalIgnoreCase))
            {
                _whisperFactory?.Dispose();
                _whisperFactory = WhisperFactory.FromPath(ModelFilePath);
                _loadedModelPath = ModelFilePath;
            }

            _currentLanguage = language;
            if (!language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                _autoLockedLanguage = null;
            }

            string targetLang = _autoLockedLanguage ?? language;
            _processor = BuildProcessorCore(_whisperFactory, targetLang);

            statusProgress?.Report($"Whisper 语音识别引擎就绪 ({modelFileName})");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AsrService] 模型初始化失败: {ex}");
            Console.WriteLine($"[AsrService] 模型初始化失败: {ex.Message}");
            statusProgress?.Report($"ASR 模型加载失败: {ex.Message}");
            return false;
        }
        finally
        {
            _inferLock.Release();
        }
    }

    private static WhisperProcessor BuildProcessorCore(WhisperFactory factory, string language)
    {
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 4, 8);
        bool isAuto = string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase);

        var builder = factory.CreateBuilder()
            .WithNoContext()
            .WithTemperature(0.0f)          // 锁定 0.0f 确定性解码，防止默认 0.2f 随机采样产生空耳
            .WithTemperatureInc(0.0f)       // 禁用 6 轮高温回退重试（彻底消除单切片卡顿 14~18 秒的恶性延迟）
            .WithMaxTokensPerSegment(36)    // 限制单切片最大 Token 数，防止环境音触发「ああああ...」448 Token 死循环
            .WithThreads(threads)
            .WithSuppressRegex(@"[\(\)\[\]（）【】♪\*]|音楽|拍手") // 禁止输出 (音楽)/(笑) 等音效标签而吞掉同片段内的真实人声台词
            .WithProbabilities();

        if (isAuto)
        {
            // Auto 语种检测阶段保持完整编码器上下文以确保语种识别准确；一旦锁定具体语种后立即切换为 384 上下文高速推理
            builder.WithLanguage("auto").WithLanguageDetection();
        }
        else
        {
            builder.WithLanguage(language).WithAudioContextSize(384);
        }

        return builder.Build();
    }

    private static async Task<bool> DownloadModelAsync(
        string targetPath,
        IProgress<string>? statusProgress,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = targetPath + ".downloading";

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("vvPlayer/1.0");

        foreach (string url in ModelDownloadUrls)
        {
            try
            {
                statusProgress?.Report("正在连接模型服务器下载 ggml-base.bin...");
                using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? 147_951_465L;
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[65536];
                    long totalRead = 0;
                    int bytesRead;
                    var lastReport = DateTime.UtcNow;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        totalRead += bytesRead;

                        if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 200)
                        {
                            double mbRead = totalRead / (1024.0 * 1024.0);
                            double mbTotal = totalBytes / (1024.0 * 1024.0);
                            int percent = (int)Math.Clamp(totalRead * 100.0 / totalBytes, 0, 100);
                            statusProgress?.Report($"正在下载语音模型 ggml-base.bin: {mbRead:F1} / {mbTotal:F1} MB ({percent}%)");
                            lastReport = DateTime.UtcNow;
                        }
                    }
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                File.Move(tempPath, targetPath);
                statusProgress?.Report("ggml-base.bin 下载完成！");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AsrService] 从 {url} 下载失败: {ex.Message}，尝试备用源...");
            }
        }

        statusProgress?.Report("模型下载失败，已切换为普通视频播放模式");
        return false;
    }

    /// <summary>
    /// 对单个 16kHz 单声道音频切片执行 Whisper 推理，输出带视频绝对时间戳的识别结果
    /// </summary>
    public async Task<IReadOnlyList<SubtitleModel>> TranscribeChunkAsync(
        AudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        if (_processor == null || chunk.Samples16k.Length == 0)
        {
            return Array.Empty<SubtitleModel>();
        }

        // 预处理音频：过滤纯底噪切片 + 人声音量峰值归一化 (Peak Normalization) + 首尾静音缓冲垫 + 边缘淡入淡出
        if (!TryPrepareSamplesForWhisper(chunk.Samples16k, out float[] samples, out TimeSpan leadingPadOffset))
        {
            return Array.Empty<SubtitleModel>();
        }

        var results = new List<SubtitleModel>();

        await _inferLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rawSegments = new List<(TimeSpan RelStart, TimeSpan RelEnd, string Text, string RawLang)>();

            // 传入 CancellationToken.None 确保单次 C++ 底层推理完整结束后再释放锁，彻底避免 WhisperProcessor 处于 processing 状态时被 Dispose 报错
            await foreach (var segment in _processor.ProcessAsync(samples, CancellationToken.None).ConfigureAwait(false))
            {
                // 过滤高静音概率或极低置信度的底噪幻觉片段
                if (segment.NoSpeechProbability > 0.72f && segment.Probability < 0.45f)
                {
                    continue;
                }
                if (segment.Probability > 0f && segment.Probability < 0.20f)
                {
                    continue;
                }

                string text = CleanRecognizedText(segment.Text);
                if (IsHallucinationOrNoise(text))
                {
                    continue;
                }

                string rawLang = string.IsNullOrWhiteSpace(segment.Language) ? _currentLanguage : segment.Language.Trim();
                TimeSpan relStart = segment.Start > leadingPadOffset ? segment.Start - leadingPadOffset : TimeSpan.Zero;
                TimeSpan relEnd = segment.End > leadingPadOffset ? segment.End - leadingPadOffset : relStart + TimeSpan.FromSeconds(0.8);
                rawSegments.Add((relStart, relEnd, text, rawLang));
            }

            for (int i = 0; i < rawSegments.Count; i++)
            {
                var seg = rawSegments[i];
                TimeSpan absStart;
                TimeSpan absEnd;

                if (rawSegments.Count == 1)
                {
                    // 单句切片直接对齐 VAD 检测到的人声真实起止边界，消除 Whisper 内部 30s 窗口时间戳漂移
                    absStart = chunk.StartTime;
                    absEnd = chunk.EndTime + TimeSpan.FromSeconds(0.2);
                }
                else
                {
                    absStart = chunk.StartTime + seg.RelStart;
                    absEnd = chunk.StartTime + seg.RelEnd;

                    if (absStart < chunk.StartTime)
                    {
                        absStart = chunk.StartTime;
                    }
                    if (absEnd > chunk.EndTime + TimeSpan.FromSeconds(0.25))
                    {
                        absEnd = chunk.EndTime + TimeSpan.FromSeconds(0.25);
                    }
                    if (i + 1 < rawSegments.Count)
                    {
                        TimeSpan nextStart = chunk.StartTime + rawSegments[i + 1].RelStart;
                        if (absEnd > nextStart)
                        {
                            absEnd = nextStart;
                        }
                    }
                }

                if (absEnd <= absStart)
                {
                    absEnd = absStart + TimeSpan.FromSeconds(1.0);
                }

                string lang = _currentLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? (_autoLockedLanguage ?? DetectLanguageFromScript(seg.Text, seg.RawLang))
                    : _currentLanguage;

                // 若处于 Auto 模式且当前识别出了包含明确语种特征（如 >=3 个日文假名或韩文谚文）的完整句子，自动锁定全片语种，避免后续短感叹词被误判为俄语/韩语
                if (_currentLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                    _autoLockedLanguage == null &&
                    _whisperFactory != null &&
                    TryGetHighConfidenceScriptLanguage(seg.Text, out string confidentLang))
                {
                    _autoLockedLanguage = confidentLang;
                    lang = confidentLang;
                    if (_processor != null)
                    {
                        await _processor.DisposeAsync().ConfigureAwait(false);
                    }
                    _processor = BuildProcessorCore(_whisperFactory, confidentLang);
                    Debug.WriteLine($"[Whisper ASR] Auto 模式已自动锁定全片主语种: {confidentLang}");
                    Console.WriteLine($"[Whisper ASR] Auto 模式已自动锁定全片主语种: {confidentLang}");
                }

                var subtitle = new SubtitleModel
                {
                    StartTime = absStart,
                    EndTime = absEnd,
                    OriginalText = seg.Text,
                    DetectedLanguage = lang
                };

                results.Add(subtitle);

                // 阶段二要求：在控制台与调试输出窗口实时打印识别出的外语原文与时间戳
                string logLine = $"[Whisper ASR] [{absStart:hh\\:mm\\:ss\\.ff} -> {absEnd:hh\\:mm\\:ss\\.ff}] ({lang}): {seg.Text}";
                Console.WriteLine(logLine);
                Debug.WriteLine(logLine);
            }
        }
        finally
        {
            _inferLock.Release();
        }

        return results;
    }

    /// <summary>
    /// Whisper 音频预处理：
    /// 1. 过滤纯静音切片
    /// 2. 对人声进行峰值归一化 (Peak Normalization 至 0.85)，解决视频人声偏小导致 Whisper 特征信噪比低的问题
    /// 3. 不在头部硬塞全零静音（防止强背景音乐视频在 0.2s 处产生 0 -> 高能量的阶跃突变被 Whisper 误判为音乐起音），仅在不足 1.25s 时补齐尾部
    /// </summary>
    private static bool TryPrepareSamplesForWhisper(
        float[] rawSamples,
        out float[] preparedSamples,
        out TimeSpan leadingPadOffset)
    {
        const int minTotalSamples = 20000; // 1.25s 最小总长度
        leadingPadOffset = TimeSpan.Zero;

        float peak = 0f;
        double sumSq = 0.0;
        for (int i = 0; i < rawSamples.Length; i++)
        {
            float abs = MathF.Abs(rawSamples[i]);
            if (abs > peak)
            {
                peak = abs;
            }
            sumSq += rawSamples[i] * rawSamples[i];
        }

        float rms = MathF.Sqrt((float)(sumSq / Math.Max(1, rawSamples.Length)));
        if (peak < 0.008f || rms < 0.002f)
        {
            preparedSamples = Array.Empty<float>();
            return false;
        }

        float gain = (peak > 0.012f && peak < 0.80f) ? Math.Min(0.85f / peak, 6.0f) : 1.0f;

        int totalLength = Math.Max(minTotalSamples, rawSamples.Length);
        preparedSamples = new float[totalLength];

        for (int i = 0; i < rawSamples.Length; i++)
        {
            preparedSamples[i] = Math.Clamp(rawSamples[i] * gain, -0.98f, 0.98f);
        }

        return true;
    }

    /// <summary>
    /// 清理 Whisper 常见的句首/句尾括号台本标记，例如 "(徳島新聞社) 行っちゃっておかしいですか" -> "行っちゃっておかしいですか"
    /// </summary>
    private static string CleanRecognizedText(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        string text = rawText.Trim();

        // 循环剥离句首或句尾成对括号包裹的台本/音效注释：(...)、（...）、[...]、【...】
        bool changed = true;
        while (changed && text.Length > 0)
        {
            changed = false;
            text = text.Trim();

            if (TryStripLeadingBracket(text, '(', ')', out string stripped) ||
                TryStripLeadingBracket(text, '（', '）', out stripped) ||
                TryStripLeadingBracket(text, '[', ']', out stripped) ||
                TryStripLeadingBracket(text, '【', '】', out stripped))
            {
                text = stripped;
                changed = true;
            }
        }

        // 仅当末尾不是右括号时，才剥离偶发的未闭合前导左括号（如 "(お腹にどこ行って?"），保留完整的 "(音楽)" / "【音楽】" 交由 IsHallucinationOrNoise 过滤
        if (!text.EndsWith(')') && !text.EndsWith('）') && !text.EndsWith(']') && !text.EndsWith('】'))
        {
            text = text.TrimStart('(', '（', '[', '【').Trim();
        }

        return text;
    }

    private static bool TryStripLeadingBracket(string input, char open, char close, out string result)
    {
        if (input.Length >= 2 && input[0] == open)
        {
            int closeIdx = input.IndexOf(close);
            // 仅当右括号之后还有实际台词正文时才剥离括号前缀；若整句都在括号内则交由 IsHallucinationOrNoise 过滤
            if (closeIdx > 0 && closeIdx < input.Length - 1)
            {
                result = input.Substring(closeIdx + 1).Trim();
                return true;
            }
        }

        result = input;
        return false;
    }

    private static bool TryGetHighConfidenceScriptLanguage(string text, out string language)
    {
        int kanaCount = 0;
        int hangulCount = 0;
        int asciiLetterCount = 0;

        foreach (char c in text)
        {
            if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF') || (c >= '\u31F0' && c <= '\u31FF'))
            {
                kanaCount++;
            }
            else if ((c >= '\uAC00' && c <= '\uD7AF') || (c >= '\u1100' && c <= '\u11FF') || (c >= '\u3130' && c <= '\u318F'))
            {
                hangulCount++;
            }
            else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                asciiLetterCount++;
            }
        }

        if (kanaCount >= 4)
        {
            language = "ja";
            return true;
        }
        if (hangulCount >= 4)
        {
            language = "ko";
            return true;
        }
        if (asciiLetterCount >= 28)
        {
            language = "en";
            return true;
        }

        language = string.Empty;
        return false;
    }

    private static string DetectLanguageFromScript(string text, string fallbackLang)
    {
        bool hasKana = false;
        bool hasHangul = false;
        bool hasCyrillic = false;

        foreach (char c in text)
        {
            if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF') || (c >= '\u31F0' && c <= '\u31FF'))
            {
                hasKana = true;
            }
            else if ((c >= '\uAC00' && c <= '\uD7AF') || (c >= '\u1100' && c <= '\u11FF') || (c >= '\u3130' && c <= '\u318F'))
            {
                hasHangul = true;
            }
            else if (c >= '\u0400' && c <= '\u04FF')
            {
                hasCyrillic = true;
            }
        }

        if (hasKana)
        {
            return "ja";
        }
        if (hasHangul)
        {
            return "ko";
        }
        if (hasCyrillic)
        {
            return "ru";
        }

        return string.IsNullOrWhiteSpace(fallbackLang) ? "auto" : fallbackLang;
    }

    private static bool ContainsAsciiLetters(string text)
    {
        foreach (char c in text)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsEnglishOrChinese(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return false;
        }

        return lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
               lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
               lang.StartsWith("cmn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHallucinationOrNoise(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        // 过滤 Whisper 常见的静音/背景音乐幻觉标记
        if ((text.StartsWith('[') && text.EndsWith(']')) ||
            (text.StartsWith('(') && text.EndsWith(')')) ||
            (text.StartsWith('（') && text.EndsWith('）')) ||
            (text.StartsWith('【') && text.EndsWith('】')) ||
            (text.StartsWith('*') && text.EndsWith('*')) ||
            (text.StartsWith('♪') || text.EndsWith('♪')))
        {
            return true;
        }

        string trimmed = text.Trim(' ', '。', '、', '！', '!', '？', '?', '…', '・', '「', '」');
        if (trimmed.Length == 0 ||
            trimmed == "ん" ||
            trimmed == "音楽" ||
            trimmed == "拍手" ||
            trimmed == "笑" ||
            trimmed == "アンディング" ||
            trimmed == "エンディング" ||
            trimmed == "オープニング")
        {
            return true;
        }

        // 过滤连续 4 个以上重复字符的拖长音/底噪死循环幻觉（例如 "ああああああ..."）
        if (HasExcessiveCharacterRepetition(trimmed, 4))
        {
            return true;
        }

        string upper = text.ToUpperInvariant();
        if (upper.Contains("BLANK_AUDIO") ||
            upper.Contains("SUBTITLES BY") ||
            upper.Contains("AMARA.ORG") ||
            upper.Contains("MUSIC PLAYING") ||
            upper.Contains("BACKGROUND MUSIC") ||
            upper.Contains("UPBEAT MUSIC") ||
            upper.Contains("ご視聴ありがとうございました") ||
            upper.Contains("チャンネル登録") ||
            upper.Contains("高評価") ||
            upper.Contains("字幕制作") ||
            upper.Contains("字幕提供") ||
            upper.Contains("请不吝点赞"))
        {
            return true;
        }

        return false;
    }

    private static bool HasExcessiveCharacterRepetition(string text, int maxRepeat)
    {
        if (string.IsNullOrEmpty(text) || text.Length < maxRepeat)
        {
            return false;
        }

        int run = 1;
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] == text[i - 1])
            {
                run++;
                if (run >= maxRepeat)
                {
                    return true;
                }
            }
            else
            {
                run = 1;
            }
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            var proc = _processor;
            _processor = null;
            if (proc != null)
            {
                _ = proc.DisposeAsync();
            }
            _whisperFactory?.Dispose();
            _inferLock.Dispose();
        }
        catch
        {
            // 忽略退出释放异常
        }
    }
}
