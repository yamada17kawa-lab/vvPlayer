using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using MyMediaPlayer.Models;
using MyMediaPlayer.Services;

namespace MyMediaPlayer.ViewModels;

/// <summary>
/// 主界面 ViewModel（阶段四完整合体版）：
/// 串联后台超前音频流提取 -> Whisper ASR 识别 -> ONNX 离线翻译 -> 线程安全字幕缓存池 -> UI 实时同步，
/// 并支持源语言/目标语言动态切换与 SRT 双语字幕导出。
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly MediaService _mediaService;
    private readonly AsrService _asrService;
    private readonly TranslateService _translateService;
    private readonly List<SubtitleModel> _subtitleCache = new();
    private readonly List<(TimeSpan Start, TimeSpan End)> _processedRanges = new();
    private readonly SemaphoreSlim _mtConcurrencyLimiter = new(2, 2);
    private readonly object _subtitleLock = new();
    private SubtitleModel? _activeSubtitle;

    private CancellationTokenSource? _pipelineCts;
    private DateTime _lastSeekUtc = DateTime.MinValue;
    private double _lastSeekTargetSeconds;

    public MediaPlayer MediaPlayer => _mediaService.Player;

    public IReadOnlyList<LanguageOption> SourceLanguages { get; } = new[]
    {
        new LanguageOption("自动检测 (Auto)", "auto"),
        new LanguageOption("英语 (EN)", "en"),
        new LanguageOption("日语 (JA)", "ja"),
        new LanguageOption("韩语 (KO)", "ko"),
        new LanguageOption("法语 (FR)", "fr"),
        new LanguageOption("德语 (DE)", "de"),
        new LanguageOption("俄语 (RU)", "ru"),
        new LanguageOption("西班牙语 (ES)", "es"),
        new LanguageOption("中文 (ZH)", "zh")
    };

    public IReadOnlyList<LanguageOption> TargetLanguages { get; } = new[]
    {
        new LanguageOption("译为中文 (ZH)", "zh"),
        new LanguageOption("仅识别原文", "none")
    };

    [ObservableProperty]
    private LanguageOption _selectedSourceLanguage;

    [ObservableProperty]
    private LanguageOption _selectedTargetLanguage;

    [ObservableProperty]
    private string _videoTitle = "未加载视频 - 点击「打开视频」或拖入视频文件";

    [ObservableProperty]
    private string _windowTitle = "vv播放器";

    partial void OnVideoTitleChanged(string value)
    {
        WindowTitle = string.IsNullOrWhiteSpace(value) || value.StartsWith("未加载视频")
            ? "vv播放器"
            : $"{value} - vv播放器";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseButtonText))]
    private bool _isPlaying;

    public string PlayPauseButtonText => IsPlaying ? "⏸ 暂停" : "▶ 播放";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiToggleButtonText))]
    private bool _isAiSubtitleEnabled;

    public string AiToggleButtonText => IsAiSubtitleEnabled ? "🤖 关闭 AI 字幕" : "🤖 开启 AI 字幕";

    [ObservableProperty]
    private double _currentTimeSeconds;

    [ObservableProperty]
    private double _totalDurationSeconds = 100;

    [ObservableProperty]
    private string _formattedCurrentTime = "00:00:00";

    [ObservableProperty]
    private string _formattedTotalDuration = "00:00:00";

    [ObservableProperty]
    private int _volume = 80;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteButtonText))]
    private bool _isMuted;

    public string MuteButtonText => IsMuted ? "🔇" : "🔊";

    [ObservableProperty]
    private string _currentOriginalSubtitle = string.Empty;

    [ObservableProperty]
    private string _currentTranslatedSubtitle = string.Empty;

    [ObservableProperty]
    private bool _isSubtitleVisible;

    [ObservableProperty]
    private bool _isCoverVisible = true;

    [ObservableProperty]
    private bool _showBilingual = true;

    [ObservableProperty]
    private string _aiStatusText = "普通播放模式";

    [ObservableProperty]
    private string _modelRootDirectory = string.Empty;

    private static string ModelConfigFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model_directory.txt");

    public bool IsDraggingSlider { get; set; }

    /// <summary>左侧播放目录（用户自建分类，只保存原视频地址）</summary>
    public PlaylistViewModel Playlist { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaylistToggleButtonText))]
    private bool _isPlaylistVisible = true;

    public string PlaylistToggleButtonText => IsPlaylistVisible ? "📋 隐藏目录" : "📋 播放目录";

    [RelayCommand]
    private void TogglePlaylist()
    {
        IsPlaylistVisible = !IsPlaylistVisible;
    }

    public MainViewModel()
    {
        _selectedSourceLanguage = SourceLanguages[0];
        _selectedTargetLanguage = TargetLanguages[0];

        _mediaService = new MediaService();
        _asrService = new AsrService();
        _translateService = new TranslateService();
        Playlist = new PlaylistViewModel(LoadVideoFile, () => _mediaService.CurrentFilePath);

        InitializeModelRootDirectory();

        _mediaService.SetVolume(Volume);

        _mediaService.TimeChanged += OnMediaTimeChanged;
        _mediaService.LengthChanged += OnMediaLengthChanged;
        _mediaService.PlayingStateChanged += OnPlayingStateChanged;

        // 若本地已有完整模型（Whisper + ONNX翻译）则默认开启并在后台预热加载到内存，打开视频时零延迟启动识别
        if (_asrService.IsModelFilePresent() && _translateService.IsModelFilePresent())
        {
            IsAiSubtitleEnabled = true;
            AiStatusText = $"正在后台预热 AI 模型...";
            _ = Task.Run(async () =>
            {
                await _asrService.EnsureModelReadyAsync(allowDownload: false, language: _selectedSourceLanguage.Code).ConfigureAwait(false);
                await _translateService.EnsureModelReadyAsync(allowDownload: false).ConfigureAwait(false);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (string.IsNullOrEmpty(_mediaService.CurrentFilePath))
                    {
                        AiStatusText = $"AI 模型已就绪 ({ModelRootDirectory})";
                    }
                });
            });
        }
        else
        {
            IsAiSubtitleEnabled = false;
            AiStatusText = "普通播放模式 (点击底部「开启 AI 字幕」可按需下载离线模型)";
        }
    }

    private void InitializeModelRootDirectory()
    {
        string defaultDir = Path.GetDirectoryName(_asrService.ModelFilePath)
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");

        if (File.Exists(ModelConfigFilePath))
        {
            try
            {
                string savedDir = File.ReadAllText(ModelConfigFilePath, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(savedDir))
                {
                    defaultDir = savedDir;
                }
            }
            catch
            {
                // 忽略配置文件读取异常，使用默认目录
            }
        }

        ApplyModelRootDirectory(defaultDir, saveConfig: false);
    }

    private void ApplyModelRootDirectory(string rootDir, bool saveConfig)
    {
        Directory.CreateDirectory(rootDir);
        ModelRootDirectory = rootDir;
        _asrService.SetModelRootDirectory(rootDir);
        _translateService.SetModelRootDirectory(rootDir);

        if (saveConfig)
        {
            try
            {
                File.WriteAllText(ModelConfigFilePath, rootDir, Encoding.UTF8);
            }
            catch
            {
                // 忽略保存异常
            }
        }
    }

    [RelayCommand]
    private void SelectModelDirectory()
    {
        if (PromptForModelDirectory(out string? selectedDir) && !string.IsNullOrWhiteSpace(selectedDir))
        {
            ApplyModelRootDirectory(selectedDir, saveConfig: true);
            bool ready = _asrService.IsModelFilePresent() && _translateService.IsModelFilePresent();
            AiStatusText = ready
                ? $"📁 已切换模型目录 (模型已就绪): {selectedDir}"
                : $"📁 已指定模型下载目录: {selectedDir}";
        }
    }

    private bool PromptForModelDirectory(out string? selectedDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 AI 离线模型存放/下载目录",
            InitialDirectory = Directory.Exists(ModelRootDirectory)
                ? ModelRootDirectory
                : AppDomain.CurrentDomain.BaseDirectory
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            selectedDirectory = dialog.FolderName;
            return true;
        }

        selectedDirectory = null;
        return false;
    }

    partial void OnSelectedSourceLanguageChanged(LanguageOption value)
    {
        _asrService.ResetAutoLockedLanguage();
        if (!IsAiSubtitleEnabled || string.IsNullOrEmpty(_mediaService.CurrentFilePath))
        {
            return;
        }

        // 切换源语言时，彻底清空旧语言识别缓存；若处于前 15 秒或短视频则直接从 00:00 重新识别
        var now = _mediaService.CurrentPlaybackTime;
        var restartFrom = now.TotalSeconds <= 15.0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(0, now.TotalSeconds - 3.0));
        lock (_subtitleLock)
        {
            _subtitleCache.Clear();
            _processedRanges.Clear();
        }
        _activeSubtitle = null;
        CurrentOriginalSubtitle = string.Empty;
        CurrentTranslatedSubtitle = string.Empty;
        IsSubtitleVisible = false;
        RestartLookAheadPipeline(_mediaService.CurrentFilePath, restartFrom);
    }

    partial void OnSelectedTargetLanguageChanged(LanguageOption value)
    {
        if (!IsAiSubtitleEnabled || string.IsNullOrEmpty(_mediaService.CurrentFilePath))
        {
            return;
        }

        var now = _mediaService.CurrentPlaybackTime;
        var restartFrom = now.TotalSeconds <= 15.0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(0, now.TotalSeconds - 3.0));
        lock (_subtitleLock)
        {
            _subtitleCache.Clear();
            _processedRanges.Clear();
        }
        _activeSubtitle = null;
        CurrentOriginalSubtitle = string.Empty;
        CurrentTranslatedSubtitle = string.Empty;
        IsSubtitleVisible = false;
        RestartLookAheadPipeline(_mediaService.CurrentFilePath, restartFrom);
    }

    partial void OnVolumeChanged(int value)
    {
        _mediaService.SetVolume(value);
        if (value > 0 && IsMuted)
        {
            IsMuted = false;
            _mediaService.Player.Mute = false;
        }
    }

    [RelayCommand]
    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要播放的视频文件",
            Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v|所有文件|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            LoadVideoFile(dialog.FileName);
        }
    }

    public void LoadVideoFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        CancelLookAheadPipeline();
        _asrService.ResetAutoLockedLanguage();

        VideoTitle = Path.GetFileName(filePath);
        lock (_subtitleLock)
        {
            _subtitleCache.Clear();
            _processedRanges.Clear();
        }
        _activeSubtitle = null;

        CurrentOriginalSubtitle = string.Empty;
        CurrentTranslatedSubtitle = string.Empty;
        IsSubtitleVisible = false;
        IsCoverVisible = false;
        CurrentTimeSeconds = 0;
        FormattedCurrentTime = "00:00:00";
        FormattedTotalDuration = "00:00:00";

        // 1. 直接开始播放视频，绝不主动弹窗
        _mediaService.LoadAndPlay(filePath);
        Playlist.MarkPlaying(filePath);

        // 2. 仅当 AI 字幕已开启时才启动后台识别与翻译管线
        if (IsAiSubtitleEnabled)
        {
            RestartLookAheadPipeline(filePath, TimeSpan.Zero);
        }
        else
        {
            AiStatusText = "普通播放模式 (未启用 AI 字幕)";
        }
    }

    /// <summary>
    /// 用户主动点击底部「开启/关闭 AI 字幕」按钮：仅在此刻检测缺失模型并弹出一次合一确认框
    /// </summary>
    [RelayCommand]
    private void ToggleAiSubtitle()
    {
        if (IsAiSubtitleEnabled)
        {
            IsAiSubtitleEnabled = false;
            CancelLookAheadPipeline();
            IsSubtitleVisible = false;
            AiStatusText = "普通播放模式 (AI 字幕已关闭)";
            return;
        }

        bool needTranslation = SelectedTargetLanguage.Code != "none";
        bool hasAsr = _asrService.IsModelFilePresent();
        bool hasMt = !needTranslation || _translateService.IsModelFilePresent();

        if (!hasAsr || !hasMt)
        {
            var missingItems = new List<string>();
            if (!hasAsr)
            {
                missingItems.Add("• Whisper 语音识别模型 (ggml-base.bin，约 141 MB)");
            }
            if (!hasMt)
            {
                missingItems.Add("• ONNX 离线英中翻译模型 (opus-mt-en-zh，约 115 MB)");
            }

            var result = MessageBox.Show(
                "开启 AI 实时翻译字幕需要下载以下离线模型：\n\n" +
                string.Join("\n", missingItems) + "\n\n" +
                $"当前模型保存目录：\n{ModelRootDirectory}\n\n" +
                "• 点击「是(Y)」：直接下载到上述当前目录\n" +
                "• 点击「否(N)」：更改/指定其他下载目录\n" +
                "• 点击「取消」：不下载，继续普通视频播放",
                "下载离线 AI 翻译字幕模型",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel || result == MessageBoxResult.None)
            {
                IsAiSubtitleEnabled = false;
                AiStatusText = "普通播放模式 (未启用 AI 字幕)";
                return;
            }

            if (result == MessageBoxResult.No)
            {
                if (!PromptForModelDirectory(out string? customDir) || string.IsNullOrWhiteSpace(customDir))
                {
                    IsAiSubtitleEnabled = false;
                    AiStatusText = "普通播放模式 (已取消选择模型目录)";
                    return;
                }

                ApplyModelRootDirectory(customDir, saveConfig: true);
            }
        }

        IsAiSubtitleEnabled = true;

        if (!string.IsNullOrEmpty(_mediaService.CurrentFilePath))
        {
            RestartLookAheadPipeline(_mediaService.CurrentFilePath, _mediaService.CurrentPlaybackTime);
        }
        else
        {
            string srcLang = SelectedSourceLanguage.Code;
            bool downloadMt = needTranslation;

            _ = Task.Run(async () =>
            {
                var statusReporter = new Progress<string>(msg =>
                {
                    Application.Current?.Dispatcher.InvokeAsync(() => AiStatusText = msg);
                });

                bool asrOk = await _asrService.EnsureModelReadyAsync(true, srcLang, statusReporter);
                bool mtOk = !downloadMt || await _translateService.EnsureModelReadyAsync(true, statusReporter);

                if (!asrOk)
                {
                    Application.Current?.Dispatcher.InvokeAsync(() => IsAiSubtitleEnabled = false);
                }
                else if (mtOk)
                {
                    ((IProgress<string>)statusReporter).Report("✅ Whisper 识别与 ONNX 翻译引擎均已就绪");
                }
            });
        }
    }

    /// <summary>
    /// 将当前已识别并翻译的字幕池导出为标准双语 .srt 字幕文件
    /// </summary>
    [RelayCommand]
    private void ExportSrt()
    {
        List<SubtitleModel> snapshot;
        lock (_subtitleLock)
        {
            snapshot = _subtitleCache.ToList();
        }

        if (snapshot.Count == 0)
        {
            MessageBox.Show("当前字幕缓存池为空，请先开启 AI 字幕并播放视频。", "导出字幕", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string defaultName = string.IsNullOrEmpty(_mediaService.CurrentFilePath)
            ? "ai_subtitles.srt"
            : Path.ChangeExtension(Path.GetFileName(_mediaService.CurrentFilePath), ".srt");

        var dialog = new SaveFileDialog
        {
            Title = "导出 SRT 双语字幕文件",
            FileName = defaultName,
            Filter = "SRT 字幕文件 (*.srt)|*.srt|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < snapshot.Count; i++)
            {
                var sub = snapshot[i];
                sb.AppendLine((i + 1).ToString());
                sb.AppendLine($"{FormatSrtTime(sub.StartTime)} --> {FormatSrtTime(sub.EndTime)}");
                if (!string.IsNullOrWhiteSpace(sub.TranslatedText) &&
                    !string.Equals(sub.OriginalText, sub.TranslatedText, StringComparison.Ordinal))
                {
                    sb.AppendLine(sub.TranslatedText);
                    if (ShowBilingual)
                    {
                        sb.AppendLine(sub.OriginalText);
                    }
                }
                else
                {
                    sb.AppendLine(sub.OriginalText);
                }
                sb.AppendLine();
            }

            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
            AiStatusText = $"💾 已导出 {snapshot.Count} 条字幕至 {Path.GetFileName(dialog.FileName)}";
        }
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (string.IsNullOrEmpty(_mediaService.CurrentFilePath))
        {
            OpenFile();
            return;
        }

        IsCoverVisible = false;
        _mediaService.TogglePlayPause();
    }

    [RelayCommand]
    private void Stop()
    {
        CancelLookAheadPipeline();
        _mediaService.Stop();
        CurrentTimeSeconds = 0;
        FormattedCurrentTime = "00:00:00";
        IsSubtitleVisible = false;
        IsCoverVisible = true;
        AiStatusText = IsAiSubtitleEnabled ? "已停止" : "普通播放模式 (未启用 AI 字幕)";
    }

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        _mediaService.Player.Mute = IsMuted;
    }

    public void PreviewSeek(double targetSeconds)
    {
        var clamped = Math.Clamp(targetSeconds, 0, TotalDurationSeconds);
        var target = TimeSpan.FromSeconds(clamped);
        FormattedCurrentTime = FormatTimeSpan(target);
        if (IsAiSubtitleEnabled)
        {
            UpdateActiveSubtitle(target);
        }
    }

    /// <summary>
    /// 快退 10 秒（快捷键 Ctrl+← / J）
    /// </summary>
    [RelayCommand]
    private void SkipBackward()
    {
        SeekRelative(-10);
    }

    /// <summary>
    /// 快进 10 秒（快捷键 Ctrl+→ / L）
    /// </summary>
    [RelayCommand]
    private void SkipForward()
    {
        SeekRelative(10);
    }

    /// <summary>
    /// 以当前播放位置为基准相对跳转指定秒数（正数快进、负数快退），复用 CommitSeek 以保持 AI 字幕管线同步重启/复用逻辑
    /// </summary>
    public void SeekRelative(double deltaSeconds)
    {
        if (string.IsNullOrEmpty(_mediaService.CurrentFilePath))
        {
            return;
        }

        double target = Math.Clamp(CurrentTimeSeconds + deltaSeconds, 0, TotalDurationSeconds);
        CommitSeek(target);
    }

    public void CommitSeek(double targetSeconds)
    {
        var clamped = Math.Clamp(targetSeconds, 0, TotalDurationSeconds);
        var target = TimeSpan.FromSeconds(clamped);
        _lastSeekUtc = DateTime.UtcNow;
        _lastSeekTargetSeconds = clamped;

        CurrentTimeSeconds = clamped;
        FormattedCurrentTime = FormatTimeSpan(target);
        _mediaService.SeekTo(target);

        if (!IsAiSubtitleEnabled)
        {
            return;
        }

        UpdateActiveSubtitle(target);

        if (!string.IsNullOrEmpty(_mediaService.CurrentFilePath))
        {
            RestartLookAheadPipeline(_mediaService.CurrentFilePath, target);
        }
    }

    private void RestartLookAheadPipeline(string filePath, TimeSpan startPosition)
    {
        if (!IsAiSubtitleEnabled)
        {
            return;
        }

        CancelLookAheadPipeline();
        _pipelineCts = new CancellationTokenSource();
        var token = _pipelineCts.Token;
        string sourceLang = SelectedSourceLanguage.Code;
        string targetLang = SelectedTargetLanguage.Code;

        _ = Task.Run(() => RunLookAheadPipelineAsync(filePath, startPosition, sourceLang, targetLang, token), token);
    }

    public void CancelLookAheadPipeline()
    {
        try
        {
            _pipelineCts?.Cancel();
            _pipelineCts?.Dispose();
        }
        catch
        {
            // 忽略取消时的释放异常
        }
        finally
        {
            _pipelineCts = null;
        }
    }

    /// <summary>
    /// 方案 B 后台核心管线：超前提取 16kHz 音频 -> VAD 静音停顿切分 -> Whisper.net 识别 -> ONNX Runtime 翻译 -> 写入字幕缓存池
    /// </summary>
    private async Task RunLookAheadPipelineAsync(
        string filePath,
        TimeSpan startPosition,
        string sourceLangCode,
        string targetLangCode,
        CancellationToken cancellationToken)
    {
        var statusReporter = new Progress<string>(msg =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() => AiStatusText = msg);
        });

        try
        {
            bool asrReady = await _asrService.EnsureModelReadyAsync(
                allowDownload: true,
                language: sourceLangCode,
                statusProgress: statusReporter,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!asrReady)
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    IsAiSubtitleEnabled = false;
                    IsSubtitleVisible = false;
                });
                return;
            }

            if (targetLangCode != "none")
            {
                await _translateService.EnsureModelReadyAsync(
                    allowDownload: true,
                    statusProgress: statusReporter,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            ((IProgress<string>)statusReporter).Report($"⚡ 超前提取已启动 (起点 {FormatTimeSpan(startPosition)} | 源语言: {sourceLangCode.ToUpperInvariant()})");

            var pendingTranslations = new List<Task>();

            await foreach (var chunk in _mediaService.ExtractAudioChunksLookAheadAsync(
                               filePath,
                               startPosition,
                               lookAheadWindowSeconds: 60.0,
                               cancellationToken).ConfigureAwait(false))
            {
                if (IsChunkAlreadyCovered(chunk.StartTime, chunk.EndTime, out var untranslatedSubs))
                {
                    if (targetLangCode != "none" && untranslatedSubs.Count > 0)
                    {
                        foreach (var pendingSub in untranslatedSubs)
                        {
                            pendingTranslations.Add(TranslateSubtitleInBackgroundAsync(pendingSub, targetLangCode));
                        }
                    }
                    continue;
                }

                var nowPlayTime = _mediaService.CurrentPlaybackTime;
                double aheadSec = (chunk.StartTime - nowPlayTime).TotalSeconds;
                string effectiveLang = _asrService.EffectiveLanguage.ToUpperInvariant();
                string aheadTag = aheadSec >= 0 ? $"超前 +{aheadSec:F1}s" : $"追赶 {aheadSec:F1}s";
                ((IProgress<string>)statusReporter).Report(
                    $"🧠 AI 识别翻译中 [{chunk.StartTime:mm\\:ss} ~ {chunk.EndTime:mm\\:ss}] ({effectiveLang} | {aheadTag})");

                var recognizedList = await _asrService.TranscribeChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                MarkChunkRangeProcessed(chunk.StartTime, chunk.EndTime);

                foreach (var sub in recognizedList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (targetLangCode == "none")
                    {
                        sub.TranslatedText = string.Empty;
                        AddOrUpdateSubtitle(sub);
                        RefreshSubtitleOnUiThread();
                    }
                    else
                    {
                        if (_translateService.TryGetCachedTranslation(sub.OriginalText, sub.DetectedLanguage, targetLangCode, out string cachedTrans))
                        {
                            sub.TranslatedText = cachedTrans;
                        }
                        else
                        {
                            sub.TranslatedText = string.Empty;
                        }

                        AddOrUpdateSubtitle(sub);
                        RefreshSubtitleOnUiThread();

                        if (string.IsNullOrWhiteSpace(sub.TranslatedText))
                        {
                            pendingTranslations.Add(TranslateSubtitleInBackgroundAsync(sub, targetLangCode));
                        }
                    }
                }

                pendingTranslations.RemoveAll(t => t.IsCompleted);

                int totalCached;
                lock (_subtitleLock)
                {
                    totalCached = _subtitleCache.Count;
                }

                ((IProgress<string>)statusReporter).Report(
                    $"✅ 已超前缓冲至 {chunk.EndTime:mm\\:ss} ({_asrService.EffectiveLanguage.ToUpperInvariant()}) | 字幕池: {totalCached} 条");

                RefreshSubtitleOnUiThread();
            }

            if (pendingTranslations.Count > 0)
            {
                await Task.WhenAll(pendingTranslations).ConfigureAwait(false);
            }

            ((IProgress<string>)statusReporter).Report("🏁 全片音轨超前识别与翻译完毕");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户 Seek、切换语言或切换视频导致正常取消
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LookAheadPipeline] 异常: {ex}");
            Console.WriteLine($"[LookAheadPipeline] 异常: {ex.Message}");
            ((IProgress<string>)statusReporter).Report($"⚠️ 识别翻译管线异常: {ex.Message}");
        }
    }

    private Task TranslateSubtitleInBackgroundAsync(SubtitleModel targetSub, string targetLangCode)
    {
        return Task.Run(async () =>
        {
            using var mtTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _mtConcurrencyLimiter.WaitAsync(mtTimeoutCts.Token).ConfigureAwait(false);
            try
            {
                string translated = await _translateService.TranslateAsync(
                    targetSub.OriginalText,
                    targetSub.DetectedLanguage,
                    targetLangCode,
                    mtTimeoutCts.Token,
                    targetSub.EnglishPivotText).ConfigureAwait(false);

                string finalTranslated = string.IsNullOrWhiteSpace(translated)
                    ? targetSub.OriginalText
                    : translated;

                lock (_subtitleLock)
                {
                    targetSub.TranslatedText = finalTranslated;
                }

                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (_activeSubtitle == targetSub ||
                        string.Equals(CurrentOriginalSubtitle, targetSub.OriginalText, StringComparison.Ordinal))
                    {
                        CurrentTranslatedSubtitle = finalTranslated;
                    }

                    var checkTime = IsDraggingSlider
                        ? TimeSpan.FromSeconds(CurrentTimeSeconds)
                        : _mediaService.CurrentPlaybackTime;
                    UpdateActiveSubtitle(checkTime);
                });
            }
            catch
            {
                // 若超时则兜底写入原文，避免界面永远停留在“正在翻译...”
                lock (_subtitleLock)
                {
                    if (string.IsNullOrWhiteSpace(targetSub.TranslatedText))
                    {
                        targetSub.TranslatedText = targetSub.OriginalText;
                    }
                }
                RefreshSubtitleOnUiThread();
            }
            finally
            {
                _mtConcurrencyLimiter.Release();
            }
        });
    }

    private void RefreshSubtitleOnUiThread()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var checkTime = IsDraggingSlider
                ? TimeSpan.FromSeconds(CurrentTimeSeconds)
                : _mediaService.CurrentPlaybackTime;
            UpdateActiveSubtitle(checkTime);
        });
    }

    private void MarkChunkRangeProcessed(TimeSpan start, TimeSpan end)
    {
        if (end <= start)
        {
            return;
        }

        lock (_subtitleLock)
        {
            _processedRanges.Add((start, end));
            _processedRanges.Sort((a, b) => a.Start.CompareTo(b.Start));

            var merged = new List<(TimeSpan Start, TimeSpan End)>(_processedRanges.Count);
            foreach (var range in _processedRanges)
            {
                if (merged.Count == 0)
                {
                    merged.Add(range);
                }
                else
                {
                    var last = merged[^1];
                    if (range.Start <= last.End + TimeSpan.FromSeconds(0.4))
                    {
                        merged[^1] = (last.Start, range.End > last.End ? range.End : last.End);
                    }
                    else
                    {
                        merged.Add(range);
                    }
                }
            }

            _processedRanges.Clear();
            _processedRanges.AddRange(merged);
        }
    }

    private bool IsChunkAlreadyCovered(TimeSpan start, TimeSpan end, out List<SubtitleModel> untranslatedSubtitles)
    {
        untranslatedSubtitles = new List<SubtitleModel>();
        lock (_subtitleLock)
        {
            var shrinkStart = start + TimeSpan.FromSeconds(0.35);
            var shrinkEnd = end - TimeSpan.FromSeconds(0.35);
            if (shrinkEnd < shrinkStart)
            {
                shrinkStart = start;
                shrinkEnd = end;
            }

            bool coveredByRange = _processedRanges.Any(r => r.Start <= shrinkStart && r.End >= shrinkEnd);
            bool coveredBySubtitles = _subtitleCache.Any(s =>
                s.StartTime <= shrinkStart && s.EndTime >= shrinkEnd);

            if (!coveredByRange && !coveredBySubtitles)
            {
                return false;
            }

            foreach (var sub in _subtitleCache)
            {
                if (sub.EndTime > start && sub.StartTime < end && string.IsNullOrWhiteSpace(sub.TranslatedText))
                {
                    untranslatedSubtitles.Add(sub);
                }
            }

            return true;
        }
    }

    public void AddOrUpdateSubtitle(SubtitleModel subtitle)
    {
        lock (_subtitleLock)
        {
            // 如果内存中已有相同位置且已翻译好的字幕，直接复用已有译文，绝不覆盖回空字符串
            var existingTranslated = _subtitleCache.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.TranslatedText) &&
                ((Math.Abs((s.StartTime - subtitle.StartTime).TotalSeconds) < 0.5 &&
                  Math.Abs((s.EndTime - subtitle.EndTime).TotalSeconds) < 0.5) ||
                 (string.Equals(s.OriginalText, subtitle.OriginalText, StringComparison.Ordinal) &&
                  s.EndTime > subtitle.StartTime && s.StartTime < subtitle.EndTime)));

            if (existingTranslated != null && string.IsNullOrWhiteSpace(subtitle.TranslatedText))
            {
                subtitle.TranslatedText = existingTranslated.TranslatedText;
            }

            _subtitleCache.RemoveAll(s =>
                Math.Abs((s.StartTime - subtitle.StartTime).TotalSeconds) < 0.35 &&
                Math.Abs((s.EndTime - subtitle.EndTime).TotalSeconds) < 0.35);

            // 若前一条字幕的结束时间侵入了当前新句子的开始时间，自动截断前一条字幕，防止旧字幕拖尾盖住正在说的新台词
            foreach (var existing in _subtitleCache)
            {
                if (existing.StartTime < subtitle.StartTime && existing.EndTime > subtitle.StartTime)
                {
                    existing.EndTime = subtitle.StartTime;
                }
            }

            _subtitleCache.Add(subtitle);
            _subtitleCache.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        }
    }

    private void OnMediaTimeChanged(TimeSpan currentTime)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (!IsDraggingSlider)
            {
                // 过滤刚完成 Seek 后 LibVLC 异步队列中残留的旧时间点事件，防止进度条瞬间回弹
                if ((DateTime.UtcNow - _lastSeekUtc).TotalMilliseconds < 350 &&
                    Math.Abs(currentTime.TotalSeconds - _lastSeekTargetSeconds) > 1.5)
                {
                    return;
                }

                CurrentTimeSeconds = currentTime.TotalSeconds;
                FormattedCurrentTime = FormatTimeSpan(currentTime);
            }

            if (IsAiSubtitleEnabled)
            {
                UpdateActiveSubtitle(currentTime);
            }
        });
    }

    private void OnMediaLengthChanged(TimeSpan totalLength)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            TotalDurationSeconds = Math.Max(1, totalLength.TotalSeconds);
            FormattedTotalDuration = FormatTimeSpan(totalLength);
        });
    }

    private void OnPlayingStateChanged(bool playing)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsPlaying = playing;
        });
    }

    private void UpdateActiveSubtitle(TimeSpan currentTime)
    {
        if (!IsAiSubtitleEnabled)
        {
            _activeSubtitle = null;
            IsSubtitleVisible = false;
            return;
        }

        SubtitleModel? matched;
        lock (_subtitleLock)
        {
            matched = _subtitleCache.LastOrDefault(s => s.IsActiveAt(currentTime));
        }

        _activeSubtitle = matched;

        if (matched != null)
        {
            // 第一行固定显示识别出的外语原文，第二行固定显示翻译后的目标语言文本
            CurrentOriginalSubtitle = matched.OriginalText;
            if (SelectedTargetLanguage.Code == "none")
            {
                CurrentTranslatedSubtitle = ShowBilingual ? string.Empty : matched.OriginalText;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(matched.TranslatedText) &&
                    _translateService.TryGetCachedTranslation(matched.OriginalText, matched.DetectedLanguage, SelectedTargetLanguage.Code, out string instantCached))
                {
                    matched.TranslatedText = instantCached;
                }

                CurrentTranslatedSubtitle = string.IsNullOrWhiteSpace(matched.TranslatedText)
                    ? "正在翻译..."
                    : matched.TranslatedText;
            }
            IsSubtitleVisible = true;
        }
        else
        {
            IsSubtitleVisible = false;
        }
    }

    private static string FormatTimeSpan(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        int totalHours = (int)time.TotalHours;
        return $"{totalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
    }

    private static string FormatSrtTime(TimeSpan time)
    {
        return time.ToString(@"hh\:mm\:ss\,fff");
    }

    public void Dispose()
    {
        CancelLookAheadPipeline();
        Playlist.Save();
        _mediaService.Dispose();
        _asrService.Dispose();
        _translateService.Dispose();
    }
}
