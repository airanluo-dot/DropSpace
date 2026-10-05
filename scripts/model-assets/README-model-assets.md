# Hy-MT2 Q8_0 Model Assets / 模型资源

Official Tencent Hy-MT2 model byte mirrors. These assets are model resources only; this release contains no DropSpace app build and is not an app update.

腾讯官方 Hy-MT2 模型原始字节镜像。本 Release 仅用于模型资源分发，不包含 DropSpace App 构建。

- `Hy-MT2-1.8B-Q8_0.gguf`: original official GGUF, one file.
- `HY-MT2-7B-Q8_0.gguf.part001` through `.part004`: four consecutive byte ranges of the original official 7B GGUF, split solely for transport. No recompression, requantization, or weight conversion. The first three parts contain 2,000,000,000 bytes each; the final part contains 1,981,928,896 bytes.
- `manifest.json`: pinned upstream sources, full sizes/hashes, ordered part sizes/hashes, and public download URLs.
- `SHA256SUMS.txt`: SHA256 checksums for downloaded release assets.
- `Hy-MT2-1.8B-GGUF-LICENSE.txt` and `Hy-MT2-7B-GGUF-LICENSE.txt`: unmodified upstream Tencent Apache-2.0 licenses. Neither pinned upstream GGUF repository contains a NOTICE file.

Download all four 7B parts into one directory, then concatenate in numeric order. Parts are not independently loadable GGUF models.

Linux/macOS:

```sh
cat HY-MT2-7B-Q8_0.gguf.part001 HY-MT2-7B-Q8_0.gguf.part002 HY-MT2-7B-Q8_0.gguf.part003 HY-MT2-7B-Q8_0.gguf.part004 > HY-MT2-7B-Q8_0.gguf
sha256sum HY-MT2-7B-Q8_0.gguf
```

Windows command prompt:

```bat
copy /b HY-MT2-7B-Q8_0.gguf.part001+HY-MT2-7B-Q8_0.gguf.part002+HY-MT2-7B-Q8_0.gguf.part003+HY-MT2-7B-Q8_0.gguf.part004 HY-MT2-7B-Q8_0.gguf
certutil -hashfile HY-MT2-7B-Q8_0.gguf SHA256
```

Expected reconstructed 7B size: `7981928896` bytes.
Expected reconstructed 7B SHA256: `58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0`.

Expected 1.8B size: `1908528192` bytes.
Expected 1.8B SHA256: `5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4`.

完整来源与每卷校验值见 manifest.json。7B 按 part001、part002、part003、part004 顺序拼接后与官方原始 GGUF 字节一致；各分卷不能单独作为模型加载。本任务仅完成传输及字节校验，未运行模型推理测试。
