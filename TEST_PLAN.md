# DropSpace 核心测试与规则

## 日常开发怎么选

普通改动默认运行 0 项功能测试。修一个已复现的问题，先选一个能区分修复前后的场景。手动入口按平台选择；一次检查已回答问题，就结束验证。[开发规则](docs/dev/passive-test-budget.md)

## 维护的 4 个场景

| 平台 | 场景 | 实际保护的行为 |
| --- | --- | --- |
| Windows | 文件路径试图跳出应用存储目录 | 拒绝越界访问 |
| Windows | 解析旧通道名 Preview 并写回 | 读为 Beta，写回 Beta |
| 官网 | 打开首页的 Windows 下载入口 | 按钮指向稳定版安装包 |
| 分享服务 | 对已有分享发起未授权写入 | 请求被拒绝 |

Windows 可在 [Core 测试项目](tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj)按方法名运行其中一项。官网和分享服务各自的 `npm test` 只运行表中的一项。GitHub Actions 提供 Windows、官网和分享服务三个手动入口。

## 当前产品和交付规则到哪里查

| 范围 | 生效规则和归属 |
| --- | --- |
| 功能与交互 | [产品](PRODUCT.md)、[功能](FEATURES.md)、[交互](UX.md)和[设计系统](DESIGN_SYSTEM.md)定义临时空间、剪贴板、音乐与灵动岛等行为。 |
| 歌词与媒体 | [整首翻译判定](.github/release-notes/v0.3.1-beta.16.md)决定何时调用 AI；[歌曲匹配](docs/dev/1.1-beta3-lyrics-matching.md)约束曲名、完整歌手署名与录音版本。全屏显示许可和触发显示的内容分别判断。 |
| 存储、隐私和权限 | [数据模型](DATA_MODEL.md)、[隐私说明](PRIVACY.md)、[架构](ARCHITECTURE.md)规定应用自有文件、外部引用、共享授权与数据迁移。 |
| 模块与平台 | [可选模块](docs/dev/feature-modules.md)规定能力、安装和数据保留；[macOS 架构](macos/docs/ARCHITECTURE.md)记录平台实现。 |
| 界面语言及更新说明 | [本地化](docs/dev/interface-localization.md)规定十种界面语言和中文／英文摘要。[版本比较](AGENTS.md#release-comparison-rule)按同平台的上一 Beta 或上一稳定版填写更新。 |
| 构建与发布 | [维护指南](.agents/skills/dropspace-maintainer/SKILL.md)规定实际产物的版本、来源、签名或哈希检查；[贡献指南](CONTRIBUTING.md)规定依赖与许可。 |
| 开发与验证 | [最小测试与开发规则](docs/dev/passive-test-budget.md)统一定义测试选择、边界校验、重试与结果报告。 |

## 覆盖边界

数据库升级、安装与回滚、跨设备传输、歌词同步与翻译、macOS 文件撤销、DPI 和无障碍交互失去原有的广泛自动回归覆盖。修改这些功能时，应根据该次改动选择一个真实入口复现。版本、签名、哈希和资源校验只验证实际交付物。

这 4 项是仓库维护的自动化功能用例。应用自带的手动诊断命令和交付物完整性校验单独保留。

本次重整前的测试和规则可在 Git 修订 `b75fcf0d` 查阅。历史测试报告记录当时执行的结果。
