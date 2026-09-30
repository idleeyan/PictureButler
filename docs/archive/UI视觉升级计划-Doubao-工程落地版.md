# PictureButler UI 视觉升级计划（Doubao · 工程落地版 · 第三版）

| 字段 | 内容 |
|------|------|
| **文档归属** | Doubao Agent（本会话）独立产出，**非** `UI视觉升级计划.md`，**非** `UI视觉升级计划-MiMo-桌面WPF主线.md` |
| **唯一文件名** | `UI视觉升级计划-Doubao-工程落地版.md`（本文件） |
| **版本** | v1.0（三份计划中的**第三版**，另两份为 TraeDesign 版、MiMo 版） |
| **日期** | 2026-09-13 |
| **基线** | `src-native` 当前代码（csproj **0.36.0**，纯数字版本线） |
| **范围** | WPF 桌面主线为主（`src-native` 全部 XAML），扩展 popup 对齐为附录 |
| **排除** | imgtag Web 前端视觉、识别/性能/数据库逻辑、安装包视觉、Docker（永不引入） |
| **关系** | 与同目录另两份计划并行，**不覆盖、不合并**；本版定位为「文件级工程落地清单」，与规范版/组件版取交集后执行 |

> **防混淆说明**：同目录已存在另外两份同名主题计划。本文件为**第三份**，作者标签 `Doubao-Agent`。任何内容冲突以「文件归属表 + 附录 C 指纹」区分后再人工裁决，禁止直接合并覆盖。

---

## 0. 一句话定位

> 前两份计划回答了「设计成什么样」（TraeDesign：三线统一规范；MiMo：WPF token/组件体系）。
> 本版回答**「先改哪个文件、每处怎么改、改完怎么验收」**——以 0.36.0 源码为基线，把升级拆成三个可独立发版的批次，每批给出文件级改动清单、验收动作与回退方案。

**为什么需要第三份**：规范与组件体系给出的是「目标态」，但落地时真正的成本在「存量硬编码替换、控件名映射、回归范围」。本版把这两件事做成清单，避免执行时在 54KB 的 `MainWindow.xaml` 里大海捞针。

---

## 1. 现状体检（基于 0.36.0 源码实读，非推断）

### 1.1 已经做对的（保留，不要推倒）

| 资产 | 证据 | 处理 |
|------|------|------|
| 深色 token 骨架 | `App.xaml` 已有 12 个基础画刷（Bg/TitleBar/Panel/PanelAlt/Border/Hover/Text/TextDim/TextFaint/Accent/AccentDim/Star） | 保留，扩展而非重构 |
| 自绘无边框窗口 + 标题栏 | `MainWindow` `WindowStyle=None`，CaptionBtn 含关闭色变动画 | 保留 |
| 深色下拉 | `FilterCombo`（用户反馈的排序下拉反人类已修，现为不透明深底+浅字） | 保留，下沉为通用组件 |
| 三态徽章语义 | 图片卡「脸/标/词」亮=已做、暗=未做 | 保留语义，仅换色值 |
| 查看器基础 | 半透明前后导航钮、位置/缩放指示、滚轮缩放提示 | 保留 |
| 图片库性能结构 | ItemsControl + FlowWrapPanel + 批量缩略图命令（0.21 虚拟化线已验证） | **红线，不可改** |

### 1.2 视觉问题清单（按用户感知强度排序，附代码证据）

| 级别 | 问题 | 证据（0.36.0） | 用户感知 |
|------|------|----------------|----------|
| **P0** | 色值双轨：业务 XAML 硬编码与 App.xaml 画刷并存 | `MainWindow.xaml` **64 处** `#hex`（红点 `#E5484D`、NoticeBar `#332A1B`/`#8A6A2F`/`#FFD9A0`、遮罩 `#B010131A`、选中底 `#242E44` 等）；`ImgViewerWindow.xaml` **18 处**；`PromptEditWindow.xaml` **18 处** | 改主题必漏改；浅色模式永远做不了 |
| **P0** | 工具栏按钮海、主次塌陷 | 图片库行1 塞搜索+3 个下拉（宽固定 98/100/132）+计数；行2 塞 3 Tab+返回+模式按钮+识别/打标/多选，**全部 `ActionBtn` 同底同边同字号** | 找不到主操作；删除/识别/取消分不清 |
| **P0** | 语义色缺失 | App.xaml 无 Success/Info/Warn/Danger 及 Soft 变体；设置页 ScanStatus/ComfyStatus/PruneStatus 全用 `TextDimBrush` 灰字 | 状态反馈不可读 |
| **P1** | 字号/间距无刻度 | 10.5 / 11 / 11.5 / 12 / 12.5 / 13 / 14 / 17 混用；半像素字号多处（12.5/11.5/10.5） | 视觉毛刺、密度不均 |
| **P1** | 空态/错误态过弱 | `ImgEmptyText` 一行灰字（`#5A6480` 14px）「没有符合条件的图片」，无行动引导 | 新人不知道下一步点哪 |
| **P1** | 控件系统未覆盖 | CheckBox 系统默认（深色下刺眼）、滚动条默认、ContextMenu 默认偏亮、ToolTip 默认黄底、无统一 FocusVisualStyle | 右键/滚动/勾选「出戏」 |
| **P1** | 卡片视觉同质 | 图片卡/人物卡/提示词卡/标签芯片/设置分组均 `#191D28` 圆角块，仅圆角差异 | 页签切换像「换了一批一样的盒子」 |
| **P2** | 品牌符号弱 | 标题栏仅「◆」+ 文案；无统一 Logo 矢量 | 截图辨识度低 |
| **P2** | 运动几乎为零 | 仅关闭按钮 0.1s 色变；选中/翻页/Toast 无反馈 | 「不跟手」 |
| **P2** | 弹窗风格分裂 | `InputBox`/`PromptEdit` 无边框自绘 vs `FacePicker`/`PersonPicker` 默认边框 | 弹窗气质不一致 |

---

## 2. 设计原则（冲突时按序遵守）

1. **厚重感优先（用户原话诉求）**：Windows 原生程序质感 = 实体边框 + 精确 1px 分隔线 + 克制动效；**禁止**玻璃拟态、大发光、大面积渐变。用户已明确后悔「网页感」，本版一切视觉决策以此为准绳。
2. **图优先**：任何装饰不得抢缩略图、人脸框、徽章的注意力。
3. **一套 token**：业务 XAML 禁止再出现 `#RRGGBB`，一律 `{StaticResource …}`。
4. **操作有主次**：每屏 Primary ≤ 1、Secondary ≤ 2，其余 Ghost；危险操作永远独立 Danger 色相。
5. **状态可读**：加载/空/错误/部分成功必须是「结构化视觉 + 行动引导」，不是一行灰字。
6. **密度可调**：延续现有紧凑/标准/宽松三档（设置页已有），默认紧凑。
7. **不动功能契约**：Click 路径、快捷键、托盘、HTTP、识别写库一律不改；**只改呈现层**。
8. **性能红线**：图片库网格 `ItemsControl + FlowWrapPanel`、虚拟化、卡片逻辑尺寸**不可改**；视觉只动 Brush/Template。

---

## 3. 设计 Token 收敛方案（地基）

### 3.1 色彩：在现有 12 画刷上补 11 个，不换色相

沿用现有主强调 `#5B8DEF`（用户肌肉记忆 + 选中/焦点已绑定），只补齐缺失：

| 新增 Token | 值 | 用途 |
|-----------|-----|------|
| `SuccessBrush` | `#3D9A6A` | 已识别徽章、扫描完成、成功 Toast |
| `SuccessSoftBrush` | `#123524` | 成功提示条底 |
| `InfoBrush` | `#4A6FD4` | AI 已打标徽章 |
| `WarnBrush` | `#C77D2E` | 提示词关联、入库提醒、NoticeBar 主色 |
| `WarnSoftBrush` | `#332A1B` | NoticeBar 底（替换硬编码） |
| `DangerBrush` | `#E5484D` | 删除、红点徽章、失败状态（替换硬编码） |
| `DangerSoftBrush` | `#3B1D12` | 危险确认条底 |
| `OverlayBrush` | `#B010131A` | 加载遮罩、查看器半透明层（替换硬编码） |
| `HoverSelectedBrush` | `#242E44` | 选中卡片底（替换硬编码） |
| `ComboSelectedBrush` | `#243358` | 下拉选中底（替换硬编码） |
| `ComboHoverBrush` | `#2A3350` | 下拉悬停底（替换硬编码） |

**硬规则**：强调色不做大面积背景；内容区底永远中性；徽章保持「暗=未做/亮=已做」语义，只换值不换义；不引入第 5 个色相。

### 3.2 字号刻度（收敛为 7 档）

| Token | px | 用途 |
|-------|----|------|
| `TypeMicro` | 10 | 角标计数、极弱注释 |
| `TypeCaption` | 11 | 说明、路径、次要元数据 |
| `TypeBody` | 12.5 | 正文、按钮（半像素仅此一档例外） |
| `TypeBodyLg` | 13.5 | 列表主文案、卡片标题 |
| `TypeTitle` | 15 | 弹窗标题、区块标题 |
| `TypeSection` | 17 | 详情标题、设置大标题（由 19 降 17，避免与卡片标题落差过大） |
| `TypeDisplay` | 24 | 空态主句（仅空态） |

字重纪律：默认 Regular；标题/选中导航/关键数字 SemiBold；**禁止** Bold/Black 用于业务文案。

### 3.3 间距 / 圆角

- 间距只取 4/8/12/16/20/24 六档，禁止 7/10/14/18 等游离值。
- 圆角：输入/徽章 4，按钮/小卡 6，搜索框/主卡片 8，人物卡 10，标签芯片 999。与 MiMo 版一致，实现时共用同一组键名，避免两版 token 打架。

### 3.4 动效（克制动效，服务「厚重感」）

| Token | 值 |
|-------|-----|
| `DurFast` | 120ms（hover/按下） |
| `DurBase` | 180ms（选中、面板显隐） |
| `DurSlow` | 240ms（Toast 进出） |

准则：只动 opacity / 轻微 translate（≤3px）；**禁止**缩放弹跳、旋转、弹簧；列表滚动区域**零动效**；跟随系统「减弱动画」时全部归零。

---

## 4. 控件体系补全（App.xaml 需要新增的 Style）

现有 8 个 Style（CaptionBtn/NavBtn/SearchBox/FilterCombo(+Item)/PromptItemStyle/OptionBtn/ActionBtn）之外，补齐 7 个：

| 新 Style | 要点 |
|----------|------|
| `BtnPrimary` | `Accent` 底、白字、圆角 6；每屏 ≤1 |
| `BtnDanger` | 透明底、字 `Danger`、hover `DangerSoft`；确认删除用 `Danger` 实底白字变体 |
| `CheckBoxDark` | 自绘 18px 勾选盒（系统默认在深色下刺眼）；选中 `Accent` 底 + 白色 Path 对勾 |
| `ScrollBarDark` | 宽 10，滑块 `#3A4256` 圆角 999，轨道透明；全局应用 |
| `MenuDark` | ContextMenu 深底 `#1B1E28`、hover `#2A3040`、危险项红字；替换系统默认亮色菜单 |
| `ToolTipDark` | 深底 `#141824`、`TypeCaption`、max-width 280 |
| `FocusVisual` | 统一 2px `Accent` 外描边（Rectangle 附加，offset 1），全局 FocusVisualStyle |

**同时**：`ActionBtn` 保留为 Secondary 基类（现状即如此），通过以上新样式拆出主次。

---

## 5. 分页改造清单（文件级）

### 5.1 `App.xaml`
- 补 §3.1 的 11 个画刷、§3.2 字号键（可用 `sys:Double` 资源）、§3.3 间距/圆角键、§3.4 时长键。
- 新增 §4 的 7 个 Style。

### 5.2 `MainWindow.xaml`（64 处硬编码 → 全部替换为资源引用）

| 区域 | 控件名 | 改动 |
|------|--------|------|
| 多选勾选框 | `Box` / `Tick`（行 30-32） | 改用 `CheckBoxDark` 语义或抽成统一 Path |
| 侧栏图片库红点 | `NavImagesBadge`（`#E5484D`） | → `DangerBrush` |
| 侧栏状态 | `HttpStatusText` | 灰→红/绿点 + 短文案；就绪绿（Success）、异常红（Danger）、启动中灰 |
| 版本号 | `VersionText` | 保留灰，字号收敛 `TypeCaption` |
| NoticeBar | `NoticeBar`（`#332A1B`/`#8A6A2F`）+ `NoticeBarText`（`#FFD9A0`） | → `WarnSoftBrush`/`WarnBrush`；文字 `N900` 系 |
| 提示词页顶栏 | `PromptNewBtn` | **升 Primary**（每屏唯一主操作） |
| 提示词页顶栏 | `PromptMultiBtn` / `PromptSelAllBtn` / `PromptSelDelBtn` | 多选=Secondary；删除所选=**Danger** |
| 提示词列表 | `PromptGrid` 内项 | 复用 `PromptItemStyle`，选中底 `#242E44` → `HoverSelectedBrush` |
| 图片库行1 | `ImgSortBox`/`ImgStatusBox`/`ImgFolderBox` | 宽度 98/100/132 改为按内容自适应（MinWidth+MaxWidth），文字字号 `TypeCaption` |
| 图片库行2 | 全部 `ActionBtn` | 主次拆分：识别/打标=Secondary，「多选」=Ghost；多选态 `ImgSelDelBtn`=Danger |
| 多选计数 | `PromptSelCount`/`ImgSelCount` | `#5B8DEF` → `AccentBrush`，字号 `TypeBody` |
| 空态 | `ImgEmptyText`（行 466） | **空态三件套**：主句 24 + 副句灰 + 行动按钮（清除筛选/去设置加文件夹），按当前上下文动态显示 |
| 加载遮罩 | `ImgLoadingMask`（`#B010131A`） | → `OverlayBrush`；胶囊内容沿用 |
| 分页 | `ImgPrevBtn`/`ImgNextBtn`/`ImgPageText` | 页码居中，当前页数字 `Accent`，按钮 Secondary |
| 图片网格卡 | `ImgGrid` 模板 | hover 边 `N500`、选中边 `AccentGlow` 1.5 + 底 `AccentSoft`；勾选框左上 24；文件名区固定两行防卡高跳动 |
| 人物卡 | `PersonGrid` 模板 | 头像圆 80 加 1px `N500` 描边；姓名 SemiBold；张数 `TypeCaption` 灰 |
| 标签芯片 | `TagGrid` 模板 + `ImgTagChipPanel` | 芯片 `N350` 底全圆；AND 筛选已选芯片 `AccentSoft` 底 + `Accent` 字，可单个 × 移除 |
| 设置页 | `SettingsView` | 分组去「卡中卡」：条目间用 1px `BorderBrush` 分隔线；ScanStatus/ComfyStatus/PruneStatus/ThumbCacheStatus 按状态着色（成功绿/失败红/进行中蓝）；危险区（清理缓存/空人物）加 `DangerSoft` 提示条 + Danger 按钮 |
| 详情栏 | `DetailPanel` | 标题 `TypeTitle` SemiBold；`DetailStarBtn` 保持星标色 |

### 5.3 `ImgViewerWindow.xaml`（18 处硬编码 → 资源）

| 区域 | 改动 |
|------|------|
| 画布背景 `#10131A` | → `PanelDeepBrush`（新增键，与主窗体系一致） |
| 标题栏 `#161A24` | → `TitleBarBrush` |
| 前后导航钮 `#99161A24` | → `OverlayBrush` 半透明 + hover `Accent` 边 |
| 顶部位置条 / 右下缩放条 `#CC10131A` | → 统一 `OverlayBrush` 变体 |
| 提示词/元数据/状态/描述四行 | 提示词 `#C77D2E` → `WarnBrush`；状态 `#5B8DEF` → `AccentBrush`；其余进 token |
| 底栏 `#161A24` + 顶边 `#2A3040` | → `TitleBarBrush`/`BorderBrush` |

### 5.4 `PromptEditWindow.xaml`（18 处）与 `InputBoxWindow.xaml`
- 全部硬编码 → token；按钮按「取消=Secondary / 确定=Primary」落地。
- 统一 Shell 质感（与主窗同底色、同圆角），消除与 `FacePicker`/`PersonPicker` 的系统边框分裂。

### 5.5 `FacePickerWindow.xaml` / `PersonPickerWindow.xaml`
- 统一为无边框自绘 Shell（`WindowStyle=None` + 自绘标题栏），列表行 hover `N350`、选中 `AccentSoft`；人脸选卡 hover 边 `Accent`，当前封面加「当前」角标。

### 5.6 扩展 `popup.html`（对齐附录）
- 颜色沿用主程序 token 表；按钮圆角 6、高 28；输入 focus `Accent`；列表项圆角 8；连接状态 pill 用 Success/Danger 软底；宽度保持 340。

---

## 6. 分阶段实施（沿 0.36 版本线，三批可独立发版）

> 版本号遵循用户偏好：**纯数字、与历史所有版本不同**。以下 0.37/0.38/0.39 为建议号，最终以用户拍板为准；每批独立可验证、可回退。

### 批次 1 —— 版本 0.37：Token 收敛 + 控件补全（约 1–1.5 天）

**目标**：消灭色值双轨，补齐缺失控件；纯视觉，零布局改动，风险最低。

| 文件 | 动作 |
|------|------|
| `App.xaml` | 补 11 画刷 + 字号/间距/动效键 + 7 个新 Style |
| `MainWindow.xaml` | 64 处硬编码 → 资源引用（**只换值，不移动任何控件位置**） |
| `ImgViewerWindow.xaml` | 18 处 → 资源引用 |
| `PromptEditWindow.xaml` | 18 处 → 资源引用 |

**验收**：`Grep '#[0-9A-Fa-f]{6}' src-native/*.xaml` 仅剩 `App.xaml` 定义处；主窗/查看器/编辑窗打开、切换页签、翻页无视觉回归；改 XAML 后**清理 obj/bin 再编译**（既有工程约定）。

**回退**：全部为颜色替换，git 单文件 revert 即可。

### 批次 2 —— 版本 0.38：工具栏分层 + 按钮主次 + 空态（约 1–1.5 天）

**目标**：解决用户感知最强的「按钮海、分不清主次、空态无引导」。

| 区域 | 动作 |
|------|------|
| 提示词页 | `PromptNewBtn` 升 Primary；删除类转 Danger |
| 图片库行1 | 下拉宽度自适应，视觉降噪 |
| 图片库行2 | 识别/打标=Secondary、多选=Ghost、多选态删除=Danger |
| 空态 | 图片库/提示词/人物/标签/搜索无结果五个空态全部「主句+副句+行动按钮」 |
| 卡片家族 | 图片卡/人物卡/标签芯片按 §5.2 差异化（hover/选中/描边） |
| 设置页 | 状态着色 + 危险区分组 |

**验收**：1280×800 下工具栏不换行溢出；空态可点出主行动；删除类按钮肉眼可辨为红色系；截图对比 0.37 主次感明显提升。

### 批次 3 —— 版本 0.39：查看器/弹窗统一 + 动效 + 品牌 + 扩展对齐（约 1 天）

| 区域 | 动作 |
|------|------|
| 查看器 | 操作区主次分离（删除独立 Danger）；信息行分两排 |
| 弹窗统一 | InputBox/PromptEdit/FacePicker/PersonPicker 全部自绘 Shell |
| 动效 | 选中/Toast/详情栏 120–240ms（仅 opacity/translate） |
| 品牌 | 标题栏 Logo 升级为清晰 16px 矢量标记（与扩展同源） |
| 焦点 | 全局 FocusVisual 落地，Tab 导航可见 |
| 扩展 | popup 对齐 token |

**验收**：Tab 一圈焦点可见；扩展与主窗截图并排「像同一产品」；Toast 出入不抢焦点。

### V4 可选（不阻塞发版）
浅色主题（同一 token 架构换映射表）、导航图标槽真实图标、设置页二级侧栏。

---

## 7. 与另两份计划的关系与裁决建议

| 维度 | TraeDesign 版（UI视觉升级计划.md） | MiMo 版（-MiMo-桌面WPF主线.md） | **本版（Doubao · 工程落地版）** |
|------|-----------------------------------|----------------------------------|--------------------------------|
| 定位 | 三线统一设计规范 | WPF token/组件体系 | **文件级工程落地清单** |
| 范围 | WPF+Web+扩展 | 仅 WPF 主线 | WPF 主线为主+扩展对齐 |
| 色相 | `#0b1120` 体系 + Lucide 图标 | 保留 `#5B8DEF` 体系 | **沿用 `#5B8DEF` 体系**（与 MiMo 一致） |
| 版本规划 | 0.25「二十五」等（**已过时**，现为 0.36 纯数字线） | V0–V4 阶段名 | **0.37/0.38/0.39 纯数字**，与历史不同 |
| 落地粒度 | 组件规范、验收标准 | token 键名、Style 键名清单 | 控件级（`ImgSortBox`…）+ 逐处替换 + 验收命令 |

**执行建议（取交集）**：
1. token 值与 Style 键名 **以 MiMo 版为准**（其附录 B 键名清单可直接用，本版引用同一命名，避免双轨）。
2. 扩展/Web 对齐规则参考 TraeDesign 版；但**仅限 popup 对齐**（Web 前端视觉不在本版执行范围，若需实施另立任务）。
3. **文件级改动顺序以本版 §5–§6 为准**——它把前两版的「目标态」翻译成了 0.36 代码上的具体动作。
4. 三份冲突时：**token 值听 MiMo、落地顺序听本版、扩展规范听 TraeDesign**，最终以用户真机验收为准。

---

## 8. 验收总清单（发版前打勾）

- [ ] `Grep '#[0-9A-Fa-f]{6}' src-native/*.xaml` 仅剩 `App.xaml` 资源定义处（零业务硬编码）
- [ ] 字号仅落在 10/11/12.5/13.5/15/17/24
- [ ] 每屏 Primary ≤ 1；所有删除类按钮为 Danger
- [ ] CheckBox/滚动条/右键菜单/ToolTip 均为深色自定义样式
- [ ] 五个空态均有「主句 + 行动引导」
- [ ] 多选、筛选、分页在 1280×800 与 1920×1080 均不溢出
- [ ] 查看器、输入框、编辑窗、选择器风格统一（自绘 Shell）
- [ ] 扩展 popup 色值与主程序一致
- [ ] 焦点环可见；Tab 可走通主路径
- [ ] **性能回归**：图片库加载/翻页/多选在 0.37 前后耗时无劣化（红线：FlowWrapPanel、虚拟化、卡片尺寸未动）
- [ ] 功能回归：搜索、分页、多选删除、识别、打标、换封面、合并、设置保存全部通过
- [ ] 改 XAML 后清理 obj/bin 再编译；发布版号遵循纯数字且与历史不同

---

## 9. 明确拒绝（Anti-Patterns）

- 不做玻璃拟态、发光描边、大面积渐变（违背用户「Windows 厚重感」诉求，且 WPF 性能不友好）。
- 不给每个设置项套阴影卡片（卡中卡）。
- 不为了「好看」加插画空态大图——本地工具空态指向动作。
- 不在升级中顺手改 SQL、识别、HTTP、扩展协议（已有 imgtag 融合计划另线推进）。
- 不引入与现有虚拟化不兼容的自适应卡片高度。
- 不引入 Docker、不引入重量级 UI 框架、不引入 Web 前端重构到本线。

---

## 附录 A · 文档归属指纹（防混淆）

| 项 | 值 |
|----|-----|
| 作者标签 | `Doubao-Agent` |
| 唯一文件名 | `UI视觉升级计划-Doubao-工程落地版.md` |
| 排序标识 | **第三版**（另两份：TraeDesign 版、MiMo 版） |
| 风格指纹 | 0.36 基线；64/18/18 硬编码统计；控件级清单；0.37/0.38/0.39 纯数字批次；验收命令可执行 |
| 易混淆对象 | `UI视觉升级计划.md`（TraeDesign，三线规范，`#0b1120` 体系）；`UI视觉升级计划-MiMo-桌面WPF主线.md`（MiMo，WPF 组件体系，`#5B8DEF` 体系） |

---

**总结**：本文件是 Doubao 针对 PictureButler 的**第三份** UI 视觉升级计划，定位为工程落地版——在 0.36.0 源码上给出「先改 `App.xaml` 补 token，再清 64+18+18 处硬编码，再拆按钮主次与空态，最后统一弹窗/动效/扩展」的三批执行路径。与前两份不冲突、可叠加：**规范听 TraeDesign、组件听 MiMo、落地顺序听本版**。若同目录文件并存，以本附录指纹区分，勿合并覆盖。
