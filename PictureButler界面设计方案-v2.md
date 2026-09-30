# PictureButler 界面设计方案 v2 ·「厚度与克制」

| 字段 | 内容 |
|---|---|
| **定位** | PictureButler 桌面端（WPF）**信息架构级**界面设计方案 |
| **基线** | `src-native` 实测：App.xaml 32 个画刷 / 9 档字号 / 6 档间距 / 5 档圆角 / 3 档时长；8 个业务窗口 XAML 硬编码已归零 |
| **与旧计划的关系** | `UI视觉升级计划-执行版.md` 负责 **0.45–0.49 的 Token 收敛与控件质感，已完成**；本方案负责它之后的问题，两者不冲突、**同时有效** |
| **交付物** | ① 本文件（规范）② `PictureButler界面原型-v2.html`（可交互高保真原型） |
| **排除** | 识别/检索/数据库逻辑、承载的性能结构、安装包与官网 |
| **已锁定决策**（2026-09-29 用户拍板） | ① **引入浅色主题** ② **侧栏图标化** ③ **不做列表视图**（网格单一presentation） |
| **落地状态** | 批次 1 + 2a + 4 → **0.60.0**；批次 2b + 3 → **0.61.0**；批次 2c（多选态替换工具条 / 标题栏 36px / 筛选行 44px）→ **0.61.2**。批次 2a/2b/4 仍待真机逐项走查 |

---

## 0. 一句话结论

> **不要重画皮肤，要重做骨架。**

0.45–0.49 已经把「颜色 / 字号 / 控件质感」做干净了。现在肉眼能看出来的问题，没有一个是控件长得不好看，而是**信息位置不对**：

| 现状 | 症状 | 本质 |
|---|---|---|
| 侧栏 5 项纯文字 | 扫视成本高三倍，导航项之间分不出主次 | 缺少**图形锚点** |
| 图片库工具条三行堆叠（搜索 / 页签 / 筛选） | 内容区被压掉近 90px，密度扑面而来 | 缺少**层级与默认态** |
| 设置页长条平铺 | 找不到某一项，只能滚 | 缺少**二级导航** |
| 任务进度只靠 Toast | 跑完就消失，回来看不到「刚才那批有没有完成」 | 缺少**常驻状态位** |
| 想做浅色主题 | `BgBrush`/`PanelBrush` 直接写死深值，没法切 | 缺少**语义层** |

这五条就是本方案要改的全部东西。**色相、字号档位、动效时长一律不动。**

---

## 1. 设计原则

沿用旧计划七条原则的前六条（厚重感 / 图优先 / 一套 token / 操作有主次 / 状态可读 / 不动功能契约），新增两条：

7. **位置比样式重要**：能靠「换个位置」解决的问题，不要靠「加个颜色」解决。
8. **默认态必须是对的**：90% 时间屏幕应该处在信息最少、干扰最小的那个状态；多余的面板默认收起，而不是默认展开。

---

## 2. 信息架构：五区 Shell

```
┌────────────────────────────────────────────────────────────────┐
│ ① 标题栏 h=36   品牌 · 版本胶囊                    ⌄ ☐ ✕        │
├───────────┬────────────────────────────────────────────────────┤
│           │ ② 上下文工具条 h=48   标题 搜索 │ 主操作 次操作      │
│ ③ 侧栏    ├────────────────────────────────────────────────────┤
│ w=216     │ ③ 筛选行 h=44（**默认收起**，内联芯片，非弹层）      │
│ 图标+文字 ├────────────────────────────────────────────────────┤
│ 服务状态  │                                                    │
│ 版本号    │              ④ 内容画布（三级底：画布/面板/凹槽）    │
│           │                                                    │
├───────────┴────────────────────────────────────────────────────┤
│ ⑤ 状态条 h=28   计数 · 已选 · 任务进度 · 8189 服务状态           │
└────────────────────────────────────────────────────────────────┘
```

### 2.1 五区职责与硬指标

| 区 | 高度/宽度 | 职责 | 硬指标 |
|---|---|---|---|
| ① 标题栏 | 36px 通栏 | 品牌识别、窗口操作 | 品牌区 ≥ 44px 命中宽；版本胶囊必须常显 |
| ② 工具条 | 48px，随页更换 | 页面标题 + 该页主操作 | **每屏 Primary ≤ 1、Secondary ≤ 2，其余 Ghost** |
| ③ 筛选行 | 44px，**默认收起** | 状态/排序/来源三段内联芯片 | 展开后工具条 + 筛选行合计 ≤ 92px |
| ④ 内容画布 | 自适应 | 唯一的"内容"发生地 | 不得出现第四个信息层 |
| ⑤ 状态条 | 28px 常驻 | 数量、选择、任务、服务 | 任何进行中的任务都必须在这里有痕迹 |

### 2.2 侧栏：把「纯文字」换成「图标 + 文字」

| 现状 | v2 |
|---|---|
| NavBtn 仅文字，选中态 = 实底 `AccentDimBrush` 填充 | 图标 20px + 文字 13.5px；选中态 = **左侧 3px accent 竖条 + 柔和蓝底 + 图标/文字转 accent 色** |

选中态从「一大块实底」改成「一条细杠 + 轻底」，是因为实底填充把侧栏切成了五个色块，**比内容还抢眼**——这违反了「图优先」。

**导航项（5 项，顺序不变）**

| 项 | 图标语义 | 说明 |
|---|---|---|
| 提示词 | 气泡 + 文本线 | 一级，带总数角标 |
| 图片库 | 四宫格 | 一级，**带红点角标**（待入库/识别失败数） |
| 人物 | 头像圆 + 肩弧 | 二级，从图片库进入的实体页，独立成项便于直接回来 |
| 标签 | 标签 | 二级 |
| 设置 | 齿轮 | 一级，固定侧栏底部上方 |

侧栏底部两行：**服务状态胶囊**（就绪 Success / 启动中 灰 / 异常 Danger）+ **版本号**（`TypeCaption`）。

### 2.3 工具条 vs 多选态

多选不是"在工具条上再加一行"，而是**整条工具条的替换**：

| 常态 | 多选态（同一 48px 槽位） |
|---|---|
| 标题 + 搜索 + 主操作组 | `已选 3 张`（accent SemiBold）+ 全选 / 反选 / 移出人物 / 导出 + **删除（Danger）** + 取消（Ghost） |

好处：高度零跳动，且多选态天然满足「本屏 Primary = 完成本次多选目的」。

---

## 3. 令牌体系 v2：三层结构

### 3.1 为什么必须是三层

现状的问题一句话：**App.xaml 里的键是「名字」而不是「语义」**。

- `BgBrush` `#1B1E28` —— 名字叫"背景"，值是**深色**的具体值
- `PanelBrush` `#20242F` —— 同样是深色具体值
- `BorderBrush` `#333A4D` —— 深色具体值

要在浅色下复用这套键，只能把每个键的值改掉。这就是「改值即全量回归」，等于每次换主题都要重做一遍视觉验收。**所以浅色主题一直做不动，不是工作量问题，是架构问题。**

### 3.2 三层定义

```
原语层 Primitive   只存色值，业务永不直接引用     #141922 / #5C6779 / #2F6BDB …
      ↓ 引用
语义层 Semantic    描述"用在哪"，随主题换整份      surface.canvas / text.primary / action.primary
      ↓ 别名
兼容层 Legacy      现有 32 个 XxxBrush，保持可用   BgBrush = surface.canvas（深色）
```

**落地**：迁移成本 = `App.xaml` 里原本的 32 行 `<SolidColorBrush>`，改为：

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="Themes/Primitive.xaml" />   <!-- 永不切换 -->
      <ResourceDictionary Source="Themes/Semantic.Dark.xaml" /> <!-- 切换这一份 -->
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

现有业务 XAML 的 `{StaticResource AccentBrush}` 一行不用改——兼容层在 `Semantic.Dark.xaml` 里把它指向 `action.primary`。

```csharp
// 切主题：整个字典替换，禁止逐个 Brush SetValue（一定会漏）
var target = dark ? "Themes/Semantic.Dark.xaml" : "Themes/Semantic.Light.xaml";
Application.Current.Resources.MergedDictionaries[1] =
    new ResourceDictionary { Source = new Uri(target, UriKind.Relative) };
```

> **新增红线 R10**：换主题只能通过替换 `MergedDictionaries[1]`，**禁止**用 `Application.Current.Resources["BgBrush"] = …` 逐个赋值。

### 3.3 语义 Token 全表（深浅双值）

**中性 / 表面**

| Semantic | Dark（= 现值，零回归） | Light | 用途 |
|---|---|---|---|
| `surface.canvas` | `#1B1E28` | `#F2F4F7` | 应用最底背景 |
| `surface.titlebar` | `#161A24` | `#FFFFFF` | 标题栏 / 侧栏底 |
| `surface.panel` | `#20242F` | `#FFFFFF` | 面板、菜单、弹窗 |
| `surface.panel-alt` | `#232735` | `#F7F8FA` | 分组条、交替行 |
| `surface.sunken` | `#191D28` | `#EFF1F5` | 输入框、卡片"凹槽" |
| `surface.deep` | `#10131A` | `#E8EBF1` | 查看器画布、最深底 |
| `border.default` | `#333A4D` | `#E2E6EC` | 1px 分隔线 |
| `border.strong` | `#3A4256` | `#C6CDD8` | 强描边、滚动条滑块、徽章未做底 |
| `interaction.hover` | `#2A3040` | `#F0F2F6` | hover 底 |
| `interaction.selected` | `#242E44` | `#E3ECFC` | 卡片/列表选中底 |
| `interaction.selected-strong` | `#2C4272` | `#CFE0FA` | 合并/选择列表强选中 |

**文本**

| Semantic | Dark | Light | 说明 |
|---|---|---|---|
| `text.primary` | `#E8EAF2` | `#141922` | 正文、标题 |
| `text.secondary` | `#9AA3B5` | `#5C6779` | 次要信息 |
| `text.tertiary` | `#5A6480` | `#6B7688` | 极弱／禁用（见 §7.2 对比度告警） |
| `text.on-accent` | `#FFFFFF` | `#FFFFFF` | 实底主按钮上的字 |

**动作与状态**

| Semantic | Dark | Light | 说明 |
|---|---|---|---|
| `action.primary` | `#5B8DEF` | `#2F6BDB` | **用作文字/图标/描边**的对角色 |
| `action.primary-hover` | `#74A3FF` | `#4C83EA` | |
| `action.primary-press` | `#3D5AA0` | `#2559BE` | |
| `action.primary-soft` | `#233459` | `#E8F0FE` | 已加入筛选的芯片底 |
| **`action.primary-solid`** | **`#2E5FC0`（0.60.0 实装）** | `#2F6BDB` | **实底按钮的填充色**（见 §7.2） |
| **`action.primary-solid-hover`** | **`#3D70D6`** | `#2559BE` | hover 也要 ≥4.5，见下方「hover 方向」说明 |
| **`action.primary-solid-press`** | **`#26489B`** | `#1E4A9E` | |

> **hover 方向（实现时补的规则）**：深色主题下 hover 朝**更亮**走、浅色主题下 hover 朝**更深**走。
> 两条路都是在「提升与页面底的对比」，同时保证实底按钮上的白字始终 ≥ 4.5:1。
> 这条约束反过来决定了 `action.primary-solid` 的最终取值：先前候选 `#3F6FD1` 自身 4.8:1 达标，
> 但它一旦再提亮到标准 hover 色，白字就掉到 4.06:1。因此把基色压深到 `#2E5FC0`（5.97:1），
> 留出 hover 提亮的空间。
| `status.success` | `#3D9A6A` | `#1A7A4D` | 人脸已识别 |
| `status.success-soft` | `#123524` | `#E2F3EA` | |
| `status.info` | `#4A6FD4` | `#2F5AC4` | AI 已打标 |
| `status.info-soft` | `#1C2745` | `#E7EEFC` | |
| `status.warn` | `#C77D2E` | `#8F5B0E` | 提示词关联、入库提醒 |
| `status.warn-soft` | `#332A1B` | `#FAF0DE` | |
| `status.danger` | `#E5484D` | `#D33A40` | 删除、失败 |
| `status.danger-soft` | `#3B1D12` | `#FBE9EA` | |
| **`status.success-text`** | **`#4EB47C`（新增）** | — | 深色下 success 文字/小图标专用 |
| **`status.info-text`** | **`#7E9DF6`（新增）** | — | 同上 |
| **`status.danger-text`** | **`#F26A6F`（新增）** | — | 同上 |
| `content.star` | `#F5B84B` | `#E0A020` | |

> 三个 `-text` 变体不是"多一个颜色"，而是**同一个语义的两个数值**：当它作为**填充/图标/边框**时用基色，当它作为**文字**时用 `-text` 变体。这是语义层存在的意义。

**遮罩**（查看器）

| Semantic | Dark | Light |
|---|---|---|
| `overlay.scrim` | `rgba(16,19,26,.69)` | `rgba(16,20,28,.58)` |
| `overlay.bar` | `rgba(22,26,36,.60)` | `rgba(255,255,255,.86)` |
| `overlay.chip` | `rgba(16,19,26,.80)` | `rgba(255,255,255,.90)` |

---

## 4. 排版与空间（**沿用现状，仅补三处**）

### 4.1 字号七档 + 两处命名例外（不动）

| Token | px | 用途 |
|---|---|---|
| `TypeMicro` | 10 | 角标计数、极弱注释 |
| `TypeCaption` | 11 | 说明、路径、次要元数据 |
| `TypeBody` | 12.5 | 正文、按钮文案 |
| `TypeBodyLg` | 13.5 | 列表主文案、卡片标题 |
| `TypeTitle` | 15 | 弹窗标题、区块标题 |
| `TypeSection` | 17 | 查看器/设置大标题 |
| `TypeDisplay` | 24 | 空态主句（**仅空态**） |
| `TypeBadgeGlyph` | 8.5 | 图片卡「脸/标/词」单字徽章 |
| `TypeNavGlyph` | 30 | 查看器 ‹ › 大箭头 |

补充三条**执行约束**（现在代码里有漂移）：

1. **行高**：正文 1.65、标题 1.3 —— iOS/Windows 混排时中文会切底，必须显式写死。
2. **字重**：只允许 `Regular` / `SemiBold`；业务文案禁 `Bold`/`Black`（中文粗体在 12.5px 会糊成一团）。
3. **省略号**：所有参与截断的 TextBlock，`TextWrapping=NoWrap` + `TextTrimming=CharacterEllipsis` 必须成对出现，否则长文件名会撑破卡片。

### 4.2 间距六档（不动）

`S1=4` `S2=8` `S3=12` `S4=16` `S5=20` `S6=24` —— 禁 7/10/14/18 等游离值，禁半像素。

### 4.3 圆角五档（不动）

`R1=4`（输入/徽章）`R2=6`（按钮/小卡）`R3=8`（搜索框/主卡片/菜单）`R4=10`（人物卡）`R5=999`（芯片/头像）

### 4.4 新增：控件高度表（现状没有，是本次补的）

| 槽位 | 高度 | 说明 |
|---|---|---|
| 标题栏 | 36 | |
| 工具条 | 48 | |
| 筛选行 | 44 | |
| 按钮 L / M / S | 40 / 32 / 28 | **默认 M=32**；工具条用 M，卡片内用 S |
| 输入框 / 搜索框 | 34 | |
| 芯片 / 分段项 | 26 | |
| 导航项 | 40 | |
| 设置行 | 56 | |
| 状态条 | 28 | |
| 图标按钮 | 32×32 | 命中区不得小于 28×28 |

---

## 5. 组件库

### 5.1 按钮：4 变体 × 3 尺寸

| 变体 | 底色 | 字色 | 边框 | 规则 |
|---|---|---|---|---|
| **Primary** | `action.primary-solid` | `text.on-accent` | 同底 | **每屏 ≤ 1**；不可用 opacity 0.45 |
| **Secondary** | `interaction.hover` | `text.primary` | 1px `border.strong` | 每屏 ≤ 2 |
| **Ghost** | 透明 | `text.secondary` | 0 | hover → `interaction.hover` |
| **Danger** | 透明 | `status.danger-text` | 1px `border.strong` | hover 底 `status.danger-soft` |
| **DangerSolid** | `status.danger` | `#FFFFFF` | 同底 | **仅用于删除流程的最后一步确认** |

落地 Style：`BtnPrimary` / `ActionBtn` / `BtnGhost` / `BtnDanger` / `BtnDangerSolid`（均已存在，只需把 Primary 底改成 `action.primary-solid`，见 §7.2）。

**禁止**：同一排出现两个 Primary；Primary 与非 danger 的 Secondary 同权并排（会让用户不知道该点哪个）。

### 5.2 输入框

- 高 34，圆角 `R3`，底 `surface.sunken`，1px `border.default`
- `Padding=12,0`（不是 12,6 —— 34 高 + 垂直 padding 会把文字挤出中线）
- Focus：`action.primary` 描边 + 全局焦点环（2px，offset 1）
- 搜索框：**带清空 ×（16px，仅 `Text.Length>0` 时可见）**

### 5.3 内联分段芯片（FilterChip）

- 高 26，圆角 `R2`，`Padding=9,4`，`TypeCaption`
- 未选：透明底、`text.secondary`
- 选中：`action.primary-soft` 底 + `action.primary` 1px 边 + `text.primary` 字
- 组标题（`FilterGroupLabel`）：`text.tertiary` `TypeCaption`，右距 6

> **红线 R1 永不放松**：任何情况下筛选区不得出现 `Popup` / `ComboBox` / 下拉弹层（本机实测弹层收不到鼠标输入，会穿透到图片网格误开图片）。「筛选行收起/展开」走的也是内联折叠，不是弹层。

### 5.4 图片卡（168×196，**结构不可改**）

```
┌────────────────────────┐  ← 外框：1px border.default，选中时 1.5px action.primary + 底 interaction.selected
│                        │
│      缩略图 152×140      │  ← 左下角三态徽章 22×22；右上「选中勾」仅在选择态出现
│                        │
├────────────────────────┤
│ 文件名（**固定两行**）   │  ← TypeBody，固定两行 + 截断省略，防卡高跳动（红线 R3）
│ 3.2 MB · 2026-08-14    │  ← TypeCaption，text.tertiary
└────────────────────────┘
```

**三态徽章**（脸 / 标 / 词）：22×22，圆角 `R1`，字 `TypeBadgeGlyph` 8.5

| 状态 | 底 | 字 |
|---|---|---|
| 未做 | `border.strong` | `text.secondary` |
| 已做 | `status.success` / `status.info` / `status.warn` | `#FFFFFF` |

保持中文单字，**不做图标化**——8.5px 的图标不可辨识，而中文单字在同样尺寸下认得出。

### 5.5 人物卡（R4=10）

头像 96×96 圆形（1px `border.default` 描边）+ 姓名 `TypeBodyLg` SemiBold + `N 张` `TypeCaption` `text.tertiary`。
hover：1px `action.primary`；选中：`interaction.selected-strong` + 左侧 3px accent 竖条 **+ 右侧圆形勾选**。

> 深色主题下只改底色看不出选中，这是历史上出过的问题；必须有 ≥ 3 重视觉冗余。

### 5.6 空态 / 加载 / 错误（统一三件套）

| 组成 | 规格 |
|---|---|
| 主句 | `TypeDisplay` 24 SemiBold，`text.primary`，**≤ 12 字** |
| 副句 | `TypeBody` 12.5，`text.secondary`，**≤ 24 字**，说明"为什么空 / 下一步怎么办" |
| 行动 | 1 个 Primary + 至多 1 个 Ghost，动词短语（如「去设置添加文件夹」） |
| 图形 | 64px 线性图标，`text.tertiary`，stroke 1.2 —— **禁止**插画、禁止 emoji |

**文案公式**：主句说状态，副句说原因和出路口。

| 场景 | 主句 | 副句 | 行动 |
|---|---|---|---|
| 库为空 | 还没有照片 | 添加文件夹后自动建立索引 | Primary「添加文件夹」 |
| 筛选无果 | 没有符合条件的照片 | 当前筛选：来源=西藏 · 状态=已识别 | Ghost「清除筛选」 |
| 搜索无果 | 找不到"{关键词}" | 换个关键词，或检查来源文件夹 | Ghost「清除搜索」 |
| 打标失败 | AI 打标中断 | 3 张超时，LM Studio 未响应 | Secondary「重试」+ Primary「检查设置」 |

**加载态**：遮罩 `overlay.scrim` + 居中进度环 + `TypeBody` 文案（不写「加载中…」，写「正在读取 1,284 张 · 已完成 620」）。超过 800ms 才显示遮罩，避免闪一下。

### 5.7 Toast

右上角纵向堆叠，宽 280–360，圆角 `R3`，进/出 240ms，**只动 opacity**（`DurSlow`）。

| 类型 | 左边条 3px | 图标 | 底 |
|---|---|---|---|
| 成功 | `status.success` | ✓ | `surface.panel` |
| 提醒 | `status.warn` | ! | `surface.panel` |
| 错误 | `status.danger` | ✕ | `surface.panel` + `status.danger-soft` 标题区 |
| 进行中 | `action.primary` | 进度环 | `surface.panel` +「在状态条查看」链接 |

单条最多 4 秒，**进行中类型不自动消失**，直到任务结束。

### 5.8 状态条（新增区）

左→右：`1,284 张` · `已选 3` · `AI 打标 进行中 412/1,284 ▓▓▓░░ 32%` · 右：`8189 已就绪 ●`。
进行中任务**必须**在这里留痕，且点击 → 跳设置页对应区块。

### 5.9 右键菜单 / ToolTip（沿用）

`MenuDark` / `MenuItemDark` / `MenuItemDanger` / `ToolTipDark` 全额保留，只把色值指向语义层。

> **红线 R2 补充**：菜单项样式**永远用全局隐式 `Style TargetType="MenuItem"`**，禁用 `ItemContainerStyle`——`MenuBase` 会把 `Separator` 也当容器，套上去直接抛 `InvalidOperationException`（WPF 无弹窗、进程直接退出）。分隔线走 `x:Key="{x:Static MenuItem.SeparatorStyleKey}"`。

---

## 6. 六屏详设（配合原型查看）

### A. 图片库 · 全部照片
- 工具条：标题「全部照片」+ `1,284 张` 计数 ｜ 搜索 240px ｜ 多选(Ghost) · 识别人脸(Secondary) · AI 打标(Secondary) · **添加文件夹(Primary)**
- 筛选行默认**收起**，工具条上有「筛选 · 3」入口（3 = 已生效条件数）
- 正文：168×196 卡片网格，**150px 缩略图 + 固定两行文件名**
- 页脚翻页：上一页 / `第 2 / 26 页`（当前页数字强调色）/ 下一页
- 验收：1280×800 下可见 ≥ 4 列 × 3 行；长文件名不撑破；hover/选中肉眼可分

### B. 图片库 · 人物
- 工具条：标题「人物」+ `48 人` ｜ 多选 · **建议合并(Secondary)** · 扫描人脸(Primary)
- 空态 / 多选态复用同一套规则
- 验收：头像不变形（统一 Cover 裁切）、姓名过长截断为 ellipsis

### C. 图片查看器
- 画布 `surface.deep`，图片居中，最小化 chrome
- 顶栏：文件名 `TypeBodyLg` + 关闭（右上 32×32）
- 左右悬浮导航钮 48×48（浮层 `overlay.bar`，hover 边 `action.primary`），字形 `TypeNavGlyph` 30
- 人脸框：仅在本屏显示 —— 1.5px 描边 + 左上角人名 chip（同样 `TypeCaption`）
- 底栏：缩放条（− ｜ 100% ｜ ＋ ｜ 适应）、元数据行、操作区（**删除独立为 Danger，不与「打开」同权**）
- 快捷键：`←/→` 切换、`Esc` 关闭、`Space` 适应窗口、`Ctrl+滚轮` 缩放

### D. AI 提示词管理
- 双栏：左 300px 列表，右编辑器区
- 左：搜索 + **新建(Primary)** + 列表项（标题 `TypeBodyLg` / 路径 `TypeCaption` tertiary / ★ Star / 更新时间）
- 右未选中时：**空态三件套**（主句「选择一条提示词」/ 副句「左侧点击任意条目开始编辑」）
- 右选中时：标题输入 + 多行正文 + 标签芯片行 + 底部「取消(Secondary) / 保存(Primary)」
- 验收：列表滚动无动效；选中项底色 + 左边框双重标识

### E. 设置（**二级侧栏，本次新增**）
- 内容区改双列：左 180px 二级目录 + 右滚动面板
- 二级目录：常规 / 来源目录 / 人脸识别 / AI 打标 / 索引与缓存 / 关于
- 右面板：每 6–8 行一个分组，组标题 `TypeTitle` SemiBold + 1px 底分隔线
- 「关于」区块底部：**版本号必须常显**（用户靠它确认是否加载了新版）
- 危险区（清理缓存 / 清理空人物 / 移出来源）：整块 `status.danger-soft` 底 + 说明文字 + `BtnDanger`
- 验收：任意一项目录点击即滚动到位；无「卡中卡」嵌套框

### F. 状态演示（原型的右下角浮标）
分别演示：空态 / 加载中 / Toast / 多选态 / 深色↔浅色。

---

## 7. 无障碍实测（本次新增，含三处必须修的问题）

### 7.1 三条硬要求
1. 正文对比度 ≥ **4.5:1**，≥18.66px 粗体或 ≥24px 正文 ≥ **3:1**（WCAG AA）
2. 焦点环：2px `action.primary`，offset 1，**全局挂载**在 `SystemParameters.FocusVisualStyleKey`
3. 命中区：图标按钮 ≥ 28×28，推荐 32×32；列表行 ≥ 32 高

### 7.2 对比度实测（WCAG 相对亮度公式手算，**不是估的**）

**浅色主题**（底 `#FFFFFF`）

| 前景 | 色值 | 比值 | 结论 |
|---|---|---|---|
| text.primary | `#141922` | **17.6:1** | AAA ✓ |
| text.secondary | `#5C6779` | **5.7:1** | AA ✓ |
| text.tertiary | `#6B7688` | **4.6:1** | AA ✓ |
| ⚠ 若沿用常见灰 `#8C96A6` | | 3.0:1 | **AA ✗ 不合格** |
| action.primary | `#2F6BDB` | **4.9:1** | AA ✓ |
| status.success | `#1A7A4D` | **5.5:1** | AA ✓ |
| ⚠ 若用 `#1F8A57` | | 4.3:1 | **AA ✗** |
| status.warn | `#8F5B0E` | **5.7:1** | AA ✓ |
| status.danger | `#D33A40` | **4.7:1** | AA ✓ |
| 白字 on action.primary-solid | on `#2F6BDB` | **4.9:1** | AA ✓ |

**深色主题**（底 `#20242F`）

| 前景 | 色值 | 比值 | 结论 |
|---|---|---|---|
| text.primary | `#E8EAF2` | **13.0:1** | AAA ✓ |
| text.secondary | `#9AA3B5` | **6.1:1** | AA ✓ |
| text.tertiary | `#5A6480` | **2.6:1** | ✗ **仅限装饰/禁用**，不得承载可读信息 |
| action.primary | `#5B8DEF` | **4.8:1** | AA ✓ |
| status.warn | `#C77D2E` | **4.7:1** | AA ✓ |
| status.success | `#3D9A6A` | **4.4:1** | △ 临界 → 改用 `success-text #4EB47C` = **6.0:1** |
| status.info | `#4A6FD4` | **3.3:1** | ✗ → 改用 `info-text #7E9DF6` = **5.9:1** |
| status.danger | `#E5484D` | **4.0:1** | ✗ → 改用 `danger-text #F26A6F` = **5.2:1** |
| **白字 on BtnPrimary（旧值 `AccentBrush #5B8DEF`）** | | **3.2:1** | ✗ → **0.60.0 已修**：实底改用 `action.primary-solid #2E5FC0` = **5.97:1**；hover `#3D70D6` = 4.68:1、press `#26489B` 同样达标 |

### 7.3 必须处理的四项（按优先级）

| # | 问题 | 现状 | 修法 | 影响面 |
|---|---|---|---|---|
| P0 ✅ | **主按钮白字对比度 3.2:1** | `BtnPrimary` 底 `#5B8DEF` + 白字 | 实底改用 `action.primary-solid #2E5FC0`（5.97:1），保留 `#5B8DEF` 做文字/图标/描边 | `BtnPrimary` 一处 Setter，全应用受益 |
| P1 ✅ | Danger 文字在深色下 4.0:1 | 删除类按钮文字用 `DangerBrush` | 新增 `status.danger-text #F26A6F` 专用于文字 | `BtnDanger` / `MenuItemDanger` |
| P1 ✅ | Info 文字在深色下 3.3:1 | AI 已打标徽章 | 新增 `status.info-text #7E9DF6` | AI 打标徽章、`InfoBrush` 文字位 |
| P2 ✅ | Success 4.4:1 临界 | 人脸已识别徽章文字 | 新增 `status.success-text #4EB47C` | 人脸徽章 |

> 注意 P0 的处理方式：**不是改品牌色**，而是把"这个颜色用来当背景"和"这个颜色用来写字"拆成两个 token。这就是 §3 三层结构的直接收益。

### 7.4 卸载动画与键盘
- 系统开启「减弱动画」→ `DurFast/Base/Slow` 全部置 0
- Tab 顺序 = 视觉顺序（左→右、上→下）；工具条→筛选→内容→状态条
- 图片网格建议支持方向键移动焦点、`Enter` 打开（当前仅鼠标路径）
- 全局：`SnapsToDevicePixels=True`，1px 线不发虚

---

## 8. 与既有七条红线的关系

| 红线 | v2 的态度 |
|---|---|
| R1 筛选区禁弹层 | **继续严守**。折叠行是内联，不是 Popup |
| R2 点击兜底两不变量 | 不动。本次不触碰任何 Click 路径 |
| R3 网格结构/卡片尺寸不可改 | **完全遵守**。168×196、`FlowWrapPanel`、虚拟化管线一律不改 |
| R4 `PopupAnimation=None` | 无新增 Popup，无风险 |
| R5 滚动区零动效 | 状态条进度与 Toast 在滚动区之外，**不违反** |
| R6 clean + 版本号纯数字且 UI 常显 | 批次表遵守；版本号继续显示在设置页「关于」与侧栏底部 |
| R7 交互必须真机验收 | 每批次的验收表都留出真机项 |
| **R8（新增）** 侧栏图标必须是 `Path` 矢量 | 禁位图缩放失真、禁 Segoe MDL2 字体图标（打包体积与渲染一致性问题） |
| **R9（新增）** 表面之间不得出现第五个层级 | 画布只有三级：canvas / panel / sunken；新增第四层一律驳回 |
| **R10（新增）** 主题切换只能替换 `MergedDictionaries[1]` | 禁止逐 Brush `SetValue`，一定会漏 |

---

## 9. 落地批次与验收

> **版本号**：下表用 `X.Y.Z` 占位。发布前必须在**你当前版本基础上递增**，并与历史所有版本号不同（纯数字、不带中文前后缀），且四处的版本号同步（`PictureButler.csproj` 两处 / `LocalHttpServer.cs` 的 `/api/health` 注释**与**返回值两处字面量 / `src-native\原生版说明.txt` / `D:\Tool\PictureButler\PictureButler-说明.txt`），发布后用 `Invoke-RestMethod http://127.0.0.1:8189/api/health` 核对返回值。

### 批次 1 —— Theming 地基 ✅ **0.60.0 已完成**

**做**：新建 `Themes/Primitive.xaml` + `Semantic.Dark.xaml` + `Semantic.Light.xaml`；`App.xaml` 改 `MergedDictionaries`；修复 §7.3 的 P0–P2 四个色。

**验收**
- ✅ `dotnet build -c Release` 零错误；`obj\...\Themes\*.baml` 三份全部产出（原语 / 深 / 浅）
- ✅ `grep '#[0-9A-Fa-f]{6,8}' App.xaml` 仅剩 §动画所需的两个 `Color` 资源（已移入语义层）
- ⬜ 用户真机：启动、五页切换、识别、打标、多选、查看器、设置滚动全过一遍
- ⬜ 浅色主题全量走查：重点是**查看器浮层**（浅色下必须改浅）与各处的选中态

**回退**：单个 commit revert。

### 批次 2a —— 侧栏图标化 ✅ **0.60.0 已完成**

**做**：5 个导航项各加 16px Path 矢量图标（`GeoNavPrompts / Images / IdPhotos / Docs / Settings`，红线 R8）；选中态由「实底填充」改为「3px 竖条 + `AccentSoftBrush` 底 + 主色文字 + SemiBold」四重冗余；新增 `NavIdleBrush` / `NavHoverBrush` 两个主题色。

**验收**
- ⬜ 真机：鼠标依次点击五个导航项，**无一穿透到内容区**（R2 不变量不能被破坏）
- ⬜ 图标在 100% / 125% / 150% 缩放下都清晰（`Stretch=Uniform` + `SnapsToDevicePixels`）
- ⬜ 选中态在深色下「一眼可辨」，且不与未选中项混淆

### 批次 2b —— Shell 五区（工具条统一 48px / 筛选行默认收起 / 新增状态条）✅ **0.61.0 已完成**

**做**：工具条统一 48px（`Border Height=48` + 内容垂直居中）；筛选行默认收起（`_imgFilterExpanded` + `ApplyFilterBarVisibility()` 统一入口，`ImgFilterToggle` 内联展开）；新增 28px 常驻状态条——**服务状态与任务状态用 `ElementName` 绑定镜像** `HttpStatusText` / `ImgRecognizeStatus`（零逻辑侵入），右侧 `StatusVersionText` 显示版本号。

**验收**
- ⬜ 1280×800 / 1440×900 / 1920×1080 三档下工具条不溢出
- ⬜ **真机**：跑一遍「点页签 → 点图片 → 点多选 → 点右键菜单」四连，确认无穿透
- ⬜ 状态条在三个视图下都可见；跑识别/打标时任务进度同步显示

### 批次 2c —— 多选态替换工具条 / 标题栏 36px / 筛选行 44px ✅ **0.61.2 已完成**

**做**（方案 §2.3 的核心决策此前未落地：原实现是多选条**追加**在页签旁边，不是替换）
- 工具条拆为 `ImgToolMain`（常规态）与 `ImgToolMulti`（多选态），**并列于同一 Grid 格、共用 48px 槽位**，`ApplyImgToolbarMode()` 统一互斥 → 多选进出零高度跳动
- 组内条目（`ImgMultiBar` / `PersonMultiBar`）显隐仍按各自状态决定，避免出现空工具条或两条并排
- 进入人物详情（`ShowPersonActionBar`）时自动退出多选——否则多选态工具条会遮住人物操作栏
- 标题栏 44 → **36px**（§2.1 硬指标）；筛选行规范为 **MinHeight=44**（用 Min 不用固定值，来源芯片换行时不被裁剪）

**验收**
- ⬜ **真机**：进多选 → 工具条整条替换、高度不跳；点「取消」→ 恢复常规态
- ⬜ 人物页多选 → 点进某个人 → 自动退出多选且操作栏可点
- ⬜ 标题栏三个按钮（最小/最大/关闭）在 36px 下仍好点

**风险**：两组堆叠于同一 Grid 格，隐藏组必须是 `Collapsed`（`Hidden` 仍会吃掉鼠标命中，见 0.47.2 教训）。

### 批次 3 —— 设置页二级侧栏 ✅ **0.61.0 已完成**

**做**：内容区拆 180px 二级目录（9 项 `SetNavBtn`）+ 滚动面板；点击 `BringIntoView()` 滚动定位；区块标题命名 `SetSec*`。**内容区现有结构一行未动**（只加容器与导航），因此风险极低。

**验收**：⬜ 9 个二级项点击各就各位；「关于」区版本号正确。

### 批次 4 —— 浅色主题 ✅ **0.60.0 已完成（切换方式：重启生效）**

**做**：`Themes/Semantic.Light.xaml`；设置页「外观 → 界面主题」深色 / 浅色；`-text` 变体在浅色下等于基色。

**切换为什么是重启**：WPF 的 `StaticResource` 只在元素构造时解析一次，换字典不会回溯刷新已存在的窗口。要即时刷新就得把全应用的颜色引用改成 `DynamicResource`——改动面极大且必然有遗漏。**选在启动前装载字典**：零遗漏、零遗漏风险，也不违反红线 R10。

**悬停方向（本批新提炼的规则）**：深色主题 hover 朝「更亮」走，浅色主题 hover 朝「更深」走——都是在提升与页面底的对比，并由此反推出 `action.primary-solid` 的最终取值（见 §3.3）。

**验收**：用 §7.2 的实测值逐项比对；**查看器浮层在浅色下必须改浅**（`OverlayBarBrush` 已改为白底），这是最容易翻车的一处。

---

## 10. 决策记录（2026-09-29 已拍板，不再悬空）

| # | 问题 | 决定 | 落地 |
|---|---|---|---|
| 1 | 是否引入浅色主题 | **是** | 批次 1 + 4，0.60.0 已实装 |
| 2 | 侧栏要不要图标 | **要** | 批次 2a，0.60.0 已实装（5 项全部 Path 矢量） |
| 3 | 图片库要不要列表视图 | **不要** | 维持网格单一呈现（红线 R3 卡片结构本来也不可改） |

**全部批次均已排期并落地**（1 / 2a / 2b / 2c / 3 / 4），剩余工作只有真机逐项走查（见 §9 各批次的 ⬜ 项）。

---

## 11. 变更记录

| 日期 | 版本 | 内容 |
|---|---|---|
| 2026-09-29 | v2.0 | 首版。基线实测 App.xaml 32 画刷；提出三层令牌、五区 Shell、状态条、设置二级侧栏；首次完成 19 项对比度实测并发现 P0 主按钮白字 3.2:1 问题 |
| 2026-09-29 | v2.1 | 决策拍板（浅色主题 ✅ / 侧栏图标 ✅ / 列表视图 ❌）。批次 1 + 2a + 4 落地为 **0.60.0**：新增 `Themes/Primitive.xaml`、`Semantic.Dark.xaml`、`Semantic.Light.xaml`、`ThemeManager.cs`；App.xaml 改三层结构并清掉全部硬编码色；修 P0–P2 四项对比度；`action.primary-solid` 由候选 `#3F6FD1` 改为 `#2E5FC0`（hover 提亮后仍须 ≥4.5:1）；新增 hover 方向规则。编译 0 错误 |
| 2026-09-29 | v2.2 | 批次 2b + 3 落地为 **0.61.0**（工具条 48px / 筛选行默认收起 / 28px 状态条 / 设置页二级侧栏）；同期按用户要求重做光标（棱角箭头 + 三指几何手型，生成器 `.workbuddy\mkcursors.py`）。**0.61.1**：`LogDebug` 加 `[Conditional("DEBUG")]`，Release 不再往程序目录写日志，并清理部署目录 18 个历史残留（约 250MB）。**0.61.2** 批次 2c：多选态改为整条工具条替换（`ImgToolMain` / `ImgToolMulti` 共用 48px 槽位）、标题栏 36px、筛选行 MinHeight 44px。三次发布 health 均已核对版本号 |
