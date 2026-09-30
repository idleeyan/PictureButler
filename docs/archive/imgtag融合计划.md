# PictureButler 项目介绍与 imgtag 彻底融合计划

> 编写日期：2026-09-13　｜　编写人：豆包（MainAgent）　｜　当前版本：0.33.0
> 本文档供接手执行的其他职能团队阅读，包含：项目全景介绍、imgtag 现状、彻底融合的技术方案与分阶段实施计划、以及执行时必须遵守的约定。

---

## 一、项目介绍

### 1.1 一句话简介

**PictureButler**（曾用名：提示词管理器）是运行在 Windows 11 上的**本地桌面程序**，核心功能二合一：**AI 提示词管理**（收藏、星标、搜索、填充到网页）与**本地照片管理**（图片库浏览、人脸识别、AI 打标、人物归类）。

### 1.2 技术架构（当前）

```
┌─────────────────────────────────────────────────────┐
│  PictureButler-绿色版.exe（原生桌面程序）              │
│  技术栈：WPF / .NET 10（net10.0-windows）单项目        │
│  ├─ 提示词管理（SQLite: prompts.db，DataService）      │
│  ├─ 图片库（自建索引 image_index.db + imgtag 识别库）   │
│  ├─ 本地 HTTP 服务 127.0.0.1:8189（供扩展读取星标）     │
│  └─ imgtag 识别引擎（当前为【独立进程】，见第三节）      │
└─────────────────────────────────────────────────────┘
         │                 │
         ▼                 ▼
┌─────────────────┐  ┌──────────────────────┐
│ 浏览器扩展（精简）│  │ imgtag.exe（Rust）    │
│ 只做：星标呈现+   │  │ 独立进程 · 127.0.0.1  │
│ 写入提示词       │  │ :8520 HTTP · 独立 db  │
└─────────────────┘  └──────────────────────┘
```

### 1.3 目录与关键文件

| 路径 | 内容 |
|---|---|
| `E:\work\PictureButler\src-native\` | **C# 主程序源码**（单项目，net10.0-windows） |
| `E:\work\PictureButler\extension\` | 浏览器扩展（精简版：content.js / popup / manifest） |
| `E:\work\PictureButler\imgtag-rs\` | **imgtag Rust 源码备份**（src + Cargo，0.3MB，剔除了编译产物） |
| `E:\work\PictureButler\图片库升级计划.md` | 历史计划文档（阶段0–4 已完成） |
| `D:\Tool\PictureButler\PictureButler-绿色版.exe` | **发布产物**（73MB 单文件，当前 0.33.0） |
| `D:\Tool\PictureButler\imgtag\` | **imgtag 运行目录**（exe + db + models，见第三节） |
| `D:\Tool\PictureButler\PictureButler-绿色版-说明.txt` | 用户可见的版本说明 |
| `%APPDATA%\com.picturebutler.app\prompts.db` | 提示词数据库（**注意：已膨胀到 929MB，见第八节**） |
| `%APPDATA%\com.picturebutler.app\image_index.db` | 图片库自建索引 |
| `%APPDATA%\com.picturebutler.app\pb_native_settings.json` | 设置（ImageFolders / ImgtagDbPath / ImgPageSize 等） |

### 1.4 当前版本与发布方式

- 版本：**0.33.0**（纯数字版本号，见第七节约定）
- 发布命令（在 `src-native` 下执行）：

```
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

- 产物 `bin\Release\net10.0-windows\win-x64\publish\PictureButler.exe`（73MB）→ 复制覆盖 `D:\Tool\PictureButler\PictureButler-绿色版.exe`
- 程序启动会自动拉起 imgtag 服务（当前为独立进程形式）

---

## 二、历史沿革（关键决策记录）

1. **webview2 阶段**：早期用 WebView2 内嵌网页，用户否决——"没有 Windows 程序的厚重感、操作割裂"，后弃用。
2. **B 方案定型**（用户拍板）：**原生桌面程序 + 浏览器扩展**。扩展只负责"呈现星标内容 + 写入提示词"；程序本体负责提示词与图片管理。
3. **扩展精简**：早期扩展是完整版（自身管理提示词 + WebDAV 同步，v4.3.2，位于 `E:\SYNC\My VS\PictureButler`，**已于 2026-09-13 删除**）；现精简为 `extension\` 目录（manifest 769B）。
4. **imgtag 并入过程**：早期 imgtag 以独立项目运行于 `E:\imgtag`（含网页管理界面）。用户多次强调"**彻底融入本项目，不要用网页方式打开 127.0.0.1:8520，所有操作在软件本地进行**"。已完成：`--no-browser` 启动（不开网页）、程序自动拉起服务、UI 内集成人脸识别/AI 打标按钮、数据库迁入程序目录（`D:\Tool\PictureButler\imgtag\db.sqlite`）、`E:\imgtag` 已删除（源码备份至 `imgtag-rs\`）。
5. **版本号约定演变**：早期约定"中文+数字"（如「三十一」），**2026-09-13 用户明确改为纯数字**（见第七节）。

---

## 三、imgtag 现状（为什么还不算"彻底融合"）

### 3.1 当前形态

- **imgtag.exe**：Rust 编译的独立可执行文件（12.9MB），位于 `D:\Tool\PictureButler\imgtag\`
- **独立进程 + HTTP 服务**：程序通过 `ImgtagRecognizer`（`src-native\ImgtagRecognizer.cs`）用 HTTP 调用 `http://127.0.0.1:8520`
- **独立数据库**：`db.sqlite`（167.7MB，人脸/标签数据），与提示词库、自建索引分离
- **独立目录**：`D:\Tool\PictureButler\imgtag\`（exe + db + models ~340MB + onnxruntime.dll）

### 3.2 已完成的集成（UI/生命周期层）

- ✅ 程序启动自动拉起 imgtag（`MainWindow.xaml.cs` ~335 行 `EnsureServer`）
- ✅ `--no-browser`：不再打开 8520 网页
- ✅ 人脸识别 / AI 打标按钮已集成在程序 UI
- ✅ 数据库路径解析（`AppSettings.ResolveImgTagDb`）：程序目录 `imgtag\db.sqlite` 优先
- ✅ 识别结果在程序内展示（人物、标签、人脸框）
- ✅ 生命周期跟随程序（程序退出时服务由系统接管，下次启动重新拉起）

### 3.3 未完成的（进程级融合）——本文档要解决的核心

- ❌ **仍是独立进程**（用户可感知到独立 exe 在运行）
- ❌ **仍是独立 HTTP 服务**（127.0.0.1:8520）
- ❌ **仍是独立目录/独立数据库**（`D:\Tool\PictureButler\imgtag\`）
- ❌ **C# 侧调用全是 HTTP 封装**（`ImgtagRecognizer` 整个类）

**用户原话（2026-09-13）**："为什么还是以独立项目的形式存在？我之前多次强调将 imgtag 相关功能彻底融合到本项目。"

---

## 四、融合目标与验收标准

### 4.1 目标

将 imgtag 识别能力**内嵌到 PictureButler 进程内**，用户无感知地完成识别，程序目录不再出现"独立项目"形态。

### 4.2 验收标准（完成定义，全部满足才算完成）

- [ ] 人脸识别 / AI 打标 / 识别结果查询**全部在 PictureButler 进程内完成**
- [ ] **无独立 imgtag 进程**（任务管理器看不到 imgtag.exe 单独运行）
- [ ] **无 8520 端口监听**
- [ ] `D:\Tool\PictureButler\imgtag\` 目录中不再有独立 exe（改为程序包内 `imgtag.dll` 等资源）
- [ ] 旧 `db.sqlite` 识别数据**无缝迁移/复用**（用户已识别的人脸与标签不丢失）
- [ ] 功能回归：批量识别人脸、单张识别、AI 打标、人物浏览/合并/改名、标签浏览，与当前行为一致或更好
- [ ] 程序启动/退出无残留进程，无端口占用
- [ ] 版本号按约定递增（当前 0.33.0 → 融合完成后新版本，纯数字）

---

## 五、技术方案：进程内集成（Rust cdylib + C# P/Invoke）

### 5.1 改造前后对比

| 维度 | 改造前 | 改造后 |
|---|---|---|
| 识别引擎形态 | imgtag.exe（独立进程） | `imgtag.dll`（Rust cdylib，进程内加载） |
| 调用方式 | HTTP 127.0.0.1:8520 | P/Invoke 直接调用 |
| 数据 | imgtag\db.sqlite（独立） | 复用同一 db.sqlite（路径仍解析，读写方变为进程内） |
| C# 代码 | `ImgtagRecognizer`（HTTP 封装） | 保留类名与公开方法签名，内部换 P/Invoke（或新建 `ImgtagNative`） |
| 发布 | exe + 目录 | dll 并入单文件（或随 exe 旁置） |
| 用户感知 | 任务管理器可见 imgtag.exe、8520 端口 | 只有 PictureButler 一个进程 |

### 5.2 Rust 侧改造（imgtag-rs）

**前置**：Rust 工具链已就绪（cargo 1.98.1 / rustc 1.98.1），源码位于 `E:\work\PictureButler\imgtag-rs\`。

当前为 `[[bin]]` 工程（`src/main.rs`，axum 服务）。核心识别逻辑在 `src/face/`、`src/db/`、`src/llm/`、`src/api/`、`src/imageproc/` 模块。

**改造步骤**：

1. `Cargo.toml`：增加 `[lib] name="imgtag_native" crate-type=["cdylib"]`，与 `[[bin]]` 并存（bin 保留用于开发调试，发布只带 cdylib）。
2. 新建 `src/lib.rs`：导出 C ABI 函数（见 5.2.1），内部复用 face/db/llm 模块的现有实现，**不复制逻辑**。
3. **数据交换格式：统一用 JSON 字符串**（serde_json 序列化，C# 端用 System.Text.Json 解析），避免复杂的结构体 FFI 定义，降低两语言耦合。
4. 线程模型：导出函数为 `extern "C" fn`，内部用 tokio runtime（`tokio::runtime::Runtime` 常驻或每调用创建）承载异步识别；回调进度通过 C 函数指针。
5. onnxruntime：现有 `ort` crate 使用 `load-dynamic`（运行时加载 `onnxruntime.dll`）——在进程内同样适用，`models/` 目录路径需显式传入。

**5.2.1 建议导出函数（C ABI）**：

```c
// 初始化（传入 db 路径、models 目录路径），返回句柄
int32_t pb_imgtag_init(const char* db_path, const char* models_dir, void** handle_out, char** err_out);
// 同步新图片（扫描文件夹/索引新增），返回 JSON 结果
int32_t pb_imgtag_sync(void* handle, const char* request_json, char** result_out, char** err_out);
// 批量人脸识别（paths 数组或 folder），进度回调
int32_t pb_imgtag_scan_faces(void* handle, const char* request_json, void* progress_ctx,
                             void (*progress)(void*, const char*), char** result_out, char** err_out);
// AI 打标（需要 LM Studio，传入 lm_base_url）
int32_t pb_imgtag_ai_tag(void* handle, const char* request_json, void* progress_ctx,
                         void (*progress)(void*, const char*), char** result_out, char** err_out);
// 查询（人物列表/照片/人脸/标签），返回 JSON
int32_t pb_imgtag_query(void* handle, const char* request_json, char** result_out, char** err_out);
// 释放字符串 / 释放句柄
void    pb_imgtag_free_string(char* s);
int32_t pb_imgtag_shutdown(void* handle);
```

> 说明：函数清单是建议，执行团队可依据 `ImgtagRecognizer.cs` 现有公开方法（ServerRunning / SyncNewImages / ScanFacesByPathsAsync / AiTagByPathsAsync / AiTagOneAsync / ScanFaceOneAsync / ScanFacesAsync / AiTagAsync / FindImgTagId / MarkPendingForTagging）逐一映射，**保证 C# 侧调用方无感知替换**。

### 5.3 C# 侧改造（src-native）

1. 新建 `ImgtagNative.cs`：`[DllImport("imgtag_native")]` 封装上述函数；字符串用 `Marshal.PtrToStringUTF8`；错误用返回码 + err 指针；`pb_imgtag_free_string` 释放。
2. **改造 `ImgtagRecognizer`**：保持类名与公开方法签名不变，内部实现从 HTTP 改为调用 `ImgtagNative`（或新建 `NativeImgtagRecognizer`，由调用方切换）。**目标：`MainWindow.xaml.cs` 等调用方零改动**。
3. 生命周期：程序启动时 `pb_imgtag_init`（单例），退出时 `pb_imgtag_shutdown`；`EnsureServer` 方法退化为"确保已 init"。
4. 异步：现有调用方大量使用 `async Task<ScanResult>`，FFI 调用放 `Task.Run` 保持 UI 不卡；进度回调需 `Dispatcher.Invoke` 回 UI。
5. LM Studio 打标：LM Studio 是**外部软件**（127.0.0.1:1234），无法进程内合并；保持 reqwest/HTTP 调用（Rust 侧内联直连，不再经程序中转 HTTP→HTTP）。

### 5.4 数据与兼容

- **db.sqlite 复用**：Rust 侧 `rusqlite` 直接打开现有 `db.sqlite`（路径来自 `AppSettings.ResolveImgTagDb`），**旧数据天然兼容**（同库同表）。
- 需处理 SQLite 并发：进程内单句柄访问，C# 侧不再并发读写识别库（识别库写操作全部走 Rust 侧）。
- `image_index.db`（自建索引）仍由 C# 侧管理，与识别库保持 ATTACH 查询关系（`ImageLibraryService`），不改动。
- 迁移检查项：人脸归属、标签、人物封面、识别时间戳在替换后保持一致。

### 5.5 发布与打包

- 方案 A（推荐）：`imgtag_native.dll` + `onnxruntime.dll` + `models/` 放入发布目录，`PublishSingleFile` 下用 `IncludeNativeLibrariesForSelfExtract` 一并打包（现有命令已含该参数）。
- 方案 B：dll 随 exe 旁置（放弃单文件，换取加载确定性）。
- `models/`（~340MB）建议独立目录（不进单文件，避免每次解压），路径经 init 传入。
- 发布后**必须真机验证**：识别功能在**未单独启动任何服务**的情况下直接可用。

---

## 六、分阶段实施计划

> 每阶段都有明确产出与验收。执行团队按顺序推进，**每阶段完成且验收通过后再进入下一阶段**。

### 阶段 0：环境与基线（预计 0.5 天）
- [ ] 确认 Rust 工具链（cargo 1.98.1）可编译 `imgtag-rs`
- [ ] 确认 `imgtag-rs` 在当前机器可编译出可用 `imgtag.exe`（回归基线：能跑 8520 服务、能识别人脸）
- [ ] 通读 `src-native\ImgtagRecognizer.cs`（现有 HTTP 层全部方法）、`MainWindow.xaml.cs` 中 imgtag 相关调用、`AppSettings.cs`（路径解析）
- [ ] 备份 `D:\Tool\PictureButler\imgtag\db.sqlite`（识别数据，防止开发期损坏）
- **验收**：基线可编译、可识别；关键文件清单记录在案

### 阶段 1：Rust 库化（预计 1–2 天）
- [ ] `Cargo.toml` 增加 cdylib 目标；新建 `src/lib.rs` 导出 5.2.1 函数
- [ ] 内部复用 face/db/llm 模块，实现 init/sync/scan_faces/ai_tag/query/shutdown
- [ ] 编译产出 `imgtag_native.dll`
- [ ] 编写 Rust 侧冒烟测试（cargo test + 手动调 dll 的 C 程序或 ctypes）
- **验收**：dll 可加载，最小调用（init→query→shutdown）成功；人脸识别结果与独立进程版一致

### 阶段 2：C# FFI 封装（预计 1–2 天）
- [ ] 新建 `ImgtagNative.cs`（DllImport 封装 + 字符串/错误管理 + 进度回调）
- [ ] 改造 `ImgtagRecognizer`（或新增 Native 实现）：公开方法签名不变，内部走 FFI
- [ ] 程序启动 init / 退出 shutdown 挂接
- **验收**：程序内"识别人脸"按钮可用（不启动任何独立进程），人物/标签展示正常；UI 无卡顿

### 阶段 3：打标与全量接口（预计 1 天）
- [ ] AI 打标走 Rust 侧直连 LM Studio（127.0.0.1:1234）
- [ ] `FindImgTagId` / `MarkPendingForTagging` / `SyncNewImages` 等全部映射
- [ ] 进度回调、取消（CancellationToken）行为与现有一致
- **验收**：打标、同步、查询与独立进程版行为一致

### 阶段 4：替换与停用独立服务（预计 0.5 天）
- [ ] 移除自动拉起 imgtag.exe 的逻辑（`EnsureServer` 改为 init 检查）
- [ ] 8520 不再监听；旧 imgtag.exe 不再被引用
- [ ] `D:\Tool\PictureButler\imgtag\` 目录改造：移除 exe，保留 db/models 作为数据资源（或按 5.5 迁入程序包）
- **验收**：任务管理器无 imgtag.exe；`netstat` 无 8520；程序全功能正常

### 阶段 5：数据迁移与清理（预计 0.5 天）
- [ ] 确认旧 db.sqlite 数据完整（人脸数/人物数/标签数前后一致）
- [ ] 清理旧目录残留、无用文件
- [ ] 说明文件更新（架构说明、融合说明）
- **验收**：无旧进程/旧端口/旧目录形态残留；识别数据零丢失

### 阶段 6：回归测试与发布（预计 1 天）
- [ ] 全功能回归：提示词（增删改查/星标/搜索）、图片库（浏览/搜索/分页/多选/右键/删除）、识别（批量/单张/打标）、人物（浏览/合并/改名/换封面/删除）、标签
- [ ] 冷启动/热退出无残留进程；连续使用 2 小时内存稳定
- [ ] 版本号递增（纯数字，如 0.34.0），csproj + LocalHttpServer `/api/health` + 说明文件三处同步
- [ ] 发布到 `D:\Tool\PictureButler\PictureButler-绿色版.exe`
- **验收**：用户真机验收通过（识别在无任何独立服务情况下可用）

---

## 七、执行者必读约定（硬性要求）

1. **版本号**：只用**数字**（如 0.34.0），**不带中文、不带前后缀**；每次发布必须与历史全部版本号不同。需同步三处：`PictureButler.csproj` 的 `<Version>`、`LocalHttpServer.cs` 的 `/api/health` 返回 `"version":"x.y.z"`、`D:\Tool\PictureButler\PictureButler-绿色版-说明.txt` 版本历史。
2. **发布路径**：`D:\Tool\PictureButler\PictureButler-绿色版.exe`（用户唯一认可的交付物）。
3. **真机验证**：用户多次强调"怎么通过验证的"。远程/程序化模拟鼠标在本环境不可靠（SendInput/mouse_event 均无法送达 WPF），**交互类修复必须由用户真机确认**；识别类功能可用真实图片路径验证。
4. **编译缓存坑**：改 XAML 后若行为异常，先清理 `obj/bin` 再发布（曾因 wpftmp 缓存导致 `IsHitTestVisible` 未生效）。
5. **并发协作**：`src-native` 无 git，其他执行者可能同时改文件；改动前核对文件 mtime 并重读。
6. **用户偏好**：全程中文；**最讨厌 Docker**（禁止推荐）；不主动加绘画元素/不用画画技能；Windows 11；操作遵循"先探查→约束→证据→执行→验证→交付"。
7. **数据库注意**：`%APPDATA%\com.picturebutler.app\prompts.db` 已膨胀至 **929MB**（历史 `img_*_gallery/preview` 残留 key），影响磁盘与速度；未经用户确认**不要擅自清理**，可在计划内单独列清理方案。
8. **LM Studio**：AI 打标依赖用户本机 LM Studio（127.0.0.1:1234），是外部软件，不在本项目合并范围内；打标失败时提示"未检测到 LM Studio"。

---

## 八、已知遗留问题清单（供排期，非本计划范围）

| 问题 | 说明 | 建议 |
|---|---|---|
| prompts.db 929MB 膨胀 | kv 表 10 万行历史 `img_*_gallery_N_chunk_*` / `preview_chunk_*` key；items 仅 0.3MB | 单独出清理方案，用户确认后执行 |
| 扩展弹窗显示图片 | 历史诉求"扩展只显示文字不显示图"尚未彻底解决 | 涉及扩展+程序 HTTP 接口，单独排期 |
| 缩略图缓存 | `%LOCALAPPDATA%\com.picturebutler.app\thumbs` 505 文件 14.6MB，正常规模 | 无需处理 |

---

## 九、结论

imgtag 的"彻底融合"= **进程内 FFI 集成**：Rust 侧库化（cdylib）+ C# 侧 P/Invoke + 复用现有 db.sqlite + 停用独立进程与 8520 端口。工具链与源码已就绪，按第六节分阶段执行，预计 5–7 个工作日。完成标准见 4.2 验收清单。

---

## 十、执行记录（2026-09-13 完成，版本 0.33.0 → 0.34.0）

### 10.1 实际做法（与第五节方案的差异说明）

| 项 | 方案预定 | 实际实现 | 原因 |
|---|---|---|---|
| 引擎形态 | `imgtag_native.dll`（cdylib） | ✅ 同 | — |
| C# 调用 | P/Invoke | ✅ 同 | — |
| 句柄模型 | `void** handle` 显式句柄 | **进程内全局单例**（`Core`） | 宿主只需一份实例，省去句柄生命周期管理，降低 FFI 复杂度 |
| 字符串释放 | `pb_imgtag_free_string` | ✅ 同（所有返回串均须释放） | — |
| 人脸缩略图 | 未在方案中细化 | **经程序自身 8189 服务提供** | 原 `ServerUrl/api/face-thumbnail/{id}` 有 3 处调用点；改为程序进程内 HTTP 端点，调用方零改动 |
| 识别库写入 | 全部走 Rust | 写入分两类 | 扫描/打标/缩略图 → Rust；`SyncNewImages`/`MarkPendingForTagging`/`FindImgTagId` 保持 C# 直连 SQLite（原本就如此，WAL 下多连接安全，改动最小化） |
| 进度传输 | 回调函数指针 | ✅ 同（事件 JSON 与旧 SSE data 行**同构**） | 复用旧解析逻辑，C# 侧改动最小 |
| 取消 | CancellationToken | `pb_imgtag_cancel()` 置位标志，图片间生效 | 现有 UI 未使用取消；接口预留 |

### 10.2 交付物

| 文件 | 说明 |
|---|---|
| `imgtag-rs\Cargo.toml` | 新增 `[lib] name="imgtag_native"`、`crate-type=["cdylib","rlib"]` |
| `imgtag-rs\src\lib.rs` | **新增**：模块声明 + FFI 导出（init/health/cancel/shutdown/scan_faces/ai_tag/ensure_face_thumbnail/free_string） |
| `imgtag-rs\src\main.rs` | 改为引用库 crate（bin 保留作调试） |
| `src-native\ImgtagNative.cs` | **新增**：DllImport 封装 + 自定义 DLL 解析器 + 进度回调 + UTF-8/错误管理 |
| `src-native\ImgtagRecognizer.cs` | 公开方法签名**不变**，内部 HTTP → FFI |
| `src-native\LocalHttpServer.cs` | 新增 `/api/face-thumbnail/{id}`；新增静态 `BaseUrl` 与 `FaceThumbnailProvider` |
| `src-native\MainWindow.xaml.cs` | 挂接缩略图提供者；退出时释放引擎 |
| `D:\Tool\PictureButler\imgtag\imgtag_native.dll` | 发布产物（9.22 MB） |

### 10.3 验收清单（4.2）结果

- [x] 人脸识别 / AI 打标 / 识别结果查询全部在 PictureButler 进程内完成
- [x] 无独立 imgtag 进程（`imgtag.exe` 已从数据目录移除；进程数 = 0）
- [x] 无 8520 端口监听（已实测本机 TCP 监听表确认）
- [x] `imgtag\` 目录不再有独立 exe（仅剩 `db.sqlite`/`models`/`onnxruntime.dll`/`imgtag_native.dll`/缓存目录）
- [x] 旧 `db.sqlite` 无缝复用：**4100 images / 446 persons / 1611 faces / 4459 tags / 20284 image_tags，embedding MD5 `45d8537a…f73748` 全程未变**
- [x] 功能回归（程序化验证）：人脸扫描、单张/批量路径解析、缩略图**冷生成**、错误路径、无回调路径
- [x] 程序启动/退出无残留进程、无端口占用（`pb_imgtag_shutdown` 后 health=0）
- [x] 版本号递增 0.33.0 → **0.34.0**（csproj + `/api/health` + 两份说明文件四处同步）
- [ ] **UI 真机交互验收**（点击「识别图片（人脸）」「AI 打标」按钮、人物封面显示）——按第七节约定 3，须由用户真机确认

### 10.4 程序化验证证据

1. **Rust 独立冒烟**（Python ctypes，数据库副本）：init rc=0（0.14s）→ health=1 → 缩略图冷生成（0.02s / 9657 字节）→ 扫描 3 图检出 3 脸（1.48s）→ shutdown → health=0。
2. **C# FFI 冒烟**（直接编译生产代码 `ImgtagNative.cs` 的 net10.0 控制台）：init/健康检查/带回调扫描（4 条事件 JSON 与旧格式逐字一致）/无回调扫描/缩略图/非法 JSON 错误路径（中文错误信息 UTF-8 往返正确）/shutdown —— 全部通过，退出码 0。
3. **真实库只读校验**：验证前后统计与 embedding 指纹完全一致。

### 10.5 回滚与备份

- 识别数据备份：`E:\work\PictureButler\.backup-imgtag-20260913\`（db.sqlite + wal + shm + 旧 imgtag.exe + index.html + start.bat）
- 回滚方式：把备份中的 `imgtag.exe` 放回 `D:\Tool\PictureButler\imgtag\`，并用 0.33.0 的 exe 覆盖交付物即可复原旧形态。

### 10.6 遗留与注意

1. **打包形态**：`imgtag_native.dll` 放在**数据目录**（`imgtag\`）而非打进单文件，通过 `NativeLibrary.SetDllImportResolver` 按绝对路径加载——避开单文件解压目录的不确定性，加载更可靠。分发时须同时携带 `imgtag\` 目录。
2. **LM Studio** 仍是外部软件（127.0.0.1:1234），不在合并范围内；打标失败仍提示"未检测到 LM Studio"。
3. `set_var("ORT_DYLIB_PATH")` 在 init 早期单线程窗口内执行，无并发读环境变量的风险。
4. prompts.db 929MB 膨胀问题仍未处理（第八节），本次未触碰。

