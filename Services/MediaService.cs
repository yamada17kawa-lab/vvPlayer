using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using NAudio.Wave;

namespace MyMediaPlayer.Services;

/// <summary>
/// 16kHz 单声道音频切片数据结构（包含该切片在原视频中的精确起止时间）
/// </summary>
public record AudioChunk(TimeSpan StartTime, TimeSpan EndTime, float[] Samples16k);

/// <summary>
/// 封装 LibVLC 与 NAudio：
/// 1. LibVLC 负责高品质视频画面与原声播放
/// 2. NAudio 负责方案 B（后台异步超前提取音轨 + 16kHz 重采样 + VAD 静音停顿切分）
/// </summary>
public class MediaService : IDisposable
{
    public const int WhisperSampleRate = 16000;

    private readonly LibVLC _libVlc;
    private Media? _currentMedia;
    private long _lastReportedTimeMs;
    private long _lastReportedLengthMs;
    private long _lastSeekTimestampTicks;
    private long _lastSeekTargetMs;

    public MediaPlayer Player { get; }
    public string? CurrentFilePath { get; private set; }
    public TimeSpan CurrentPlaybackTime => TimeSpan.FromMilliseconds(Interlocked.Read(ref _lastReportedTimeMs));

    public event Action<TimeSpan>? TimeChanged;
    public event Action<TimeSpan>? LengthChanged;
    public event Action<bool>? PlayingStateChanged;
    public event Action<string>? MediaLoaded;
    public event Action<TimeSpan>? UserSeeked;

    public MediaService()
    {
        Core.Initialize();
        _libVlc = new LibVLC("--no-osd");
        Player = new MediaPlayer(_libVlc);

        Player.TimeChanged += (_, e) =>
        {
            if (e.Time >= 0)
            {
                long seekTicks = Interlocked.Read(ref _lastSeekTimestampTicks);
                long seekTargetMs = Interlocked.Read(ref _lastSeekTargetMs);
                double msSinceSeek = (DateTime.UtcNow.Ticks - seekTicks) / (double)TimeSpan.TicksPerMillisecond;
                if (msSinceSeek < 450 && Math.Abs(e.Time - seekTargetMs) > 1500)
                {
                    return;
                }

                Interlocked.Exchange(ref _lastReportedTimeMs, e.Time);
                TimeChanged?.Invoke(TimeSpan.FromMilliseconds(e.Time));
            }

            long playerLength = Player.Length;
            if (playerLength > 0)
            {
                ReportLengthIfNeeded(playerLength);
            }
        };

        Player.LengthChanged += (_, e) =>
        {
            if (e.Length > 0)
            {
                ReportLengthIfNeeded(e.Length);
            }
        };

        Player.Playing += (_, _) =>
        {
            PlayingStateChanged?.Invoke(true);
            long playerLength = Player.Length;
            if (playerLength > 0)
            {
                ReportLengthIfNeeded(playerLength);
            }
        };
        Player.Paused += (_, _) => PlayingStateChanged?.Invoke(false);
        Player.Stopped += (_, _) => PlayingStateChanged?.Invoke(false);
        Player.EndReached += (_, _) => PlayingStateChanged?.Invoke(false);
    }

    private void ReportLengthIfNeeded(long lengthMs)
    {
        if (lengthMs <= 0)
        {
            return;
        }

        long previous = Interlocked.Exchange(ref _lastReportedLengthMs, lengthMs);
        if (previous != lengthMs)
        {
            LengthChanged?.Invoke(TimeSpan.FromMilliseconds(lengthMs));
        }
    }

    public void LoadAndPlay(string filePath)
    {
        CurrentFilePath = filePath;
        Interlocked.Exchange(ref _lastReportedTimeMs, 0);
        Interlocked.Exchange(ref _lastReportedLengthMs, 0);

        _currentMedia?.Dispose();
        _currentMedia = new Media(_libVlc, filePath, FromType.FromPath);
        _currentMedia.DurationChanged += (_, e) =>
        {
            if (e.Duration > 0)
            {
                ReportLengthIfNeeded(e.Duration);
            }
        };

        // 后台快速读取容器时长作为即时兜底，确保导入视频瞬间即可显示总时长
        _ = Task.Run(() =>
        {
            try
            {
                using var reader = new MediaFoundationReader(filePath);
                long durationMs = (long)reader.TotalTime.TotalMilliseconds;
                if (durationMs > 0 && Interlocked.Read(ref _lastReportedLengthMs) <= 0)
                {
                    ReportLengthIfNeeded(durationMs);
                }
            }
            catch
            {
                // 忽略个别特殊容器 MediaFoundation 不支持的情况，交由 LibVLC 事件报告
            }
        });

        Player.Play(_currentMedia);
        MediaLoaded?.Invoke(filePath);
    }

    public void TogglePlayPause()
    {
        if (Player.IsPlaying)
        {
            Player.Pause();
        }
        else if (_currentMedia != null)
        {
            if (Player.State == VLCState.Ended || Player.State == VLCState.Stopped)
            {
                Interlocked.Exchange(ref _lastReportedTimeMs, 0);
                Player.Stop();
            }
            Player.Play();
        }
    }

    public void Pause()
    {
        if (Player.IsPlaying)
        {
            Player.Pause();
        }
    }

    public void Stop()
    {
        Player.Stop();
        Interlocked.Exchange(ref _lastReportedTimeMs, 0);
    }

    public void SeekTo(TimeSpan targetTime)
    {
        var clampedMs = (long)Math.Max(0, targetTime.TotalMilliseconds);
        long totalLengthMs = Interlocked.Read(ref _lastReportedLengthMs);
        if (totalLengthMs > 0)
        {
            clampedMs = Math.Min(clampedMs, totalLengthMs);
        }

        Interlocked.Exchange(ref _lastSeekTargetMs, clampedMs);
        Interlocked.Exchange(ref _lastSeekTimestampTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _lastReportedTimeMs, clampedMs);

        if (_currentMedia != null && (Player.State == VLCState.Ended || Player.State == VLCState.Stopped))
        {
            Player.Play(_currentMedia);
        }

        if (Player.IsSeekable)
        {
            Player.Time = clampedMs;
        }
        else if (totalLengthMs > 0)
        {
            Player.Position = (float)Math.Clamp((double)clampedMs / totalLengthMs, 0.0, 1.0);
        }
        else
        {
            Player.Time = clampedMs;
        }

        UserSeeked?.Invoke(TimeSpan.FromMilliseconds(clampedMs));
    }

    public void SetVolume(int volume)
    {
        Player.Volume = Math.Clamp(volume, 0, 100);
    }

    /// <summary>
    /// 方案 B：从指定时间点开始，后台异步超前提取音频、重采样为 16kHz 单声道，并结合 VAD 静音检测切分出语音片段
    /// </summary>
    /// <param name="filePath">视频文件路径</param>
    /// <param name="startPosition">起始提取位置（例如刚打开视频为 0，或用户拖拽进度条后的新位置）</param>
    /// <param name="lookAheadWindowSeconds">最大超前缓冲窗口（默认超前当前播放进度 60 秒，避免无限制占用 CPU）</param>
    /// <param name="cancellationToken">取消令牌（当用户拖拽进度条或切换视频时立即中断旧管线）</param>
    public async IAsyncEnumerable<AudioChunk> ExtractAudioChunksLookAheadAsync(
        string filePath,
        TimeSpan startPosition,
        double lookAheadWindowSeconds = 60.0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 将文件 I/O 与重采样完全放到后台线程
        await Task.Yield();

        using var reader = new MediaFoundationReader(filePath);
        if (startPosition > TimeSpan.Zero && startPosition < reader.TotalTime)
        {
            reader.CurrentTime = startPosition;
        }

        var targetFormat = new WaveFormat(WhisperSampleRate, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, targetFormat)
        {
            ResamplerQuality = 60
        };

        // 每次读取 50ms 帧 (16000 Hz * 0.05s = 800 个采样点 = 1600 字节)
        const int frameSamples = 800;
        const int frameBytes = frameSamples * 2;
        byte[] byteBuffer = new byte[frameBytes];

        // 自适应双模式 VAD 切片参数（同时兼容安静人声与强背景音乐/街头嘈杂视频）：
        // - 预卷缓冲：6 帧 (300ms)，保留开口说话前的起音
        // - 最小语音切片长度：1.6 秒
        // - 软切分长度：2.6 秒（出现相对能量回落即切分）
        // - 最大语音切片长度：3.8 秒（避免长切片混入过多前奏 BGM 导致 Whisper 漏识短句，同时保证低延迟）
        const double minChunkSeconds = 1.6;
        const double softMaxChunkSeconds = 2.6;
        const double maxChunkSeconds = 3.8;
        const int preRollFrameCapacity = 6;    // 6 * 50ms = 300ms 句首预留
        const int silenceFramesToCut = 6;      // 6 * 50ms = 300ms 停顿断句
        const int softSilenceFramesToCut = 4;  // 4 * 50ms = 200ms 软断句

        var accumulatedSamples = new List<float>(WhisperSampleRate * 5);
        var preRollFrames = new Queue<float[]>(preRollFrameCapacity + 1);
        TimeSpan cursorTime = startPosition;
        TimeSpan chunkStartTime = startPosition;
        bool inSpeech = false;
        int consecutiveSilenceFrames = 0;
        float rollingBgRms = 0.012f;

        while (!cancellationToken.IsCancellationRequested)
        {
            // 超前限流控制：如果提取游标已经超前当前播放进度超过 lookAheadWindowSeconds，则稍作等待
            var currentPlayTime = CurrentPlaybackTime;
            if (cursorTime - currentPlayTime > TimeSpan.FromSeconds(lookAheadWindowSeconds))
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                continue;
            }

            int bytesRead = resampler.Read(byteBuffer.AsSpan(0, frameBytes));
            if (bytesRead <= 0)
            {
                // 已到达视频末尾，把剩余的语音片段发出
                if (inSpeech && accumulatedSamples.Count > WhisperSampleRate / 3)
                {
                    yield return new AudioChunk(chunkStartTime, cursorTime, accumulatedSamples.ToArray());
                }
                yield break;
            }

            int sampleCount = bytesRead / 2;
            float sumSquares = 0f;
            float[] frameFloats = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                short pcm16 = BitConverter.ToInt16(byteBuffer, i * 2);
                float normalized = pcm16 / 32768f;
                frameFloats[i] = normalized;
                sumSquares += normalized * normalized;
            }

            float rms = MathF.Sqrt(sumSquares / Math.Max(1, sampleCount));
            TimeSpan frameDuration = TimeSpan.FromSeconds((double)sampleCount / WhisperSampleRate);

            // 动态跟踪背景均值能量（支持强 BGM 视频的相对能量谷值断句）
            if (rms < rollingBgRms)
            {
                rollingBgRms = rollingBgRms * 0.90f + rms * 0.10f;
            }
            else
            {
                rollingBgRms = rollingBgRms * 0.97f + rms * 0.03f;
            }
            rollingBgRms = Math.Clamp(rollingBgRms, 0.004f, 0.35f);

            if (!inSpeech)
            {
                float onsetThreshold = Math.Max(0.008f, Math.Min(0.025f, rollingBgRms * 1.15f));
                bool isVoiceOnset = rms >= onsetThreshold;

                if (isVoiceOnset)
                {
                    inSpeech = true;
                    int preRollCount = preRollFrames.Count;
                    chunkStartTime = cursorTime - TimeSpan.FromSeconds(preRollCount * 0.05);
                    if (chunkStartTime < startPosition)
                    {
                        chunkStartTime = startPosition;
                    }

                    consecutiveSilenceFrames = 0;
                    accumulatedSamples.Clear();
                    while (preRollFrames.Count > 0)
                    {
                        accumulatedSamples.AddRange(preRollFrames.Dequeue());
                    }
                    accumulatedSamples.AddRange(frameFloats);
                }
                else
                {
                    if (preRollFrames.Count >= preRollFrameCapacity)
                    {
                        preRollFrames.Dequeue();
                    }
                    preRollFrames.Enqueue(frameFloats);
                }
            }
            else
            {
                accumulatedSamples.AddRange(frameFloats);

                // 当能量绝对值低于 0.014 或回落到背景均值的 80% 以下时视为换气/句间低谷
                float holdThreshold = Math.Max(0.007f, rollingBgRms * 0.80f);
                bool isVoiceActive = rms >= holdThreshold;

                if (isVoiceActive)
                {
                    consecutiveSilenceFrames = 0;
                }
                else
                {
                    consecutiveSilenceFrames++;
                }

                double currentChunkDuration = (double)accumulatedSamples.Count / WhisperSampleRate;

                bool shouldCutByPause = currentChunkDuration >= minChunkSeconds && consecutiveSilenceFrames >= silenceFramesToCut;
                bool shouldCutBySoftPause = currentChunkDuration >= softMaxChunkSeconds && consecutiveSilenceFrames >= softSilenceFramesToCut;
                bool shouldCutByMaxLength = currentChunkDuration >= maxChunkSeconds;

                if (shouldCutByPause || shouldCutBySoftPause || shouldCutByMaxLength)
                {
                    double trailingSilenceSec = consecutiveSilenceFrames * 0.05;
                    TimeSpan chunkEndTime = (cursorTime + frameDuration) - TimeSpan.FromSeconds(Math.Min(trailingSilenceSec * 0.5, 0.20));
                    if (chunkEndTime <= chunkStartTime + TimeSpan.FromSeconds(0.6))
                    {
                        chunkEndTime = cursorTime + frameDuration;
                    }

                    var chunkSamples = accumulatedSamples.ToArray();

                    preRollFrames.Clear();
                    inSpeech = false;
                    consecutiveSilenceFrames = 0;
                    accumulatedSamples.Clear();

                    yield return new AudioChunk(chunkStartTime, chunkEndTime, chunkSamples);
                }
            }

            cursorTime += frameDuration;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Player.IsPlaying)
            {
                Player.Stop();
            }
        }
        catch
        {
        }

        try
        {
            _currentMedia?.Dispose();
        }
        catch
        {
        }

        try
        {
            Player.Dispose();
        }
        catch
        {
        }

        try
        {
            _libVlc.Dispose();
        }
        catch
        {
        }
    }
}
