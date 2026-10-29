using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyMediaPlayer.Services;

/// <summary>
/// 离线机器翻译服务 (Microsoft.ML.OnnxRuntime + SentencePiece Unigram 分词器)：
/// 封装 Opus-MT (MarianMT) Encoder-Decoder ONNX 模型，将 Whisper 识别出的外语实时离线翻译为中文
/// </summary>
public class TranslateService : IDisposable
{
    private const int EosTokenId = 0;
    private const int UnkTokenId = 1;
    private const int PadAndStartTokenId = 65000;
    private const char SentencePieceSpace = '\u2581'; // '▁'

    private record ModelAsset(string RelativeRemotePath, string LocalFileName, long ExpectedSizeBytes);

    private static readonly ModelAsset[] RequiredAssets =
    {
        new("vocab.json", "vocab.json", 1_747_795),
        new("tokenizer.json", "tokenizer.json", 6_380_952),
        new("onnx/encoder_model_quantized.onnx", "encoder_model_quantized.onnx", 52_899_742),
        new("onnx/decoder_model_quantized.onnx", "decoder_model_quantized.onnx", 59_842_102)
    };

    private static readonly string[] RepoBaseUrls =
    {
        "https://hf-mirror.com/Xenova/opus-mt-en-zh/resolve/main",
        "https://huggingface.co/Xenova/opus-mt-en-zh/resolve/main"
    };

    private readonly SemaphoreSlim _translateLock = new(1, 1);
    private InferenceSession? _encoderSession;
    private InferenceSession? _decoderSession;

    // SentencePiece Unigram 词表映射：Token文本 -> (Id, 对数概率得分)
    private readonly Dictionary<string, (int Id, float Score)> _pieceToToken = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _idToPiece = new();
    private int _maxPieceLength = 16;

    public string ModelDirectory { get; private set; }
    public bool IsModelReady => _encoderSession != null && _decoderSession != null && _pieceToToken.Count > 0;

    public TranslateService()
    {
        ModelDirectory = ResolveModelDirectory();
    }

    public void SetModelRootDirectory(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return;
        }

        string newDir = Path.Combine(rootDirectory, "opus-mt-en-zh");
        Directory.CreateDirectory(newDir);
        if (!string.Equals(ModelDirectory, newDir, StringComparison.OrdinalIgnoreCase))
        {
            ModelDirectory = newDir;
            _encoderSession?.Dispose();
            _encoderSession = null;
            _decoderSession?.Dispose();
            _decoderSession = null;
            _pieceToToken.Clear();
            _idToPiece.Clear();
        }
    }

    private static string ResolveModelDirectory()
    {
        string devAssetsDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets"));
        if (Directory.Exists(devAssetsDir))
        {
            return Path.Combine(devAssetsDir, "opus-mt-en-zh");
        }

        string localAssetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "opus-mt-en-zh");
        Directory.CreateDirectory(localAssetsDir);
        return localAssetsDir;
    }

    /// <summary>
    /// 检测本地 Assets/opus-mt-en-zh 下的 ONNX 模型与词表文件是否已齐全
    /// </summary>
    public bool IsModelFilePresent()
    {
        foreach (var asset in RequiredAssets)
        {
            string fullPath = Path.Combine(ModelDirectory, asset.LocalFileName);
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length < asset.ExpectedSizeBytes * 0.8)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 确保 ONNX 离线翻译模型与分词词表就绪；若缺失且用户已授权则从镜像流式下载
    /// </summary>
    public async Task<bool> EnsureModelReadyAsync(
        bool allowDownload,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        await _translateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsModelReady)
            {
                return true;
            }

            if (!IsModelFilePresent())
            {
                if (!allowDownload)
                {
                    return false;
                }

                bool downloaded = await DownloadAssetsAsync(statusProgress, cancellationToken).ConfigureAwait(false);
                if (!downloaded)
                {
                    return false;
                }
            }

            statusProgress?.Report("正在加载 ONNX 离线翻译模型 (opus-mt-en-zh)...");

            LoadTokenizerVocabulary(
                Path.Combine(ModelDirectory, "vocab.json"),
                Path.Combine(ModelDirectory, "tokenizer.json"));

            var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
            };

            _encoderSession?.Dispose();
            _decoderSession?.Dispose();

            _encoderSession = new InferenceSession(Path.Combine(ModelDirectory, "encoder_model_quantized.onnx"), sessionOptions);
            _decoderSession = new InferenceSession(Path.Combine(ModelDirectory, "decoder_model_quantized.onnx"), sessionOptions);

            statusProgress?.Report("ONNX 离线翻译引擎就绪");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TranslateService] 模型初始化失败: {ex}");
            Console.WriteLine($"[TranslateService] 模型初始化失败: {ex.Message}");
            statusProgress?.Report($"翻译模型加载失败（将仅显示识别原文）: {ex.Message}");
            return false;
        }
        finally
        {
            _translateLock.Release();
        }
    }

    private async Task<bool> DownloadAssetsAsync(
        IProgress<string>? statusProgress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ModelDirectory);

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("vvPlayer/1.0");

        for (int i = 0; i < RequiredAssets.Length; i++)
        {
            var asset = RequiredAssets[i];
            string targetPath = Path.Combine(ModelDirectory, asset.LocalFileName);
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length >= asset.ExpectedSizeBytes * 0.8)
            {
                continue;
            }

            string tempPath = targetPath + ".downloading";
            bool fileSuccess = false;

            foreach (string baseUrl in RepoBaseUrls)
            {
                string url = $"{baseUrl}/{asset.RelativeRemotePath}";
                try
                {
                    statusProgress?.Report($"正在下载翻译组件 ({i + 1}/{RequiredAssets.Length}) {asset.LocalFileName}...");
                    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    long totalBytes = response.Content.Headers.ContentLength ?? asset.ExpectedSizeBytes;
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
                                statusProgress?.Report(
                                    $"下载翻译模型 ({i + 1}/{RequiredAssets.Length}) {asset.LocalFileName}: {mbRead:F1}/{mbTotal:F1} MB ({percent}%)");
                                lastReport = DateTime.UtcNow;
                            }
                        }
                    }

                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                    File.Move(tempPath, targetPath);
                    fileSuccess = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TranslateService] 从 {url} 下载失败: {ex.Message}");
                }
            }

            if (!fileSuccess)
            {
                statusProgress?.Report($"翻译模型 {asset.LocalFileName} 下载失败");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 解析 vocab.json 与 tokenizer.json，构建 SentencePiece Unigram 动态规划分词器
    /// </summary>
    private void LoadTokenizerVocabulary(string vocabJsonPath, string tokenizerJsonPath)
    {
        _pieceToToken.Clear();
        _idToPiece.Clear();
        _maxPieceLength = 1;

        var pieceToId = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var vocabStream = File.OpenRead(vocabJsonPath))
        using (var vocabDoc = JsonDocument.Parse(vocabStream))
        {
            foreach (var prop in vocabDoc.RootElement.EnumerateObject())
            {
                int id = prop.Value.GetInt32();
                pieceToId[prop.Name] = id;
                _idToPiece[id] = prop.Name;
                if (prop.Name.Length > _maxPieceLength)
                {
                    _maxPieceLength = prop.Name.Length;
                }
            }
        }

        // 从 tokenizer.json 读取每个 Unigram piece 的对数概率分数
        var pieceScores = new Dictionary<string, float>(StringComparer.Ordinal);
        if (File.Exists(tokenizerJsonPath))
        {
            using var tokStream = File.OpenRead(tokenizerJsonPath);
            using var tokDoc = JsonDocument.Parse(tokStream);
            if (tokDoc.RootElement.TryGetProperty("model", out var modelElem) &&
                modelElem.TryGetProperty("vocab", out var vocabArray) &&
                vocabArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in vocabArray.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
                    {
                        string? piece = item[0].GetString();
                        float score = item[1].GetSingle();
                        if (!string.IsNullOrEmpty(piece))
                        {
                            pieceScores[piece] = score;
                        }
                    }
                }
            }
        }

        foreach (var kvp in pieceToId)
        {
            // 排除特殊控制 Token，防止 Viterbi 分词误选 </s> 或 <unk> (其在 tokenizer.json 中得分为 0.0)
            if (kvp.Value == EosTokenId || kvp.Value == UnkTokenId || kvp.Value == PadAndStartTokenId)
            {
                continue;
            }
            if (kvp.Key.StartsWith(">>", StringComparison.Ordinal) && kvp.Key.EndsWith("<<", StringComparison.Ordinal))
            {
                continue;
            }

            // 在 MarianMT tokenizer.json 中，得分为 -100 的条目均为目标语言(中文)专属词表，绝不能进入源语言编码分词器
            if (!pieceScores.TryGetValue(kvp.Key, out float score) || score <= -99.0f)
            {
                continue;
            }

            _pieceToToken[kvp.Key] = (kvp.Value, score);
        }
    }

    /// <summary>
    /// 使用 Viterbi 动态规划执行 SentencePiece Unigram 分词
    /// </summary>
    public long[] Encode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new long[] { EosTokenId };
        }

        // Metaspace 预处理：按空白字符切分单词，并在每个词前加上 '▁' (\u2581)
        string[] words = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tokenIds = new List<long>(words.Length * 2 + 2);

        foreach (string rawWord in words)
        {
            string word = SentencePieceSpace + rawWord;
            int n = word.Length;

            float[] bestScore = new float[n + 1];
            int[] bestPrev = new int[n + 1];
            int[] bestId = new int[n + 1];

            Array.Fill(bestScore, float.NegativeInfinity);
            bestScore[0] = 0f;

            for (int end = 1; end <= n; end++)
            {
                int minStart = Math.Max(0, end - _maxPieceLength);
                for (int start = minStart; start < end; start++)
                {
                    if (float.IsNegativeInfinity(bestScore[start]))
                    {
                        continue;
                    }

                    string sub = word.Substring(start, end - start);
                    if (_pieceToToken.TryGetValue(sub, out var tok))
                    {
                        float candidateScore = bestScore[start] + tok.Score;
                        if (candidateScore > bestScore[end])
                        {
                            bestScore[end] = candidateScore;
                            bestPrev[end] = start;
                            bestId[end] = tok.Id;
                        }
                    }
                }

                // 若没有任何词表子串覆盖当前字符，退化为单字符 <unk>
                if (float.IsNegativeInfinity(bestScore[end]))
                {
                    bestScore[end] = bestScore[end - 1] - 100f;
                    bestPrev[end] = end - 1;
                    bestId[end] = UnkTokenId;
                }
            }

            // 回溯收集当前单词的最佳分词序列
            var wordTokens = new List<long>(8);
            int curr = n;
            while (curr > 0)
            {
                wordTokens.Add(bestId[curr]);
                curr = bestPrev[curr];
            }
            wordTokens.Reverse();
            tokenIds.AddRange(wordTokens);
        }

        tokenIds.Add(EosTokenId);
        return tokenIds.ToArray();
    }

    /// <summary>
    /// 将模型输出的 Token ID 序列还原为中文文本
    /// </summary>
    public string Decode(IEnumerable<long> tokenIds)
    {
        var sb = new StringBuilder();
        foreach (long id in tokenIds)
        {
            int intId = (int)id;
            if (intId == EosTokenId || intId == UnkTokenId || intId == PadAndStartTokenId)
            {
                continue;
            }

            if (_idToPiece.TryGetValue(intId, out string? piece))
            {
                if (piece.StartsWith(">>", StringComparison.Ordinal) && piece.EndsWith("<<", StringComparison.Ordinal))
                {
                    continue;
                }
                sb.Append(piece);
            }
        }

        return sb.ToString().Replace(SentencePieceSpace, ' ').Trim();
    }

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8.0)
    };

    private readonly ConcurrentDictionary<string, string> _translationCache = new(StringComparer.Ordinal);

    public bool TryGetCachedTranslation(string sourceText, string sourceLang, string targetLang, out string translatedText)
    {
        translatedText = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return false;
        }

        if (sourceLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
            sourceLang.StartsWith("cmn", StringComparison.OrdinalIgnoreCase))
        {
            translatedText = sourceText;
            return true;
        }

        string cacheKey = $"{sourceLang.ToLowerInvariant()}:{targetLang.ToLowerInvariant()}:{sourceText.Trim()}";
        if (_translationCache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            translatedText = cached;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 执行机器翻译：
    /// 1. 英语源文本直接使用本地 ONNX (opus-mt-en-zh) 推理
    /// 2. 日语/韩语/俄语等非英语源文本：由于 opus-mt-en-zh 仅支持英语输入，优先直接多语种翻译，离线时自动使用 Whisper 英语中转文本经过 ONNX 译为中文
    /// </summary>
    public async Task<string> TranslateAsync(
        string sourceText,
        string sourceLang,
        string targetLang = "zh",
        CancellationToken cancellationToken = default,
        string? englishPivotText = null)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return string.Empty;
        }

        // 如果源语言已经是中文，无需翻译直接返回
        if (sourceLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
            sourceLang.StartsWith("cmn", StringComparison.OrdinalIgnoreCase))
        {
            return sourceText;
        }

        string cacheKey = $"{sourceLang.ToLowerInvariant()}:{targetLang.ToLowerInvariant()}:{sourceText.Trim()}";
        if (_translationCache.TryGetValue(cacheKey, out string? cachedTranslation) &&
            !string.IsNullOrWhiteSpace(cachedTranslation))
        {
            return cachedTranslation;
        }

        bool isEnglishSource = IsPrimarilyEnglish(sourceText, sourceLang);

        // 非英语源语言（如日语 ja、韩语 ko 等）：直接针对识别出的外语文本执行多语种精翻，避免 Whisper 语音直译产生对不起/谢谢等噪声幻觉
        if (!isEnglishSource)
        {
            string? multiLangTranslated = await TryTranslateMultiLangTextAsync(sourceText, sourceLang, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(multiLangTranslated))
            {
                _translationCache[cacheKey] = multiLangTranslated;
                string logLine = $"[MultiLang MT] ({sourceLang}) {sourceText} => {multiLangTranslated}";
                Console.WriteLine(logLine);
                Debug.WriteLine(logLine);
                return multiLangTranslated;
            }

            if (!string.IsNullOrWhiteSpace(englishPivotText) && IsModelReady && ContainsAsciiLetters(englishPivotText))
            {
                await _translateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    string pivotZh = await Task.Run(() => RunOnnxSeq2SeqInference(englishPivotText, cancellationToken), cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(pivotZh) && !string.Equals(pivotZh, englishPivotText, StringComparison.Ordinal))
                    {
                        _translationCache[cacheKey] = pivotZh;
                        return pivotZh;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 忽略异常
                }
                finally
                {
                    _translateLock.Release();
                }
            }

            return sourceText;
        }

        if (!IsModelReady)
        {
            return sourceText;
        }

        await _translateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string enZhResult = await Task.Run(() => RunOnnxSeq2SeqInference(sourceText, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(enZhResult) && !string.Equals(enZhResult, sourceText, StringComparison.Ordinal))
            {
                _translationCache[cacheKey] = enZhResult;
            }
            return enZhResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TranslateService] 推理异常: {ex.Message}");
            return sourceText;
        }
        finally
        {
            _translateLock.Release();
        }
    }

    private static bool IsPrimarilyEnglish(string text, string sourceLang)
    {
        if (!string.IsNullOrWhiteSpace(sourceLang) &&
            !sourceLang.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !sourceLang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int asciiLetters = 0;
        int nonAsciiLetters = 0;
        foreach (char c in text)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                asciiLetters++;
            }
            else if (char.IsLetter(c) && c > 127)
            {
                nonAsciiLetters++;
            }
        }

        return asciiLetters > 0 && asciiLetters >= nonAsciiLetters;
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

    private static async Task<string?> TryTranslateMultiLangTextAsync(
        string sourceText,
        string sourceLang,
        CancellationToken cancellationToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        raceCts.CancelAfter(TimeSpan.FromMilliseconds(6000));

        // 优先调用国内低延迟接口 (~100ms)，若成功则直接返回，避免无谓消耗海外慢速接口连接
        string? youdaoResult = await TryYoudaoTranslateAsync(sourceText, sourceLang, raceCts.Token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(youdaoResult))
        {
            return youdaoResult;
        }

        return await TryMyMemoryTranslateAsync(sourceText, sourceLang, raceCts.Token).ConfigureAwait(false);
    }

    private static string ResolveYoudaoTranslateType(string sourceLang)
    {
        if (string.IsNullOrWhiteSpace(sourceLang))
        {
            return "AUTO";
        }

        return sourceLang.Trim().ToLowerInvariant() switch
        {
            "ja" or "jp" => "JA2ZH_CN",
            "ko" or "kr" => "KR2ZH_CN",
            "en" => "EN2ZH_CN",
            "fr" => "FR2ZH_CN",
            "ru" => "RU2ZH_CN",
            "es" or "sp" => "SP2ZH_CN",
            _ => "AUTO"
        };
    }

    private static async Task<string?> TryYoudaoTranslateAsync(string sourceText, string sourceLang, CancellationToken token)
    {
        string youdaoType = ResolveYoudaoTranslateType(sourceLang);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1800));

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://m.youdao.com/translate");
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15");
                req.Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("inputtext", sourceText),
                    new KeyValuePair<string, string>("type", youdaoType)
                });

                using var resp = await SharedHttpClient.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    // 注意：m.youdao.com 返回的 Content-Type 为 "text/html; charset=UTF8" (缺少连字符)，
                    // 直接调用 ReadAsStringAsync 会因 .NET 不识别 "UTF8" 字符集名称而抛出 InvalidOperationException！
                    // 因此必须读取原始字节流并显式使用 Encoding.UTF8 解码。
                    byte[] rawBytes = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
                    string html = Encoding.UTF8.GetString(rawBytes);

                    const string startMarker = "<ul id=\"translateResult\">";
                    int ulIndex = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
                    if (ulIndex >= 0)
                    {
                        int liStart = html.IndexOf("<li>", ulIndex, StringComparison.OrdinalIgnoreCase);
                        int liEnd = liStart >= 0 ? html.IndexOf("</li>", liStart, StringComparison.OrdinalIgnoreCase) : -1;
                        if (liStart >= 0 && liEnd > liStart)
                        {
                            string rawTranslated = System.Net.WebUtility.HtmlDecode(html.Substring(liStart + 4, liEnd - (liStart + 4))).Trim();
                            if (!string.IsNullOrWhiteSpace(rawTranslated))
                            {
                                return rawTranslated;
                            }
                        }
                    }
                }
            }
            catch
            {
                // 忽略单次异常，重试一次
            }
        }

        return null;
    }

    private static async Task<string?> TryMyMemoryTranslateAsync(string sourceText, string sourceLang, CancellationToken token)
    {
        string sl = string.IsNullOrWhiteSpace(sourceLang) || sourceLang.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "ja"
            : sourceLang;

        string encodedText = Uri.EscapeDataString(sourceText);
        string url = $"https://api.mymemory.translated.net/get?q={encodedText}&langpair={sl}|zh-CN";

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                attemptCts.CancelAfter(TimeSpan.FromMilliseconds(4500));

                using var response = await SharedHttpClient.GetAsync(url, attemptCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                string json = await response.Content.ReadAsStringAsync(attemptCts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("responseData", out var respData) &&
                    respData.TryGetProperty("translatedText", out var transElem) &&
                    transElem.ValueKind == JsonValueKind.String)
                {
                    string? result = transElem.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(result) && !result.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                    {
                        return result;
                    }
                }
            }
            catch
            {
                if (attempt == 0 && !token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(150, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        return null;
    }

    private string RunOnnxSeq2SeqInference(string sourceText, CancellationToken cancellationToken)
    {
        if (_encoderSession == null || _decoderSession == null)
        {
            return sourceText;
        }

        // opus-mt-en-zh 仅支持包含英文字母的文本，若不含英文字母则直接返回避免产生乱码
        if (!ContainsAsciiLetters(sourceText))
        {
            return sourceText;
        }

        long[] inputIds = Encode(sourceText);
        int srcLen = inputIds.Length;

        int unkCount = inputIds.Count(id => id == UnkTokenId);
        int contentCount = Math.Max(1, srcLen - 1);
        if (unkCount * 2 >= contentCount)
        {
            return sourceText;
        }

        long[] attentionMask = Enumerable.Repeat(1L, srcLen).ToArray();

        var inputIdsTensor = new DenseTensor<long>(inputIds, new[] { 1, srcLen });
        var attnMaskTensor = new DenseTensor<long>(attentionMask, new[] { 1, srcLen });

        var encoderInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attnMaskTensor)
        };

        using var encoderResults = _encoderSession.Run(encoderInputs);
        var encoderHiddenTensor = encoderResults.First().AsTensor<float>();

        // 自回归解码：初始输入为 decoder_start_token_id (65000)
        var generatedIds = new List<long>(64) { PadAndStartTokenId };
        var tokenCounts = new Dictionary<int, int>();
        int maxTargetTokens = Math.Clamp(srcLen * 3 + 8, 16, 72);

        for (int step = 0; step < maxTargetTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int tgtLen = generatedIds.Count;
            var decoderInputTensor = new DenseTensor<long>(generatedIds.ToArray(), new[] { 1, tgtLen });

            var decoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", decoderInputTensor),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHiddenTensor),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attnMaskTensor)
            };

            using var decoderResults = _decoderSession.Run(decoderInputs);
            var logitsTensor = decoderResults.First().AsTensor<float>();

            int vocabSize = logitsTensor.Dimensions[2];
            int lastStepIndex = tgtLen - 1;
            int lastTokenId = (int)generatedIds[lastStepIndex];

            int bestTokenId = EosTokenId;
            float bestLogit = float.NegativeInfinity;

            for (int v = 0; v < vocabSize; v++)
            {
                // 屏蔽 <pad> (65000)、<unk> (1) 以及连续重复的相同 Token
                if (v == PadAndStartTokenId || v == UnkTokenId)
                {
                    continue;
                }

                if (v != EosTokenId && v == lastTokenId)
                {
                    continue;
                }

                // 屏蔽 2-gram 循环重复 (如 A B A B)
                if (tgtLen >= 4 &&
                    v == (int)generatedIds[tgtLen - 2] &&
                    lastTokenId == (int)generatedIds[tgtLen - 3])
                {
                    continue;
                }

                float logit = logitsTensor[0, lastStepIndex, v];

                // 按已出现次数指数级递增重复惩罚，彻底杜绝破折号/标点死循环
                if (tokenCounts.TryGetValue(v, out int count) && count > 0)
                {
                    float penalty = MathF.Pow(1.65f, count);
                    logit = logit > 0 ? logit / penalty : logit * penalty;
                }

                if (logit > bestLogit)
                {
                    bestLogit = logit;
                    bestTokenId = v;
                }
            }

            if (bestTokenId == EosTokenId)
            {
                break;
            }

            generatedIds.Add(bestTokenId);
            tokenCounts[bestTokenId] = tokenCounts.TryGetValue(bestTokenId, out int c) ? c + 1 : 1;
        }

        string translated = SanitizeTranslatedOutput(Decode(generatedIds));
        if (!string.IsNullOrWhiteSpace(translated))
        {
            string logLine = $"[ONNX MT] {sourceText} => {translated}";
            Console.WriteLine(logLine);
            Debug.WriteLine(logLine);
            return translated;
        }

        return sourceText;
    }

    private static string SanitizeTranslatedOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmed = text.Trim();

        // 若输出全为破折号、横线或标点符号，判定为无效幻觉并丢弃
        bool hasMeaningfulChar = false;
        foreach (char c in trimmed)
        {
            if (char.IsLetterOrDigit(c))
            {
                hasMeaningfulChar = true;
                break;
            }
        }

        return hasMeaningfulChar ? trimmed : string.Empty;
    }

    public void Dispose()
    {
        _encoderSession?.Dispose();
        _decoderSession?.Dispose();
        _translateLock.Dispose();
    }
}
