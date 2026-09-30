# PictureButler

Windows 本地桌面程序：**AI 提示词管理** + **本地照片管理**（人脸 / AI 打标）。

原生 WPF 界面（.NET 10），配浏览器扩展拾取器；识别引擎为进程内 Rust cdylib，无独立服务进程。

当前版本：**0.62.9**

---

## 功能

**提示词管理**
- 列表 / 搜索 / 星标 / 详情 / 复制
- 图片墙视图（设置 → 提示词可切换）
- 浏览器扩展一键把星标提示词写入网页输入框

**图片库**
- 浏览 / 分页 / 搜索 / 筛选（状态、排序、来源、标签）
- 大图查看器：←/→、A/D 翻页，适应窗口、人脸框叠加
- 人物相册 / 标签相册 / 证件照 / 文档 分类浏览
- 人脸识别自动归类人物，AI 打标生成描述与标签

**系统**
- 本地服务 `127.0.0.1:8189`（扩展连接 + 人脸缩略图）
- 托盘常驻、全局热键 `Ctrl + Alt + Space`
- 深色 / 浅色主题（设置 → 外观，切换后重启生效）

---

## 目录结构

```
PictureButler/
├── src-native/          C# WPF 主程序（net10.0-windows）
├── imgtag-rs/           Rust 识别引擎（cdylib imgtag_native + 可选 imgtag.exe）
├── extension/           Chrome 扩展「PictureButler 拾取器」
├── docs/                设计文档与归档
├── PictureButler界面设计方案-v2.md   界面方案 v2 规范
└── scripts/             构建与部署脚本
```

---

## 环境要求

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download)（编译主程序）
- [Rust](https://rustup.rs/)（编译 `imgtag_native.dll`，仅改识别引擎时需要）

---

## 构建

### 主程序（WPF）

```powershell
# 环境变量（本机构建时若 NuGet 还原报 path1 为空，先补这三行）
$env:ProgramFiles = "C:\Program Files"
${env:ProgramFiles(x86)} = "C:\Program Files (x86)"
$env:ProgramW6432 = "C:\Program Files"

cd src-native
dotnet clean -c Release
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  --output bin/publish-out
```

产物：`src-native/bin/publish-out/PictureButler.exe`（单文件自包含，约 81 MB）。

### 识别引擎（Rust，可选）

```powershell
cd imgtag-rs
cargo build --release
# 产物：target/release/imgtag_native.dll → 拷到 imgtag\ 目录
```

### 一键部署

```powershell
.\scripts\deploy.ps1
```

脚本会：校验四处版本号一致 → 彻底 clean → publish → 备份旧 exe → 覆盖 `D:\Tool\PictureButler\PictureButler.exe` → 验证 `/api/health`。

---

## 部署与数据

| 路径 | 内容 |
|---|---|
| `D:\Tool\PictureButler\PictureButler.exe` | 交付物（单文件免安装） |
| `D:\Tool\PictureButler\imgtag\` | 识别数据：`db.sqlite`、`models\`、`onnxruntime.dll`、`imgtag_native.dll` |
| `D:\Tool\PictureButler\extension\` | 浏览器扩展 |
| `%APPDATA%\com.picturebutler.app\` | `prompts.db`、`image_index.db`、设置、日志 |

**版本号约定**：每次更新须同步四处——`PictureButler.csproj`、`LocalHttpServer.cs`（注释 + 真值）、`src-native/原生版说明.txt`、交付目录 `PictureButler-说明.txt`。发布后用 `http://127.0.0.1:8189/api/health` 复核。

---

## 浏览器扩展安装

1. 先运行 PictureButler
2. Chrome 打开 `chrome://extensions/` → 开发者模式 → 「加载已解压的扩展程序」
3. 选择 `extension` 文件夹
4. 点扩展图标 → 点击星标提示词 → 自动写入当前网页输入框

---

## 架构要点

- **imgtag 彻底融合**：识别以 `imgtag_native.dll`（Rust cdylib）在主进程内 P/Invoke 调用，无 8520 端口、无独立进程。
- **三层主题令牌**：`Themes/Primitive.xaml`（原语）→ `Semantic.Dark/Light.xaml`（语义）→ 业务 XAML 只引用语义键。换主题替换 `MergedDictionaries[1]`，须重启生效。
- **本地 HTTP**：`LocalHttpServer` 手写 TcpListener，供扩展取星标提示词、供程序取人脸缩略图。

---

## 开发说明

- 界面规范：`PictureButler界面设计方案-v2.md`
- 改 XAML 先 `dotnet clean`，避免陈旧 BAML
- 交互类改动需真机验收（本环境无法驱动 WPF 鼠标）
- 提交前确认 `git diff` 只包含预期改动；构建产物与数据库已被 `.gitignore` 排除

## 许可

私有项目，未公开授权。
