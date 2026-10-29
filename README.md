# 🎬 vvPlayer (vv播放器)

<p align="center">
  <img src="Assets/cover.jpg" alt="vvPlayer Cover" width="600" style="border-radius: 8px; box-shadow: 0 4px 12px rgba(0,0,0,0.3);" />
</p>

<p align="center">
  <b>一款集成了离线 AI 语音识别与实时翻译引擎的现代化 Windows 桌面媒体播放器</b><br>
  <sub>基于 .NET 10 (WPF) + LibVLCSharp 构建，支持实时双语字幕、画中画置顶小窗口、60秒超前滑动窗口与多级分类播放目录。</sub>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0_Windows-512BD4?logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/WPF-Windows_Desktop-0078D7?logo=windows&logoColor=white" alt="WPF" />
  <img src="https://img.shields.io/badge/Engine-LibVLC_3.0-FF8800?logo=vlcmediaplayer&logoColor=white" alt="LibVLC" />
  <img src="https://img.shields.io/badge/AI_ASR-Whisper.net-green?logo=openai&logoColor=white" alt="Whisper.net" />
  <img src="https://img.shields.io/badge/AI_MT-ONNX_Runtime-blue?logo=onnx&logoColor=white" alt="ONNX Runtime" />
  <img src="https://img.shields.io/badge/Architecture-MVVM-lightgrey" alt="MVVM" />
</p>

---

## ✨ 核心特性

### 1. 🤖 实时 AI 语音识别与双语翻译引擎
- **本地离线语音识别 (ASR)**：内置 [Whisper.net](https://github.com/sandrohanea/whisper.net) 引擎，无需联网即可直接从本地音视频中精准分离音频流并识别对话。
- **混合机器翻译架构 (MT)**：
  - **离线模式**：基于 ONNX Runtime 与 SentencePiece 动态规划分词自回归解码，保障私密性。
  - **多源智能故障转移**：针对多语言互译提供多通道智能 fallback 机制与内存级结果缓存，毫秒级响应。
- **60 秒超前滑动预处理窗口**：后台异步超前处理未来 60 秒音频内容；当用户拖拽进度条跳转（Seek）时，已翻译区间秒级复用（`< 0.3ms` 内存命中），彻底告别卡顿与等待。
- **双语同屏显示与 SRT 导出**：顶部浅白原文 + 底部醒目亮黄译文（带高可读性文字阴影滤镜）；支持将全片已识别翻译的时间戳字幕一键导出为标准 `.srt` 文件。

### 2. 📺 画中画置顶小窗口 (Mini-Window / PiP)
- **无感平滑切换**：采用同进程同渲染纹理状态形变技术，切换至小窗口时零黑屏、零掉帧、不中断音频及后台 AI 推理。
- **智能交互浮层**：小窗口模式下自动开启屏幕置顶（`Topmost = true`）；鼠标悬浮时呈现紧凑控制条（进度控制、快进快退、播放/暂停、恢复大屏），静止 2.5 秒后自动淡出。
- **自由拖拽与双击恢复**：单击按住画面即可在多显示器之间随心拖拽吸附；双击画面或按 `Esc` 键瞬间还原标准大屏。

### 3. 📋 多级分类播放目录
- **虚拟树形分类管理**：支持自定义创建多级文件夹对视频进行分类整理。
- **轻量无冗余**：仅记录原文件物理路径，不复制视频文件，零额外磁盘占用。
- **拖拽式交互**：支持从 Windows 资源管理器直接拖入单个视频或文件夹批量添加，并支持目录树内自由拖拽归类重排。
- **失效智能检测**：自动标记本地已移动或删除的文件，历史记录持久化至用户配置目录。

### 4. ⚡ 强劲的现代播放核心
- 基于 **VideoLAN LibVLCSharp** 原生硬件解码渲染管线，支持几乎所有常见的音视频封装格式（MP4, MKV, AVI, MOV, FLV, WMV, WebM, MP3, WAV, FLAC 等）。
- 优化的进程生命周期管理，告别传统播放器在多媒体或 AI 线程退出时的窗口未响应假死问题。

---

## ⌨️ 常用快捷键

| 按键 / 组合键 | 功能说明 | 适用场景 |
| :--- | :--- | :--- |
| **`Space` (空格)** | 播放 / 暂停 | 标准 / 全屏 / 小窗口 |
| **`Ctrl + →`** 或 **`L`** | **快进 10 秒** | 标准 / 全屏 / 小窗口 |
| **`Ctrl + ←`** 或 **`J`** | **快退 10 秒** | 标准 / 全屏 / 小窗口 |
| **`→`** / **`←`** | 快进 5 秒 / 快退 5 秒 | 标准 / 全屏 / 小窗口 |
| **`F11`** | 进入 / 退出全屏模式 | 全局 |
| **`Esc`** | 退出全屏模式 / 退出小窗口模式 | 全屏 / 小窗口 |
| **鼠标双击画面** | 切换全屏 (标准模式) / 恢复大屏 (小窗口模式) | 画面区域 |
| **鼠标左键按住画面** | 随意拖拽移动小窗口 | 小窗口模式 |

---

## 🛠️ 技术栈与依赖架构

| 模块 | 技术选型 | 说明 |
| :--- | :--- | :--- |
| **开发框架** | .NET 10.0 (Windows Desktop) / C# 13 | 现代化高性能 .NET 运行时 |
| **UI 界面层** | WPF (Windows Presentation Foundation) | 现代化暗色主题与自定义响应式控件 |
| **架构模式** | MVVM (CommunityToolkit.Mvvm 8.4) | 高内聚弱耦合的响应式属性与命令绑定 |
| **视频播放引擎** | LibVLCSharp 3.10.2 + VideoLAN.LibVLC.Windows 3.0.24 | 工业级硬解播放核 |
| **音频提取处理** | NAudio 3.3.0 + MediaFoundationReader | 流式音频切片重采样与格式转换 |
| **本地语音识别** | Whisper.net 1.9.1 + Whisper.net.Runtime | OpenAI Whisper GGML 本地 C++ 推理封装 |
| **本地机器翻译** | Microsoft.ML.OnnxRuntime 1.31.0 | 离线 Seq2Seq 自回归解码引擎 |

---

## 📁 目录结构说明

```text
vvPlayer/
├── Assets/                 # 应用图标 (app.ico)、封面图 (cover.jpg)
├── Models/                 # 数据契约（PlaylistNode 节点树、SubtitleModel 字幕模型等）
├── Services/               # 业务支撑服务
│   ├── AsrService.cs       # 离线语音识别（Whisper 管道封装）
│   ├── MediaService.cs     # 播放控制与实时音频切片采样
│   ├── PlaylistService.cs  # 播放目录 JSON 序列化持久化服务
│   └── TranslateService.cs # 离线 ONNX 与多引擎智能翻译服务
├── ViewModels/             # MVVM 视图模型
│   ├── MainViewModel.cs    # 播放控制、AI 流水线调度、超前滑动窗口管理
│   └── PlaylistViewModel.cs# 目录增删改查、树拖拽节点管理
├── Views/                  # WPF 界面与视图层
│   ├── InputDialog.cs      # 通用输入弹窗
│   ├── MainWindow.xaml     # 主界面结构 (TopBar、VideoView、Subtitle、MiniOverlay)
│   └── MainWindow.xaml.cs  # 窗口全屏、小窗口状态切换与快捷键拦截
├── .gitignore              # Git 忽略规则配置 (已预设过滤 bin/obj/大型视频/模型)
├── nuget.config            # NuGet 依赖源配置
├── vvPlayer.csproj         # 项目工程配置
└── vvPlayer.slnx           # 解决方案入口
```

---

## 🚀 编译与本地运行

### 前置要求
1. **操作系统**：Windows 10 / 11 64 位
2. **SDK 环境**：安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（或更高版本）
3. **开发工具**：Visual Studio 2022+ / JetBrains Rider / VS Code（带 C# Dev Kit）

### 源码编译
```bash
# 1. 克隆或下载本仓库
git clone https://github.com/YOUR_USERNAME/vvPlayer.git
cd vvPlayer

# 2. 还原 NuGet 包并编译 Debug 版本
dotnet build vvPlayer.csproj

# 3. 运行应用程序
dotnet run --project vvPlayer.csproj
```

> **提示**：首次编译完成后，可直接在 `bin\Debug\net10.0-windows\` 目录下双击运行 `vvPlayer.exe`。

---

## 🤖 离线 AI 模型配置 (可选)

播放器具备智能按需载入能力：
1. **语音识别模型**：默认支持 Whisper `ggml-base.bin`，首次启用时若检测到本地缺失将自动提供指引；用户亦可通过播放器底部「📁 模型目录」自由指定外部已下载的模型目录。
2. **翻译模型**：包含离线 ONNX 与快速 API 两种模式，可根据机器配置随时在设置中按需切换。

---

## 📄 开源许可

本项目基于 [MIT License](LICENSE) 开源协议，欢迎提交 Issue 与 Pull Request 共同改进！
