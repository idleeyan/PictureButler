# PictureButler UI 视觉升级计划 · 执行版

| 字段 | 内容 |
|------|------|
| **定位** | 项目**唯一权威**的 UI 视觉升级计划 |
| **基线** | `src-native` 实际代码 **0.44.0**（2026-09-13 实测，非引用旧文档） |
| **取代** | 本文件合并并取代同目录四份旧计划：`UI视觉升级计划.md`(TraeDesign)、`UI视觉升级计划-MiMo-桌面WPF主线.md`(MiMo)、`UI视觉升级计划-Web-Imgtag前端扩展生态.md`(Marvis)、`UI视觉升级计划-Doubao-工程落地版.md`(Doubao) |
| **范围** | WPF 桌面主线（`src-native` 全部 XAML）+ 浏览器扩展 popup 对齐 |
| **排除** | imgtag Web 前端视觉、识别/性能/数据库逻辑、安装包与官网 |

---

## 0. 前提澄清（必须先读）

### 0.1 产品面：Web 前端已不存在

四份旧计划里有两份假设存在「imgtag Web 前端（Vue 3 + Tailwind，三栏布局）」。**实测该产品面已退役**：

| 检查项 | 实测结果 |
|--------|----------|
| 仓库内 `index.html` | 仅两处：`.backup-imgtag-20260913\index.html`（130KB，与原 `imgtag.exe`、`db.sqlite` 一同归档）与 `桌面版转换方案\index.html`（66KB，**设计稿**，非产品界面） |
| `src-native` 是否还有 Web 界面 | 无 `index.html`、无 WebView2、无 8520 端口 |
| 当前 HTTP 面 | 仅进程内 **8189**（供浏览器扩展取数据、取人脸缩略图） |

**结论：视觉升级只需覆盖两块** —— WPF 桌面（主体）+ 浏览器扩展 popup（`extension\popup.html`，3.7KB）。

### 0.2 基线：四份旧计划的版本号全部过期

| 旧计划 | 声称基线 | 实际 |
|--------|----------|------|
| TraeDesign | 0.24.0，批次 0.25「二十五」等 | **0.44.0** |
| MiMo | 阶段名 V0–V4 | 同上 |
| Marvis | 未提 | 同上 |
| Doubao | 0.36.0，批次 0.37/0.38/0.39 | 同上 |

本计划一切数字均为 **0.44.0 实测值**。

### 0.3 0.44.0 实测现状数字（本计划的依据）

**硬编码色值分布**（`#[0-9A-Fa-f]{6|8}`）：

| 文件 | 处数 | 唯一色值 | 说明 |
|------|------|----------|------|
| `MainWindow.xaml` | **73** | 35 | 主要战场 |
| `ImgViewerWindow.xaml` | **22** | 10 | |
| `PromptEditWindow.xaml` | **20** | 9 | |
| `PersonPickerWindow.xaml` | 7 | 5 | |
| `FacePickerWindow.xaml` | 6 | 5 | |
| `InputBoxWindow.xaml` | 4 | 4 | |
| **业务 XAML 合计** | **132** | — | 目标：全部归零 |
| `App.xaml` | 57 | 30 | 资源定义处，**允许保留** |

> 注：旧文档写的「64 / 18 / 18」是 0.36 时的数字，已失效。

**字号实际使用**：8、9、10、10.5、11、11.5、12、12.5、13、14、15、16、17、19、30 —— **共 15 种**，其中半像素字号 4 种（10.5/11.5/12.5）。这是「视觉毛刺」的直接来源。

**`App.xaml` 资源现状**：12 个 `SolidColorBrush` + 11 个 `Style`。

其中 **3 个 Style 已是死代码**（0.42 把筛选下拉整体换成内联控件后无人引用，约 90 行）：
`FilterCombo`（47 行）、`FilterComboItem`（24 行）、`FilterCycleBtn`（20 行）。

---

## 1. 设计原则与红线

### 1.1 原则（冲突时按序遵守）

1. **厚重感优先**（用户原话诉求）：Windows 原生质感 = 实体边框 + 精确 1px 分隔线 + 克制动效。**禁止**玻璃拟态、大发光、大面积渐变。
2. **图优先**：任何装饰不得抢缩略图、人脸框、三态徽章的注意力；强调色用于状态，不做大面积背景。
3. **一套 token**：业务 XAML 禁止再出现 `#RRGGBB`，一律 `{StaticResource …}`。
4. **操作有主次**：每屏 Primary ≤ 1、Secondary ≤ 2，其余 Ghost；危险操作永远独立 Danger 色相。
5. **状态可读**：加载 / 空 / 错误必须是「结构化视觉 + 行动引导」，不是一行灰字。
6. **不动功能契约**：Click 路径、快捷键、托盘、HTTP、识别写库一律不改，只改呈现层。
7. **深色单一主题**：沿用现有深色蓝灰体系，**不换色相**。浅色主题不列入本计划（token 架构预留即可）。

### 1.2 技术红线（0.44.0 血泪教训，违反必出故障）

| # | 红线 | 原因 |
|---|------|------|
| **R1** | **筛选区禁止使用任何弹出层（Popup / 下拉框）** | 本机实测：手写 ComboBox 模板的弹层收不到鼠标输入，点选项会穿透到下方网格误开图片。0.41/0.42 已把排序/状态/来源整体换成内联分段选择，**不得回退为下拉** |
| **R2** | **点击兜底的两条不变量必须保留**：①判定前先做**滚动可视区**判断；②先命中测试、再沿父链找 `DataContext`、再按**容器矩形**兜底 | 图片网格与人物列表都在滚动容器内、随内容滚动。0.37 漏了①导致「点页签/按钮穿透、误开图片」，0.43/0.44 才修好 |
| **R3** | **图片网格结构不可改**：`ItemsControl + FlowWrapPanel`、卡片逻辑尺寸（168×196）、虚拟化与批量缩略图管线 | 0.21 线已验过的性能结构；改卡片高度会破坏滚动与内存 |
| **R4** | **任何 `PopupAnimation` 一律 `None`** | Slide 收起动画期间弹层可见但不收点击，是第二个穿透源头 |
| **R5** | **列表滚动区零动效**；全局动效只动 `opacity` 与 ≤3px `translate` | 性能与「厚重感」双重约束 |
| **R6** | 改 XAML 后**必须 `dotnet clean` 再 publish**；版本号**纯数字且与历史全不同**；版本号必须显示在 UI 上 | 既有工程约定 |
| **R7** | 交互类改动**必须由用户真机验收**（本环境 `SendInput` 送不到 WPF） | 环境限制 |

---

## 2. Design Tokens

> 落地形式：扩展 `App.xaml` 资源字典。**键名沿用现有命名习惯**（`XxxBrush`），避免与既有 12 个画刷双轨。

### 2.1 色彩

**现有 12 个画刷保留不动**（已在使用，改值即全量回归）：

`BgBrush` `TitleBarBrush` `PanelBrush` `PanelAltBrush` `BorderBrush` `HoverBrush`
`TextBrush` `TextDimBrush` `TextFaintBrush` `AccentBrush` `AccentDimBrush` `StarBrush`

**新增 11 个**（补齐语义色 + 吸收散落的硬编码）：

| Token | 值 | 取代的硬编码 | 用途 |
|-------|-----|--------------|------|
| `SuccessBrush` | `#3D9A6A` | — | 人脸已识别徽章、扫描完成、成功态 |
| `SuccessSoftBrush` | `#123524` | — | 成功提示条底 |
| `InfoBrush` | `#4A6FD4` | — | AI 已打标徽章 |
| `WarnBrush` | `#C77D2E` | 查看器提示词行 `#C77D2E` 等 | 提示词关联、入库提醒、NoticeBar 主色 |
| `WarnSoftBrush` | `#332A1B` | NoticeBar 底 | 提醒条底 |
| `DangerBrush` | `#E5484D` | 侧栏红点、失败态 | 删除、红点、失败 |
| `DangerSoftBrush` | `#3B1D12` | — | 危险确认条底 |
| `OverlayBrush` | `#B010131A` | 加载遮罩、查看器浮层 | 半透明遮罩 |
| `SelectedBrush` | `#242E44` | 卡片/列表选中底 | 选中态底 |
| `PanelDeepBrush` | `#10131A` | 查看器画布 `#10131A` | 最深面板 |
| `BorderStrongBrush` | `#3A4256` | 滚动条滑块、强描边 | 强边框、滚动条 |

**硬规则**：强调色不做大面积背景；内容区底永远中性；三态徽章保持「暗=未做 / 亮=已做」语义，只换值不换义；不引入第 5 个色相。

> **取舍说明**：旧 MiMo 版设计了 `N0–N950` 十五级中性阶。本计划**不采纳**——现有 12 画刷已覆盖实际用色，引入完整色阶需要把每一处颜色重新映射，对一个「只动呈现层」的升级来说风险与收益不成比例。色阶保留为将来做浅色主题时的 v2 选项。

### 2.2 字号（15 种 → 7 档 + 2 处命名例外）

| Token（资源键） | px | 用途 |
|-----------------|-----|------|
| `TypeMicro` | 10 | 角标计数、极弱注释 |
| `TypeCaption` | 11 | 说明、路径、次要元数据 |
| `TypeBody` | 12.5 | 正文、按钮文案 |
| `TypeBodyLg` | 13.5 | 列表主文案、卡片标题 |
| `TypeTitle` | 15 | 弹窗标题、区块标题 |
| `TypeSection` | 17 | 查看器/设置大标题 |
| `TypeDisplay` | 24 | 空态主句（仅空态） |

**两处命名例外**（不进正文刻度，各自单独定义为常量）：

| 例外 | px | 位置 | 理由 |
|------|-----|------|------|
| `TypeBadgeGlyph` | 8.5 | 图片卡「脸/标/词」徽章 | 150px 缩略图上的单字徽章，放大即失比例 |
| `TypeNavGlyph` | 30 | 查看器 ‹ › 大箭头 | 字形符号而非文字 |

**字重**：默认 `Regular`；标题 / 选中导航 / 关键数字 `SemiBold`；**禁止**业务文案用 `Bold`/`Black`。

**行高**：正文 1.65；标题 1.3。

### 2.3 间距 / 圆角

- **间距只取六档**：`S1=4` `S2=8` `S3=12` `S4=16` `S5=20` `S6=24`。禁止 7/10/14/18 等游离值，禁止半像素。
- **圆角五档**：`R1=4`（输入/徽章）、`R2=6`（按钮/小卡）、`R3=8`（搜索框/主卡片/菜单）、`R4=10`（人物卡）、`R5=999`（标签芯片/头像）。

### 2.4 动效

| Token | 值 |
|-------|-----|
| `DurFast` | 120ms（hover / 按下） |
| `DurBase` | 180ms（选中、面板显隐） |
| `DurSlow` | 240ms（Toast 进出） |

准则：只动 `opacity` / ≤3px `translate`；禁止缩放弹跳、旋转、弹簧；**列表滚动区零动效**；跟随系统「减弱动画」时全部归零（R4、R5）。

---

## 3. 控件体系

### 3.1 现有 11 个 Style 的处置

| Style | 处置 |
|-------|------|
| `CaptionBtn` | 保留，语义归为 **Ghost**（标题栏按钮） |
| `NavBtn` | 保留（侧栏导航） |
| `SearchBox` | 保留，字号收敛 `TypeBody`，focus 边 `AccentBrush` |
| `PromptItemStyle` | 保留，选中底改用 `SelectedBrush` |
| `OptionBtn` | 保留（页签 / 分段控件基类） |
| `ActionBtn` | 保留，**语义降为 Secondary 基类** |
| `FilterChip` | 保留（内联分段芯片，0.42 新增，已含选中态） |
| `FilterGroupLabel` | 保留（筛选分组小标题） |
| ~~`FilterCombo`~~ | **删除**（死代码，且违反 R1） |
| ~~`FilterComboItem`~~ | **删除**（随上） |
| ~~`FilterCycleBtn`~~ | **删除**（死代码，0.42 已被 FilterChip 取代） |

### 3.2 新增 7 个 Style

| 新 Style | 要点 |
|----------|------|
| `BtnPrimary` | `AccentBrush` 底、白字、圆角 `R2`；每屏 ≤ 1 |
| `BtnGhost` | 透明底、hover `HoverBrush`、字 `TextDimBrush` |
| `BtnDanger` | 透明底、字 `DangerBrush`、hover `DangerSoftBrush`；最终确认用 `DangerBrush` 实底白字变体 |
| `CheckBoxDark` | 自绘 18px 勾选盒（系统默认在深色下刺眼）；选中 `AccentBrush` 底 + 白色 `Path` 对勾 |
| `ScrollBarDark` | 宽 10，滑块 `BorderStrongBrush` 圆角 999，轨道透明；全局应用 |
| `MenuDark` | ContextMenu 深底 `PanelBrush`、hover `BorderBrush`、分隔线 `BorderBrush`；**危险项（删除）字 `DangerBrush`** |
| `ToolTipDark` | 深底 `PanelDeepBrush`、`TypeCaption`、max-width 280 |
| `FocusVisual` | 统一 2px `AccentBrush` 外描边（offset 1），设为全局 `FocusVisualStyle` |

> 共 8 项（含 FocusVisual），其中 FocusVisual 以附加属性方式全局挂载。

---

## 4. 分文件改动清单

### 4.1 `App.xaml`

1. 新增 §2.1 的 11 个 `SolidColorBrush`。
2. 新增 §2.2 字号、§2.3 间距/圆角、§2.4 时长键（`sys:Double` 资源）。
3. 新增 §3.2 的 8 个 Style。
4. **删除** `FilterCombo` / `FilterComboItem` / `FilterCycleBtn`（约 90 行死代码）。

### 4.2 `MainWindow.xaml`（73 处硬编码 → 0）

| 区域 | 控件 | 动作 |
|------|------|------|
| 多选勾选框 | `MultiChk` 内 `Box`/`Tick` | 改用 `CheckBoxDark` |
| 侧栏红点 | `NavImagesBadge`（`#E5484D`） | → `DangerBrush` |
| 侧栏状态 | `HttpStatusText` | 灰 → 红/绿点 + 短文案（就绪 Success / 异常 Danger / 启动中灰） |
| 版本号 | `VersionText` | 字号 `TypeCaption`（**必须继续显示，用户靠它确认版本**） |
| 入库提醒 | `NoticeBar`（`#332A1B`/`#8A6A2F`）、`NoticeBarText`（`#FFD9A0`） | → `WarnSoftBrush` / `WarnBrush` / `TextBrush` |
| 提示词顶栏 | `PromptNewBtn` | **升 `BtnPrimary`**（每屏唯一主操作） |
| 提示词顶栏 | `PromptMultiBtn`/`PromptSelAllBtn`/`PromptSelDelBtn` | 多选/全选 = Secondary；删除所选 = **`BtnDanger`** |
| 提示词列表 | `PromptGrid` 项 | 选中底 `#242E44` → `SelectedBrush` |
| 图片库 行0 | `ImgSearchBox`/`ImgTagChipPanel`/`ImgCountText` | 全部进 token；计数 `TypeBody` |
| 图片库 行1 | `ImgTabAll`/`ImgTabPersons`/`ImgTabTags` | 保持 `OptionBtn` |
| 图片库 行1 | `ImgBackBtn` | → `BtnGhost` |
| 图片库 行1 | `ImgMultiBtn` | → `BtnGhost` |
| 图片库 行1 | `ImgRecognizeBtn`/`ImgTagBtn` | → Secondary（`ActionBtn`） |
| 图片库 行1 | 多选条 `ImgSelDelBtn` | → **`BtnDanger`**；`ImgSelCount` → `AccentBrush` |
| 图片库 行2 | `ImgFilterGroup` + `FilterGroupLabel`×3 + `FilterChip`×11 + `ImgFolderChipPanel` | 进 token；**保持内联无弹层（R1）** |
| 图片库 行3 | `ImgListScroll`/`ImgGrid` 卡片 | 卡片 hover 边 `BorderBrush`、选中边 `AccentBrush` 1.5 + 底 `SelectedBrush`；文件名区**固定两行**防卡高跳动（R3） |
| 图片库 行3 | 三态徽章（脸/标/词） | 未做底 `BorderStrongBrush`、字 `TextDimBrush`；已做分别 `SuccessBrush`/`InfoBrush`/`WarnBrush`。**保持中文单字，不做图标化** |
| 图片库 行3 | `PersonGrid` 卡片 | 头像加 1px `BorderBrush` 描边；姓名 `TypeBodyLg` SemiBold；张数 `TypeCaption` `TextFaintBrush` |
| 图片库 行3 | `TagGrid` 芯片 + `ImgTagChipPanel` | 芯片 `BorderBrush` 底全圆；已加入 AND 筛选的芯片 `AccentSoft`（新增 `AccentSoftBrush`=`#233459`）底 + `AccentBrush` 字，可单个 × 移除 |
| 图片库 行3 | `ImgLoadingMask`（`#B010131A`） | → `OverlayBrush` |
| 图片库 行3 | `ImgEmptyText`（行内灰字） | **空态三件套**：主句 `TypeDisplay` + 副句 `TypeBody` `TextDimBrush` + 行动按钮（清除筛选 / 去设置加文件夹），按上下文动态显示 |
| 图片库 行4 | `ImgPrevBtn`/`ImgNextBtn`/`ImgPageText` | 按钮 Secondary；当前页数字 `AccentBrush` |
| 右键菜单 | 图片卡 `ContextMenu` 10 项 | → `MenuDark`；「删除（回收站）」字 `DangerBrush` |
| 设置页 | `SettingsView` 各分组 | 去「卡中卡」：组内条目用 1px `BorderBrush` 分隔；`ScanStatus`/`ComfyStatus`/`PruneStatus`/`ThumbCacheStatus` 按状态着色；危险区（清理缓存 / 清理空人物）加 `DangerSoftBrush` 提示条 + `BtnDanger` |
| 详情栏 | `DetailPanel` / `DetailStarBtn` | 标题 `TypeTitle` SemiBold；星标保持 `StarBrush` |

### 4.3 `ImgViewerWindow.xaml`（22 处 → 0）

| 区域 | 动作 |
|------|------|
| 画布 `#10131A` | → `PanelDeepBrush` |
| 标题栏 / 底栏 `#161A24`、顶边 `#2A3040` | → `TitleBarBrush` / `BorderBrush` |
| 前后导航钮 `#99161A24` | → `OverlayBrush` 变体 + hover 边 `AccentBrush`；字号用 `TypeNavGlyph` |
| 位置条 / 缩放条 `#CC10131A` | → `OverlayBrush` 变体 |
| 提示词行 `#C77D2E` | → `WarnBrush` |
| 状态行 `#5B8DEF` | → `AccentBrush` |
| 元数据 / 描述行 | 进 token |
| 底栏操作区 | **删除类独立为 `BtnDanger`**，不与「打开」并排同权 |

### 4.4 `PromptEditWindow.xaml`（20 处 → 0）与 `InputBoxWindow.xaml`（4 处 → 0）

- 全部硬编码 → token。
- 按钮按「取消 = Secondary / 确定 = `BtnPrimary`」；删除确认 = `BtnDanger` 实底变体。
- 统一 Shell 质感（同底色、同圆角），消除与选择器窗口的风格分裂。

### 4.5 `FacePickerWindow.xaml`（6 处）/ `PersonPickerWindow.xaml`（7 处）→ 0

- 统一为无边框自绘 Shell（`WindowStyle=None` + 自绘标题栏），与主窗同底色。
- 列表行 hover `HoverBrush`、选中 `SelectedBrush`；人脸选卡 hover 边 `AccentBrush`，当前封面加「当前」角标。

### 4.6 `extension\popup.html`（对齐附录）

- 颜色沿用主程序 token 表；按钮圆角 `R2`、高 28；输入 focus 边 `AccentBrush`。
- 列表项圆角 `R3`、间距 `S2`；连接状态胶囊用 Success / Danger 软底。
- **宽度保持 340px**（用户已有肌肉记忆）；星标用图标替代字符 `★`。
- 保持纯 HTML/CSS/JS，不引入框架。

---

## 5. 实施批次

> 版本号遵循既有约定：**纯数字、与历史所有版本不同、必须显示在 UI 上**。以下为建议号。

### 批次 1 —— 0.45.0：Token 收敛 + 控件补全（零布局改动）

**目标**：消灭色值双轨，补齐缺失控件。纯色值替换，**不移动任何控件位置、不改任何 Click**。

1. `App.xaml`：+11 画刷、+字号/间距/圆角/时长键、+8 Style、−3 死 Style。
2. 六个窗口 XAML 的 **132 处**硬编码 → 资源引用。
3. 字号收敛到 7 档（+2 命名例外）。

**验收**
- `grep -E '#[0-9A-Fa-f]{6,8}' src-native/*.xaml` 仅命中 `App.xaml` 资源定义处。
- 主窗 / 查看器 / 提示词编辑 / 两个选择器 / 输入框：打开、切页签、翻页、识别、打标、多选无视觉回归。
- 版本号在 UI 上正确显示 0.45.0。

**回退**：纯颜色/字号替换，单文件 revert 即可。

### 批次 2 —— 0.46.0：层级与控件质感

**目标**：解决用户感知最强的「按钮分不清主次」「出戏控件」。

1. 按钮主次落地：`PromptNewBtn` 升 Primary；删除类全部转 Danger；多选/返回转 Ghost。
2. 深色化 `CheckBoxDark`、`ScrollBarDark`、`MenuDark`、`ToolTipDark`。
3. `FocusVisual` 全局挂载。
4. 空态三件套：图片库 / 提示词 / 人物 / 标签 / 搜索无结果 五处。
5. 设置页状态着色 + 危险区分组。

**验收**：1280×800 下工具栏不溢出；空态可点出主行动；删除类肉眼可辨为红色系；Tab 一圈焦点可见。

### 批次 3 —— 0.47.0：结构收尾与品牌

1. 查看器底栏主次分离（删除独立）。
2. 弹窗 Shell 统一（四个子窗）。
3. 工具条信息架构收尾（行1 页签+操作 / 行2 筛选，已在 0.42 成型，此处只做对齐与间距）。
4. 品牌：标题栏 `◆` 升级为 16px 矢量标记，与扩展图标同源。
5. 扩展 popup 对齐 token。
6. 动效：选中 / Toast / 面板显隐 120–240ms（R5）。

**验收**：扩展与主窗截图并排「像同一产品」；Toast 出入不抢焦点；滚动区无掉帧。

### 可选（不阻塞发版）

浅色主题（同一 token 架构换映射表）、侧栏导航真实图标、设置页二级侧栏。

---

## 6. 验收总清单

- [ ] `grep -E '#[0-9A-Fa-f]{6,8}' src-native/*.xaml` 仅剩 `App.xaml` 定义处（业务硬编码 = 0）
- [ ] 字号仅落在 10 / 11 / 12.5 / 13.5 / 15 / 17 / 24（+ 徽章 8.5、导航字形 30 两处命名例外）
- [ ] 每屏 Primary ≤ 1；所有删除类为 Danger
- [ ] CheckBox / 滚动条 / 右键菜单 / ToolTip 均为深色自定义样式
- [ ] 五处空态均有「主句 + 行动引导」
- [ ] 多选、筛选、分页在 1280×800 与 1920×1080 均不溢出
- [ ] 查看器、输入框、编辑窗、两个选择器风格统一
- [ ] 扩展 popup 色值与主程序一致
- [ ] 焦点环可见；Tab 可走通主路径
- [ ] **功能回归**：搜索、分页、多选删除、识别、打标、换封面、合并、设置保存
- [ ] **性能回归**：图片库加载/翻页/多选耗时无劣化（R3 结构未动）
- [ ] **交互真机验收**：筛选切换不误开图片、页签/返回不穿透、滚到底后点上方按钮无误开（R1/R2/R4）
- [ ] 改 XAML 后清理 `obj`/`bin` 再发布；版本号纯数字且与历史不同

---

## 7. 明确拒绝（Anti-Patterns）

- **不**给每个设置项套阴影卡片（卡中卡）。
- **不**做玻璃拟态、发光描边、大面积渐变。
- **不**让主按钮满天飞（识别/打标/多选全蓝底会毁掉层级）。
- **不**把三态徽章「脸 / 标 / 词」图标化：150px 缩略图上中文单字的辨识密度最高（此为产品差异化资产）。
- **不**把筛选区改回下拉或任何弹层（R1）。
- **不**为「好看」加插画空态大图 —— 本地工具的空态应指向动作。
- **不**在视觉升级中顺手改 SQL / 识别 / HTTP / 扩展协议。
- **不**引入与现有虚拟化不兼容的自适应卡片高度。
- **不**引入 Docker、重量级 UI 框架，**不**把 Web 前端重构塞进本线。
- **不**把 Lucide 之类 Web 图标库作为 WPF 运行时依赖：需要图标时用内嵌 `Path` geometry 集中放 `Icons.xaml`。

---

## 8. 与四份旧计划的取舍记录（Decision Trace）

| 决策 | 取自 | 理由 |
|------|------|------|
| 不换色相，沿用 `#5B8DEF` 深色蓝灰体系 | MiMo / Doubao | 选中/焦点/主按钮已绑定蓝系，换色需全量回归状态对比，收益不抵成本 |
| Token 键名沿用 `XxxBrush`，不引入 `N0–N950` 色阶 | MiMo 的键名纪律 + 本项目现状 | 引入十五级色阶需重映射所有用色，对「只动呈现层」风险过高；色阶留作浅色主题 v2 |
| 文件级改动清单与批次顺序 | Doubao | 其控件级清单与验收命令最可执行；但此处已按 0.44.0 实测重算（132 处、15 种字号、3 个死 Style） |
| 扩展 popup 对齐规则 | TraeDesign / Marvis | 两者一致；扩展是真实产品面，值得对齐 |
| 性能红线（网格结构、虚拟化、卡片尺寸不动） | MiMo / Doubao | 双方独立得出同一结论，且与 0.21 性能线一致 |
| 「脸/标/词」徽章保持中文单字 | MiMo | 小尺寸下信息密度最高；否决 TraeDesign 的 Lucide 全量替换 |
| **筛选区禁用弹层** | **本项目 0.41–0.44 实测** | 非任何旧计划所知：手写 ComboBox 弹层在本机收不到鼠标输入，已导致多轮穿透故障 |
| **点击兜底不可变量（可视区 + 容器矩形）** | **本项目 0.37–0.44 实测** | 非任何旧计划所知：网格/人物列表在滚动容器内，缺可视区判断会误开图片 |
| 全平台 Web 部分整体作废 | — | 该产品面已退役（见 §0.1） |

---

## 9. 一句话总结

**先把 132 处硬编码与 15 种字号收成一套 token（0.45），再把按钮主次与深色控件做出来（0.46），最后统一弹窗、查看器与品牌（0.47）**；全程遵守 §1.2 的七条技术红线，只动呈现层，不动功能契约与性能结构。
