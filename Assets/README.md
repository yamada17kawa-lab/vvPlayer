# 静态模型资源目录 (Assets)

根据架构设计规范，离线 AI 模型不打包进 `.exe` 主程序，而是存放在本地目录（或 `AppData`）：

- `ggml-base.bin`：Whisper.net 语音识别模型（阶段二引入）
- `opus-mt-en-zh.onnx`：ONNX Runtime 离线机器翻译模型（阶段三引入）
