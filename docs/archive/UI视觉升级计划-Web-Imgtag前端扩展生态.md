---
AIGC:
    Label: "1"
    ContentProducer: 001191440300708461136T1XGW3
    ProduceID: 42dfa2fcd38ff5bf05e1580992a0bf7f_81def6acaf4011f18f50525400aeaaa3
    ReservedCode1: ASX5g0O6une4rE90HUH8XI33t6IgVZyZr2T2p+8lrDlO4Pho1JFZZrG35evnFL7bHRo+SUNZeFgGy5AbhzSwmRaroxRDCOCGHEo4yiIxCI/7SW2pTPg7WtqFyHfXnVlSYH1ikaGEfZpnmDmhPTTMZX4qiblaViFRYSCXV4epw+ZMA4ObmgPd5Z/92SY=
    ContentPropagator: 001191440300708461136T1XGW3
    PropagateID: 42dfa2fcd38ff5bf05e1580992a0bf7f_81def6acaf4011f18f50525400aeaaa3
    ReservedCode2: ASX5g0O6une4rE90HUH8XI33t6IgVZyZr2T2p+8lrDlO4Pho1JFZZrG35evnFL7bHRo+SUNZeFgGy5AbhzSwmRaroxRDCOCGHEo4yiIxCI/7SW2pTPg7WtqFyHfXnVlSYH1ikaGEfZpnmDmhPTTMZX4qiblaViFRYSCXV4epw+ZMA4ObmgPd5Z/92SY=
---

# PictureButler UI 视觉升级计划（Web · imgtag 前端 + 浏览器扩展生态）

| 字段 | 内容 |
|------|------|
| **文档归属** | Marvis（本会话）独立产出，**非** `UI视觉升级计划-MiMo-桌面WPF主线.md` 亦非 `UI视觉升级计划.md`（TraeDesign） |
| **文件名** | `UI视觉升级计划-Web-Imgtag前端扩展生态.md`（本文件） |
| **版本** | v1.0 |
| **日期** | 2026-09-13 |
| **范围** | imgtag Web 前端（Vue 3 + Tailwind CSS） + 浏览器扩展（Manifest V3） |
| **排除** | WPF 原生桌面端、图片查看器、桌面设置页、数据库、识别、性能 |
| **关系** | 与 MiMo（WPF）、TraeDesign（全平台）并行；本计划**专注 Web 生态**，为 TraeDesign 的全平台统一提供 Web 线详细方案 |

> **防混淆说明**：若同目录下存在其他 agent 生成的升级计划，请以文件名与上表「文档归属」为准。本文件仅服务 `imgtag` Web 前端与浏览器扩展的视觉升级，不覆盖 WPF 桌面端。

---

## 0. 身份与定位

**产品视觉设计师 + 前端架构视角**：imgtag Web 前端是用户**最高频**的入口——图片浏览、标签筛选、人物管理、批量操作都在这里。浏览器扩展是**最轻量**的入口——用户通过它快速存取提示词。两条线需要各自的精致感，但也要有统一的「产品气质」。

| 维度 | 判断 |
|------|------|
| 风格锚点 | **Linear / Notion / Raycast** 的现代工具感：克制、紧凑、暗色优先 |
| 氛围词 | 专业、冷静、高效、可信赖 |
| 反锚点 | 不要「电商风」、不要「社交相册」、不要「AI 花哨插件」 |
| 一句话目标 | 用户打开 imgtag 后**10 秒内找到图片、看清状态、一键操作** |

### 0.1 现状体检（基于 `index.html` 源码）

**做得对的**

- 已有完整暗色主题基础色（`#0b1120` / `#151b2c` / `#1e2a47`），暗色基调统一。
- 三栏布局（文件夹 / 标签人物 / 图片网格）信息架构合理。
- 状态角标有「已标记 / 未标记 / 失败」语义。
- 搜索框 + 筛选条已有雏形。

**视觉问题清单**

| 优先级 | 问题 | 现状证据 | 用户感知 |
|--------|------|----------|----------|
| P0 | 色彩系统双轨 | Tailwind 内置色板（`bg-gray-900`）与 `:root` 变量（`--bg: #0b1120`）混用，`accent: #3b82f6` 多处硬编码 | 主题切换困难，品牌色不统一 |
| P0 | Emoji 图标泛滥 | 导航、按钮、状态大量使用 Emoji（📁🏷👤✨⚙） | 跨平台显示不一致，无法随主题变色 |
| P1 | 圆角/阴影/边框不统一 | 卡片圆角 8/10/14px 并存，按钮 6/8/10px，阴影只在 hover 出现 | 视觉「毛刺」，质感碎片化 |
| P1 | 信息密度过高 | 顶部状态栏 7 组数字 + 2 下拉 + 3 按钮，换行 | 「按钮海」，找不到主操作 |
| P1 | 字号与层级混乱 | 10px ~ 18px 混用，text-xs/text-sm/硬编码 font-size 混用 | 视觉无节奏 |
| P1 | 空态 / 错误态弱 | 仅一行灰字；错误无重试引导 | 不知道下一步点哪 |
| P2 | 图片比例问题 | 固定 200px 高度，竖图被过度裁剪 | 图片不完整 |
| P2 | 扩展弹窗简陋 | 340px 固定宽，连接状态仅文字，无图标 | 「临时工」质感 |
| P2 | 动效几乎为零 | 无 hover/点击过渡，模态无过渡 | 交互「死板」 |
| P3 | 可访问性缺失 | 大量按钮无 aria-label，无焦点态，对比度不足 | 屏幕阅读器不可用 |

---

## 1. 设计原则

1. **图片优先**：任何装饰不得抢缩略图、人脸、标签的注意力。
2. **一套令牌**：所有颜色、字号、间距、圆角只允许引用 CSS 变量；禁止 HTML 内联样式。
3. **操作有主次**：每屏最多 1 个 Primary，0–2 个 Secondary。
4. **状态可读**：加载 / 空 / 错误 / 部分成功必须有结构化视觉。
5. **密度可调**：默认紧凑，预留宽松选项。
6. **不动功能契约**：按钮 Click 路径、搜索逻辑、筛选规则、WebSocket 通信一律不改；只改呈现层。

---

## 2. Design Tokens（视觉地基）

### 2.1 色彩系统

```css
:root {
  /* === 背景 === */
  --pb-bg-deep:    #060a12;    /* 最深背景（canvas、模态遮罩） */
  --pb-bg-base:    #0b1120;    /* 页面底色（Web 端）/ 扩展底（对齐 Web） */
  --pb-bg-surface: #151d2e;    /* 卡片 / 面板 / 抽屉 */
  --pb-bg-elevated:#1c2541;    /* 浮层 / popover / dropdown */
  --pb-bg-overlay: rgba(0,0,0,0.72);
  --pb-bg-hover:   rgba(255,255,255,0.06);
  --pb-bg-active:  rgba(255,255,255,0.10);

  /* === 边框 === */
  --pb-border-subtle: rgba(148,163,184,0.08);
  --pb-border-default: rgba(148,163,184,0.14);
  --pb-border-strong:  rgba(148,163,184,0.22);
  --pb-border-focus:   #5b8def;

  /* === 文字 === */
  --pb-text-primary:   #e8eaf2;
  --pb-text-secondary: #9aa3b5;
  --pb-text-tertiary:  #5a6480;
  --pb-text-disabled:  #3a4256;
  --pb-text-inverse:   #0b1120;    /* 反白文字 */

  /* === 品牌 / 主色 === */
  --pb-brand-400: #60a5fa;
  --pb-brand-500: #3b82f6;          /* 主强调色 */
  --pb-brand-600: #2563eb;          /* 悬停 / 强强调 */
  --pb-brand-soft:  rgba(59,130,246,0.10);
  --pb-brand-dim:   rgba(59,130,246,0.25);

  /* === 功能色 === */
  --pb-success-400: #4ade80;
  --pb-success-500: #22c55e;
  --pb-success-soft: rgba(34,197,94,0.10);

  --pb-warning-400: #fbbf24;
  --pb-warning-500: #f59e0b;
  --pb-warning-soft: rgba(245,158,11,0.10);

  --pb-danger-400: #f87171;
  --pb-danger-500: #ef4444;
  --pb-danger-soft: rgba(239,68,68,0.10);

  --pb-info-400: #38bdf8;
  --pb-info-500: #0ea5e9;

  /* === 状态徽章色（保持现有语义） === */
  --pb-badge-done-bg:   rgba(34,197,94,0.12);
  --pb-badge-done-text: #22c55e;
  --pb-badge-pending-bg: rgba(245,158,11,0.12);
  --pb-badge-pending-text: #f59e0b;
  --pb-badge-failed-bg:  rgba(239,68,68,0.12);
  --pb-badge-failed-text: #ef4444;
}
```

**用法硬规则**
- 强调色**不**做大面积背景；内容区底永远用 `--pb-bg-surface`。
- 同一视图 Primary 按钮不超过 1 个。
- 徽章语义保持「暗=未做 / 亮=已做」。
- 禁止引入第 5 个色相。

### 2.2 字体

```css
--pb-font-sans: "Inter", "PingFang SC", "Microsoft YaHei", "Segoe UI", system-ui, sans-serif;
--pb-font-mono: "JetBrains Mono", "SF Mono", Consolas, monospace;
```

字号刻度：

| Token | px | 用途 |
|-------|----|------|
| pb-text-xs | 11 | 计数、辅助说明、标签 |
| pb-text-sm | 13 | 按钮、输入框、列表正文 |
| pb-text-base | 14 | 正文、卡片标题 |
| pb-text-lg | 16 | 侧栏标题、模态标题 |
| pb-text-xl | 20 | 大标题、空态主文案 |
| pb-text-2xl | 24 | 启动页/品牌标题 |

字重：标题 `600`，正文 `400`，按钮 `500`，统计数字 `600`。

### 2.3 间距 / 圆角 / 阴影

| Token | 值 | 用途 |
|-------|-----|------|
| pb-space-xs | 4 | 微间距 |
| pb-space-sm | 8 | 组件内 gap |
| pb-space-md | 12 | 卡片 padding、区块间隙 |
| pb-space-lg | 16 | 内容区边距 |
| pb-space-xl | 20 | 主内容边距 |
| pb-space-2xl | 24 | 大区块分隔 |
| pb-radius-sm | 6 | 输入框、小按钮 |
| pb-radius-md | 10 | 卡片、下拉 |
| pb-radius-lg | 16 | 模态框、Toast |
| pb-radius-full | 9999 | 头像、圆形按钮 |
| pb-shadow-sm | 0 2px 8px rgba(0,0,0,0.2) | 卡片 hover |
| pb-shadow-md | 0 4px 20px rgba(0,0,0,0.35) | 模态框 |
| pb-shadow-lg | 0 24px 60px rgba(0,0,0,0.55) | 弹窗 |
| pb-shadow-toast | 0 8px 24px rgba(0,0,0,0.4) | Toast |

### 2.4 动效

| Token | 值 | 用途 |
|-------|-----|------|
| pb-dur-fast | 120ms | hover / 按下 |
| pb-dur-base | 180ms | 选中、面板显隐 |
| pb-dur-slow | 250ms | Toast 进出、模态打开 |
| pb-ease-out | cubic-bezier(0.2, 0.8, 0.2, 1) | 默认缓动 |

---

## 3. 组件体系

### 3.1 按钮变体

| 变体 | 样式 | 用途 |
|------|------|------|
| Primary | `--pb-brand-500` 底，白字，无描边 | 每屏 ≤1：「＋ 新建」「保存」 |
| Secondary | `--pb-bg-surface` 底，`--pb-border-strong` 边，`--pb-text-primary` 字 | 常规操作 |
| Ghost | 透明底，hover `--pb-bg-hover`，`--pb-text-secondary` 字 | 次要操作 |
| Danger | 透明底，hover `--pb-danger-soft`，`--pb-danger-500` 字 | 删除、取消 |
| DangerFill | `--pb-danger-500` 底，白字 | 确认删除 |
| IconBtn | Ghost + 20px 图标 | 标题栏、卡片角操作 |

### 3.2 输入框

- 搜索框：`--pb-bg-base` 底、`--pb-border-default` 边、`pb-radius-md`、高 40；focus 边 `--pb-border-focus`；左侧 32px 图标槽（search 图标）。
- 文本输入：同底色，focus 同规则。
- 下拉框：统一样式，选中项 `--pb-brand-500`，弹层 `--pb-bg-elevated` + `--pb-border-strong` 边 + 阴影。

### 3.3 卡片家族

| 卡片 | 底色 | 边 | 圆角 | 特征 |
|------|------|----|------|------|
| 图片卡 | `--pb-bg-surface` | 无 → hover `--pb-border-default` | `pb-radius-md` | 图片区域 `object-fit: cover`；文件名 `pb-text-xs` |
| 人物卡 | `--pb-bg-surface` | hover `--pb-border-default` | `pb-radius-md` | 圆头像 + 姓名 + 张数 |
| 标签 Chip | `--pb-brand-soft` | 无 | 全圆 | `--pb-brand-400` 字；选中态 `--pb-brand-500` 底 + 白字 |
| 状态徽章 | 见 §2.1 | 无 | `pb-radius-sm` | 小尺寸，图标 + 文字 |
| 设置分组 | `--pb-bg-surface` | 无 | `pb-radius-lg` | 区块标题 `--pb-brand-400` + `pb-text-sm` SemiBold |

### 3.4 状态徽章

保持「脸 / 标 / 词」或「已标记 / 未标记 / 失败」语义：
- 尺寸：高 20，圆角 `pb-radius-sm`，字 `pb-text-xs` SemiBold。
- 已做：底 `--pb-success-soft`，字 `--pb-success-500`。
- 未做：底 `--pb-warning-soft`，字 `--pb-warning-500`。
- 失败：底 `--pb-danger-soft`，字 `--pb-danger-500`。

### 3.5 导航

- 左侧文件夹栏：选中项背景 `--pb-brand-soft`，左侧 3px `--pb-brand-400` 竖线。
- 中间标签/人物栏：Tab 切换为分段控件，选中态 `--pb-brand-soft` 底 + `--pb-brand-400` 字。
- AND/OR 切换：胶囊分段控件，选中态同品牌。

### 3.6 菜单 / 提示 / 滚动条

| 控件 | 目标 |
|------|------|
| Dropdown | 深色：底 `--pb-bg-elevated`，边 `--pb-border-strong`，字 `--pb-text-primary`，hover `--pb-bg-hover` |
| Tooltip | 深色浮层：底 `--pb-bg-elevated`，边 `--pb-border-subtle`，字 `--pb-text-secondary`，`pb-text-xs` |
| ScrollBar | 宽 8；轨道透明；滑块 `--pb-border-default`，hover `--pb-brand-400`；圆角 999 |
| Loading | 遮罩 `rgba(6,10,18,0.8)`；中心旋转用 `--pb-brand-500` |
| Toast | 右侧滑入，280px 宽；底 `--pb-bg-elevated` + 左 3px `--pb-brand-500` / `--pb-success-500` / `--pb-danger-500` |

### 3.7 模态框 / 对话框

- 圆角 `pb-radius-lg`，背景 `--pb-bg-surface`，阴影 `pb-shadow-md`。
- 标题栏：`pb-text-lg` SemiBold，右侧关闭按钮 40px 圆形。
- 底部按钮：取消 Secondary，确认 Primary；删除确认确定 DangerFill。

---

## 4. 分界面升级方案

### 4.1 主界面 · 三栏布局

```
┌─────────────────────────────────────────────────────────┐
│ 工具栏 56px: [搜索框] [排序] [状态] [来源] [筛选芯片]  │
├───────┬───────────────────────┬─────────────────────────┤
│ 文件夹│ 标签 / 人物           │ 图片网格 (响应式)       │
│ 栏 260│ 栏 260                │                         │
│       │                       │                         │
│       │                       │                         │
└───────┴───────────────────────┴─────────────────────────┘
```

### 4.2 顶部工具栏（P0，解决按钮海）

精简为两行：

**行1（48px）**：面包屑（当前路径/人物名） + 搜索框（宽 560px） + 统计数字（总数 · 已标记 · 待处理）

**行2（40px）**：排序下拉 + 状态下拉 + 来源下拉 + 「全部标记」/「刷新」/「批量」按钮

### 4.3 图片网格

- 默认 4 列，响应式为 3/2/1 列。
- 卡片：`object-fit: cover`，宽高比 4:3 默认，可切换 1:1/16:9。
- 竖图：使用 `object-fit: contain` + `--pb-bg-base` 背景填充，避免裁剪。
- hover：整卡 `translateY(-3px)` + `pb-shadow-sm` + 边框 `--pb-brand-400`。
- 选中：`pb-radius-sm` ring 2px `--pb-brand-500`。
- 文件名区：两行截断，`pb-text-xs`。

### 4.4 人物面板

- 头像：1:1 圆形，人脸居中裁剪，`pb-radius-full`。
- 姓名 `pb-text-base` SemiBold，照片数 `pb-text-xs` `--pb-text-tertiary`。
- 批量操作栏：固定在面板底部，左侧已选数量，右侧图标按钮。

### 4.5 标签面板

- 芯片行高 28px，圆角全圆，`--pb-brand-soft` 底。
- AND/OR 切换：胶囊分段控件。
- Top200 时顶部说明「显示最常用 200 / 共 N」`pb-text-xs`。
- 搜索无结果：空态教「换个词或清空」。

### 4.6 详情模态框

- 最大宽度 1200px，圆角 `pb-radius-lg`。
- 左侧图片区：深色背景，图片最大化，操作按钮浮于底部。
- 右侧信息面板：固定 320px，分组展示（文件信息、描述、标签、人脸）。
- 关闭按钮：右上角 40px 圆形图标。

### 4.7 空状态与加载

**空状态三件套**：
1. SVG 插画（暗色主题，简约线条）
2. 主文案 `pb-text-xl`
3. 行动按钮 `Primary`

**加载骨架**：网格 skeleton（4 列 × N 行 shimmer），而非简单文字。

### 4.8 批量操作

- 进入批量模式时顶部固定操作栏，左侧已选数量，右侧操作按钮。
- 操作完成后自动退出批量模式。
- 批量删除：危险操作使用 Danger 按钮，二次确认文案突出影响范围。

### 4.9 图片查看器（嵌入页）

- 画布 `--pb-bg-deep`。
- 左右切换按钮：48px 圆形半透明浮层，hover 显示。
- 底部信息条：居中 `当前 / 总数`，左侧返回，右侧操作菜单。
- 人脸框：2px `--pb-brand-500` 描边，小标签显示人名。

---

## 5. 浏览器扩展弹窗

扩展是用户高频入口，虽小但需精致。

### 5.1 整体

- 宽度 360px（从 340px 放宽），最大高度 480px。
- 背景 `--pb-bg-surface`，字体 `--pb-font-sans`，字号 13px 基准。
- 顶部 header 48px：左侧 Logo 24px + 标题，右侧连接状态 pill。

### 5.2 连接状态

- **已连接**：绿色圆点 + 「已连接」pill，底 `--pb-success-soft`。
- **未连接**：红色圆点 + 「未启动」pill，底 `--pb-danger-soft`，下方提示「启动 PictureButler 后重试」。

### 5.3 搜索与列表

- 搜索框：高 34px，内嵌 search 图标，placeholder `--pb-text-tertiary`。
- 刷新按钮：32px 图标按钮，与搜索框等高。
- 列表项：高 64px，左侧缩略图 48px 圆角 `pb-radius-sm`，右侧标题 14px SemiBold，内容 12px 两行截断。
- 列表项 hover：底 `--pb-bg-elevated`，左侧 `arrow-right` 图标。
- 星标：用 `star` 图标替代字符 `★`。

### 5.4 写入反馈

- 成功：顶部绿色 Toast，「已写入：提示词标题」。
- 失败：顶部红色 Toast，明确失败原因。

### 5.5 底部

- 底部 footer 放「启动 PictureButler」主按钮，减少用户寻找成本。

---

## 6. 交互体验升级

1. **搜索体验**：focus 时显示最近搜索/热门标签；结果高亮匹配关键词。
2. **筛选体验**：已选筛选用 chip 行展示，可逐个移除或一键清除。
3. **键盘导航**：方向键移动焦点、Enter 打开、Space 多选、Ctrl+A 全选、Esc 退出。
4. **动效**：按钮 hover 150ms、卡片 hover 200ms、模态打开 250ms、Toast 滑入 250ms。
5. **响应式**：支持 1280/1024/768/480px 断点。

---

## 7. 可访问性底线

- 正文对比度 ≥ 4.5:1；徽章/UI ≥ 3:1。
- 焦点环：2px `--pb-brand-400` 外描边，offset 1。
- 动效遵循系统「减弱动画」时长设为 0。
- 所有图标按钮设置 `aria-label`。
- 危险操作二次确认。

---

## 8. 分阶段实施

### 阶段 W0 — Token 收敛（约 0.5–1 天）

**目标**：改主题只改一处；消灭双轨色值。

1. 整理 `index.html` 中 `:root` 完整 token：色/字/距/圆角/动效。
2. 全量替换内联样式和 Tailwind 硬编码色类为 CSS 变量。
3. 字号、间距替换到刻度表。
4. **不改**布局结构、不改 Click。

**验收**：CSS 无硬编码 `#`；Web 端打开、切换页签、打开模态无视觉回归。

### 阶段 W1 — 组件与图标（约 1–1.5 天）

**目标**：按钮主次、卡片家族、图标替换。

1. 引入 Lucide 图标库，替换所有 Emoji 图标。
2. 按钮变体拆分（Primary/Secondary/Ghost/Danger）。
3. 图片卡 hover/选中/占位统一。
4. 下拉框、Tooltip、滚动条深色化。
5. 模态框统一圆角与阴影。

**验收**：图片库页一眼能看出「多选 / 识别 / 打标 / 删除」层级；右键与主 UI 同气质。

### 阶段 W2 — 布局与空态（约 1–1.5 天）

**目标**：工具栏两行化、空态三件套、详情模态框。

1. 工具栏重排（行1 面包屑/搜索/统计 / 行2 筛选/操作）。
2. 各空态与错误态。
3. 详情模态框重构（左右分栏）。
4. Toast 规范落地。

**验收**：1280×800 下工具栏不换行；空态可点出主行动。

### 阶段 W3 — 扩展与动效（约 0.5–1 天）

1. 扩展弹窗精致化（宽度/间距/图标/状态 pill）。
2. 全局动效（hover/modal/toast 120–250ms）。
3. 键盘导航完善。
4. 响应式断点适配。

**验收**：扩展与 Web 端截图并排「像同一产品」。

---

## 9. 验收清单

- [ ] CSS 零硬编码色值（仅 CSS 变量）
- [ ] 字号仅落在 11/13/14/16/20/24
- [ ] 每屏 Primary ≤ 1；删除类均为 Danger
- [ ] Dropdown/Tooltip/ScrollBar 深色一致
- [ ] 空态均含主句 + 行动
- [ ] 工具栏在 1280×800 不溢出
- [ ] 模态框风格统一
- [ ] 扩展弹窗与 Web 端色值一致
- [ ] 焦点环可见；Tab 可操作
- [ ] 不改任何后端逻辑、WebSocket、搜索算法

---

## 10. Decision Trace

| 决策 | 原因 | 替代方案 | 权衡 |
|------|------|----------|------|
| 在现有暗色体系上演进 | 用户已有暗色记忆；换亮色成本高收益低 | 全面切换亮色 | 视觉「刷新感」不如换主色强，靠层级与细节 |
| 品牌色保持 `#3b82f6` | 选中/焦点/主按钮已绑定蓝系 | 换 `#2dd4bf` Teal | 与部分 AI 工具「青绿潮流」不够贴，但更像可靠工具 |
| 图标库选 Lucide | MIT 协议、风格简洁、体积可控 | Heroicons / Remix Icon | Lucide 与 Linear/Raycast 风格最接近 |
| 图片比例可切换 | 竖图被裁剪是长期痛点 | 固定 4:3 | 增加一点交互复杂度 |
| 扩展宽度放宽到 360px | 340px 长内容截断 | 保持 340px | 多 20px 不影响弹窗定位 |

---

## 11. Anti-Patterns（明确拒绝）

- 不要给每个设置项都套阴影卡片（卡中卡）。
- 不要渐变大底、发光描边、大面积模糊（Web 端虽无性能顾虑，但风格不搭）。
- 不要主按钮满天飞。
- 不要空态只写「暂无数据」。
- 不要为了「好看」加插画大图——本地工具空态应指向动作。
- 不要在升级中顺手改后端逻辑。

---

## 附录 A · 与已有计划的映射

| 已有计划 | 本计划的关系 |
|----------|--------------|
| `UI视觉升级计划-MiMo-桌面WPF主线.md`（MiMo） | MiMo 覆盖 WPF 桌面线；本计划覆盖 Web + 扩展线，**不冲突** |
| `UI视觉升级计划.md`（TraeDesign） | TraeDesign 是全平台统一视角；本计划是 TraeDesign 的 Web 线**详细方案**，可被 TraeDesign 引用 |
| `imgtag融合计划.md` | 融合计划侧重 Rust cdylib 集成；本计划只关注视觉呈现，不碰集成 |

---

## 附录 B · 建议的 CSS 变量键名清单

```
--pb-bg-deep, --pb-bg-base, --pb-bg-surface, --pb-bg-elevated,
--pb-bg-overlay, --pb-bg-hover, --pb-bg-active,
--pb-border-subtle, --pb-border-default, --pb-border-strong, --pb-border-focus,
--pb-text-primary, --pb-text-secondary, --pb-text-tertiary, --pb-text-disabled,
--pb-brand-400, --pb-brand-500, --pb-brand-600, --pb-brand-soft, --pb-brand-dim,
--pb-success-400, --pb-success-500, --pb-success-soft,
--pb-warning-400, --pb-warning-500, --pb-warning-soft,
--pb-danger-400, --pb-danger-500, --pb-danger-soft,
--pb-info-400, --pb-info-500,
--pb-badge-done-bg, --pb-badge-done-text, --pb-badge-pending-bg,
--pb-badge-pending-text, --pb-badge-failed-bg, --pb-badge-failed-text,
--pb-font-sans, --pb-font-mono,
--pb-text-xs, --pb-text-sm, --pb-text-base, --pb-text-lg, --pb-text-xl, --pb-text-2xl,
--pb-space-xs, --pb-space-sm, --pb-space-md, --pb-space-lg, --pb-space-xl, --pb-space-2xl,
--pb-radius-sm, --pb-radius-md, --pb-radius-lg, --pb-radius-full,
--pb-shadow-sm, --pb-shadow-md, --pb-shadow-lg, --pb-shadow-toast,
--pb-dur-fast, --pb-dur-base, --pb-dur-slow, --pb-ease-out
```

---

## 附录 C · 文档归属指纹（防混淆）

| 项 | 值 |
|----|-----|
| 作者标签 | `Marvis-Agent` |
| 唯一文件名 | `UI视觉升级计划-Web-Imgtag前端扩展生态.md` |
| 风格指纹 | 强调 Linear/Notion/Raycast；品牌色保持 `#3b82f6`；图片比例可切换；W0–W3 阶段名；Lucide 图标库 |
| 易混淆对象 | `UI视觉升级计划-MiMo-桌面WPF主线.md`（MiMo，WPF 线）；`UI视觉升级计划.md`（TraeDesign，全平台线） |
| 与 TraeDesign 的关系 | 本计划是 TraeDesign 全平台计划中 **Web 线的详细方案**，可被直接引用 |

---

**总结**：本文件是 Marvis 针对 **imgtag Web 前端 + 浏览器扩展** 的视觉升级蓝图——「把现有的暗色工具界面收成系统」，不是换皮重做。先 token、再组件、再布局、再细节；视觉只动呈现层。本计划可被 TraeDesign 的全平台计划引用作为 Web 线的详细方案。
*（内容由AI生成，仅供参考）*
