# DropSpace 品牌母版规范

## 1. 这套文件是什么

这是一套基于当前「Portal / Space Rift」方案整理出的 DropSpace 品牌母版包。

核心视觉：
- 深色空间/传送门底板
- 中央菱形“被投放对象”
- 紫色空间裂隙/入口
- Mini Mark：下方容器 + 上方菱形

## 2. 主文件优先级

### App 图标
1. `01_MASTER/DropSpace-AppIcon-Master-4096.png`
2. `01_MASTER/DropSpace-AppIcon-Master-2048.png`
3. `00_REFERENCE/DropSpace-AppIcon-Generated-Native-1254x1254.png`

说明：
4096 / 2048 版本是从当前生成源图使用高质量 Lanczos 重采样得到，方便后续统一导出。
它们不会凭空增加原始细节，因此原生 1254×1254 文件也被完整保留。

### Mini Mark
优先使用：
- `01_MASTER/DropSpace-MiniMark-Master.svg`

这是可无限缩放的矢量标记。

### Wordmark
优先使用：
- `01_MASTER/DropSpace-Wordmark-Master.svg`

SVG 内的 DropSpace 字样已转为路径，不要求终端设备安装同一字体。

## 3. App Icon 构图规范

- 画布：正方形
- 主体居中
- 不允许非等比拉伸
- 不要直接裁掉发光边缘
- 不要把 Windows 的最终圆角/蒙版永久焊死到所有 Logo 版本里
- 完整 3D App Icon 作为主要产品图标
- `DropSpace-AppIcon-Flat-Vector.svg` 作为小尺寸或特殊场景的备用矢量简化版本

## 4. 安全区

建议在 2048×2048 画布上：
- 核心识别元素尽量控制在中心约 80% 区域
- 最外侧约 10% 作为安全边距
- Windows 各种自适应裁切、缩略图、商店展示不得裁掉中央菱形和传送门主体

## 5. 推荐颜色

- Deep Navy: `#101321`
- Graphite: `#242632`
- Electric Violet: `#6F5BFF`
- Rift Violet: `#9C83FF`
- Flare: `#F0ECFF`
- Ink: `#151720`
- White: `#FFFFFF`

## 6. 允许版本

- Full 3D App Icon
- Flat Vector App Icon
- Purple Mini Mark
- Black Mini Mark
- White Mini Mark
- DropSpace Wordmark
- Mini Mark + DropSpace 横向组合

## 7. 禁止

- 改变 Mini Mark 的菱形位置
- 将容器横向压扁或纵向拉长
- 随意改变紫色为其他强调色
- 给 Wordmark 加描边、阴影或立体效果
- 把完整品牌字样强塞进 16×16 / 20×20 小图标
- 用 JPEG 作为透明 Logo 主文件
