# 算法层与显示接缝：外部评审逐条核对

对一份针对 `DP.Vision.Algorithms` 与显示接缝的外部评审做逐条核对。评审方自述"容器内没有 .NET SDK，未能构建或运行测试"，
因此这里把所有结论拉回代码复核，并对**能量化的部分**补上实测。

核对方式与证据分档：

- **已实测**：本轮真实构建 / 运行 / 探针得到的数据（双 TFM）。
- **已核对**：读源码确认，或由源码公式精确推算。
- **未核实**：需要基准或真机才能定的量级，本文不给结论。

本轮实测环境：`DP.Vision.sln` Debug 构建 **0 警告 0 错误**；`DP.Vision.Tests` **115 例 × 2 TFM**、
`DP.Vision.Algorithms.Tests` **69 例 × 2 TFM**，全绿。

---

## 1. 判定总表

| 编号 | 评审结论 | 判定 | 关键依据 |
|---|---|---|---|
| 架构-1 | 算法侧没有"发放算子"的模块/注册表，宿主必须点名 OpenCV | **事实正确，但性质需改** | 确实不存在任何模块类型；但这是 `ALGORITHM_MIGRATION.md` **明文选择**的装配方式，不是漏做 |
| 架构-2 | 失败报告两套（返回状态 vs 抛异常），结果类型的 `Status` 恒为 `Completed` | **正确**，且比描述更普遍 | 8 个结果类型的 `Status` 是常量；8 处 `NotSupportedException` 与 4 处 `InvalidOperationException` 并存 |
| 架构-3 | 没有把算法结果变成可绘制物的适配层，应新建一个"引用两侧"的工程 | **方向对，前提错** | 该工程**已经存在**：`DP.LabelInspection.Adapter.Vision`。真问题是它该留在消费方还是上收 |
| 架构-4 | 显示代码住在核心工程，算子工程都能看见；边界测试只覆盖采集侧 | **正确**，且耦合已是承重墙 | 所有算子工程经 `Algorithms → DP.Vision` 传递可见 `03.Display`；适配器正靠传递引用使用 `GeometryOverlay` |
| 架构-5 | WinForms/WPF 画布近乎复制（各约 850 行） | **基本正确，但"曲线简化"不成立** | 874 / 853 行，**484 行非空行逐字节相同**；`ContourLod` 等模型早已在核心共享 |
| Bug-1 | 默认模板阈值几乎接受任何匹配 | **正确，实测比描述更严重** | 见 §3.1：**平坦区**上匹配**花纹模板**得分 0.914，默认阈值下判为"找到" |
| Bug-2 | 画布关闭后再投帧会让生产者线程抛 `ObjectDisposedException` | **正确** | `LatestFrameMailbox.Post` 关闭时抛异常，而其自身文档写的是"不会接受" |
| Bug-3 | `CvPixels.Buffer` 把任何多通道图当 Bgr24；`Gray` 把未知布局当 RGBA | **正确** | `CvPixels.cs:44-50` / `:29-33` |
| Bug-4 | `IImagePreprocessor` 要求"产生新 FrameId"，但返回类型没有 FrameId | **正确** | 契约注释与签名互相矛盾 |
| 性能-1 | 每次调用都拷贝整图，区域很小也照付 | **正确** | `CvPixels.Gray` 全图转换 + `OpenCvEdgeMeasurer` 全图 Canny |
| 性能-2 | 逐像素读取慢（`At<int>` / `At<byte>` / Otsu 直方图） | **正确** | 三处均确认；同工程内已有按行拷贝的更快写法 |
| 性能-3 | 结果被拷贝两次 | **正确**，且 Gray8 路径实际是三次 | `CvPixels.Buffer` → `VisionImage.CopyFrom`；`Gray` 另有 `Clone` |
| 气味-1 | `CvImages` 与 `CvPixels` 职责重叠 | **正确**，且危害比"重叠"大 | 两者的**支持格式集合不同**，是正确性陷阱 |
| 气味-2 | 实现之间存在隐藏依赖 | **正确** | 模板定位器自建姿态定位器；边缘测量器调用标定求解器 |
| 气味-3 | `RenderCache.Dispose()` 实为"清空" | **正确，但优先级低** | 文档已明说"清空后仍可继续使用"；改名是公开 API 破坏性变更 |
| 气味-4 | 异常参数名常错 | **正确，且是"模式"不是两处笔误** | `Intersect`、`BlobOptions` 确认；`01.Imaging` 另有 **5 处同类**（见 §3.5(c)） |
| 气味-5 | `STRUCTURE.md` 过时（单类型、目录名、文件数） | **部分正确** | 目录名与文件数确实过时；但工程清单**昨天刚同步过**，算法工程都在列 |

评审未覆盖：Basler / Halcon 驱动、ONNX、ZXing、ROI 编辑器。本文同样不覆盖。

---

## 2. 评审建议里需要改的三处

### 2.1 Bug-1 的修法会把问题换回去

评审建议"改用 `SqDiffNormed` 或 `CCoeffNormed`"。这两个都**不解决**当前公式被选中的理由。

`OpenCvTemplateLocator.cs:8` 的注释写明：当前用"归一化均方差"，是因为它对**黑色和常量模板同样有定义**。
而：

- `TM_SQDIFF_NORMED = Σ(T−I)² / sqrt(ΣT²·ΣI²)` —— 常量模板 `ΣT² = 0`，**除零**。
- `TM_CCOEFF_NORMED` 要减去均值再算相关，常量模板方差为 0，**同样退化**。

也就是说，评审选的两个替代项恰好会在当前公式专门照顾的场景上崩掉。**诊断对，处方错。**

正确的方向是三者之一（需要先定语义）：

1. 保留公式，**把默认阈值改到有判别力的区间**，并写清"分数 = 1 − 平均平方差/255²"的含义与适用范围；
2. 改成按**模板自身的能量**归一（`Σ(T−I)²/Σ(T−mean T)²` 之类），常量模板单独定义；
3. 让 `minimumScore` **没有默认值**，强制调用方显式声明。

### 2.2 架构-1 是"重开一个已有决定"，不是"补一个缺口"

`ALGORITHM_MIGRATION.md:38` 原文：

> 注入实现由宿主拥有。算法选择通过装配API，**不是自动插件扫描或阈值互换**。

`ALGORITHM_MODULES.md:25` 也写"宿主装配时才选择具体实现"。

所以算法侧没有注册表是**刻意的**，与采集侧 `IVisionAcquisitionDriverModule` 的自动扫描是**两种不同取舍**，
不是"采集做了、算法忘了"。评审把两者对比后要求统一，等于要求推翻这条决定——这可以谈，
但必须作为**决定重开**来记录（为什么当时选装配、现在为什么改），不能写成"缺失项"。

顺带一个事实：采集侧的插件加载器**已经存在且被验证过**（目录扫描 + Manifest + 窄契约）。若决定改，
应直接复用那一套而不是新造一个，否则两条链会出现两套插件语义。

### 2.3 架构-3 的"新建一个适配工程"已经存在

`DP.LabelInspection/src/DP.LabelInspection.Adapter.Vision/` 就是评审描述的那个工程：
`netstandard2.0`，同时引用 `DP.LabelInspection.Contracts` 与 `DP.Vision.Algorithms`，
里面 `Display/VisionAdapter.cs` 负责把业务几何转成 `DP.Vision` 的 `GeometryOverlay` 等显示类型。

真正的问题比评审说的更值得讨论：

- 这个适配器**没有声明**对显示层的依赖，却在使用 `GeometryOverlay`——靠 `Algorithms → DP.Vision` 的传递引用拿到。
  这正是架构-4 的耦合在**实际承重**，不是"今天没人用所以没风险"。
- 适配器住在**消费方仓库**里。若只此一个消费方，这是合理的（变化随业务走）；
  若预期第二个消费方，才应该上收成 `DP.Vision` 侧工程。

所以这条的正确提法不是"补一个适配层"，而是"**决定适配层的归属**"。

---

## 3. 复核细节

### 3.1 Bug-1：默认阈值（已实测）

代码路径（`OpenCvTemplateLocator.cs`）：

- `:37` `Cv2.MatchTemplate(roi, pattern, scores, TemplateMatchModes.SqDiff)` —— 原始平方差，未归一。
- `:41` `score = clamp(1 - min / (65025d * w * h), 0, 1)`，即 `1 − 平均平方差 / 255²`。
- `:42` `found = score >= minimumScore`，默认 `minimumScore = .9`（`TemplateLocation.cs:76`）。
- 姿态定位器同式（`OpenCvTemplatePoseLocator.cs:51`），除数是掩码面积而非整块。

本轮用临时探针实测（双 TFM 结果一致，跑完即删，工作区已确认干净）：

| 场景 | 默认阈值 `.9` | 阈值 `.99` |
|---|---|---|
| 常量模板 `60` 对**平坦区** `128`（暗 68 级） | `Found=True`，`Score=0.928889` | `Found=False` |
| **花纹模板**（15 个 10~240 的跳变值）对**完全平坦的 128 区** | `Found=True`，`Score=0.914043` | — |

第二行是关键：目标区**毫无结构**，模板却"找到"了。评审的算术也精确对得上——
`1 − 83840/15/65025 = 0.914043`，与实测逐位相同，说明公式行为与推算一致，问题**纯粹出在默认值**。

可用区间之窄值得记下：**"完全不像"是 0.914，"完全一致"是 1.0**，全部判别力挤在 8.6% 的量程里。

评审推测"测试里传 `.9999` 说明作者已经踩到过"——这条**只是推测**，未证实。
`LocatedRangeTests.cs:26,28,48` 三处 `.9999` 的用例，断言的是**掩码把候选排除掉**（硬过滤），
不是靠分数区分，所以未必因为阈值太松才这么写。**不要把它当成"作者知情"的证据。**

### 3.2 Bug-2：关闭后投帧（已核对）

- `LatestFrameMailbox.cs:28-31`：`_disposed` 时 `throw new ObjectDisposedException(...)`。
- 同一文件 `:17` 的文档：`<returns>是否接受本帧；旧序号**或已关闭邮箱**不会接受。</returns>` —— 与实现相反。
- `IVisionCanvas.cs:10`：`<returns>是否接受该预览序号。</returns>`。
- `VisionCanvasControl.cs`（Winform `:212` / WPF `:215`）都是 `return _mailbox.Post(frame);` 纯转发，
  所以异常直接抛回**相机回调线程**。

评审引用的是 `IVisionCanvas` 的文档，实际更贴切的依据是**邮箱自己的文档**。结论不变。
修法明确：`Post` 在已关闭时返回 `false`（与文档一致），`AdvanceTo` 需另行决定（它没有"接受与否"的返回位）。

### 3.3 Bug-3 / Bug-4（已核对）

- `CvPixels.cs:44-50`：`image.Channels() == 1 ? Gray8 : Bgr24`。4 通道图会被按 3 字节/行拷贝 → 错行、图损坏。
  当前调用方只传 1/3 通道 8 位，所以是**给下一个调用方埋的雷**，不是活跃缺陷。
- `CvPixels.cs:29-33`：非 Gray8/Bgr24/Rgb24/Bgra32 一律走 `RGBA2GRAY`。
- `ImagePreprocessing.cs:53` 要求"包装帧时必须产生新FrameId"，`:56` 却返回裸 `IImageSource`（无 FrameId）。

### 3.4 架构-4：显示代码住在核心（已核对）

`DP.Vision` 的直接消费者只有 5 个：`Acquisition.Abstractions`、`Acquisition.Management`、`Algorithms`、`Basler`、`UI`。
算子工程（`OpenCv` / `Onnx` / `OnnxDetection` / `Zxing` / `Halcon`）都只引用 `Algorithms`，
**经传递**拿到 `DP.Vision`，于是 `03.Display/` 的 16 个文件（`Visual`、`CanvasLayer`、`RenderCache`、
`LatestFrameMailbox`、`VisionColors`…）对所有算子工程可见。

评审说"今天没有算子用它"——**已核对属实**（在四个算子工程里检索显示类型，零命中）。
但"没人用"不等于"没风险"，因为**已经有人在用**：见 §2.3。

现有边界测试 `SolutionDependencyBoundaryTests.cs:25-44` 共 4 条规则，全部针对采集侧与 UI 套件，
**没有一条覆盖算法侧**——评审这条正确。

**修法成本很低**：同仓已有现成范式。`DP.Vision.Algorithms.Tests/Architecture/LegacyCaptureAbstractionTests.cs`
已经在做"生产源码里不得出现某标识符"的扫描（含从测试输出目录回溯仓库根的工具函数），
把它的做法扩成"算子工程源码不得出现 `03.Display` 类型名"即可。

### 3.5 评审没看到的两条

**（a）`DP.Vision.Halcon` 声明了对 `DP.Vision.Algorithms` 的引用，但一行都没用。**

`DP.Vision.Halcon.csproj:10` 有该 `ProjectReference`，而该工程 14 个文件**全部是采集侧**
（`HalconAcquisition*` / `HalconStream*` / `HalconNeutralFrames` / `HalconImageSource`），
检索 `DP.Vision.Algorithms` 与各算法类型：**零命中**。

后果与架构-4 同源：每个 HALCON 采集包消费者都会被拖上整条算法链 + 核心链。
而且它对**基于程序集引用的检查完全隐形**（Roslyn 只为实际用到的引用写 `AssemblyRef`），
现有 4 条边界规则也管不到它。`ALGORITHM_MODULES.md:20` 把 `DP.Vision.Halcon` 列为"拟议"的算法基座，
所以这更像是**为将来预留的引用先落地了**——要么现在删掉，要么补一条边界规则把它钉住。

**（b）`CvPixels` 与 `CvImages` 的"重叠"其实是能力矩阵不一致。**

- `CvImages.Mat`（`:20`）只支持 Gray8 / Bgr24，其他一律 `NotSupportedException`。
- `CvPixels.Supports`（`:11`）只排除 Gray16，其余都放行；`Gray` 还会把未知布局当 RGBA 处理。

于是同一个像素布局，走 `CvImages` 会抛、走 `CvPixels` 会被**静默按错误格式转换**。
这比"两个类都干同一件事"严重一档：调用方选错辅助类，得到的是**不同的失败模式**（一个报错、一个出错图）。

**（c）气味-4（异常参数名）在 `01.Imaging` 是"一个 `throw` 管 N 个条件"的系统性写法，不是笔误。**

`01.Imaging` 下 6 个 `ArgumentOutOfRangeException(nameof(...))` 里，有 **5 处**是"多个参数的条件或在一起、却只报其中一个名字"：

| 位置 | 条件个数 | 报出的名字 | 实际可能出错的参数 |
|---|---:|---|---|
| `ImageInfo.cs:23` | 5（含 `EPixelLayout` 枚举校验） | `width` | `height`、`layout` |
| `FrameWriter.cs:41` | 多 | `count` | `sourceOffset`、`offset` |
| `ImageBuffer.cs:85` | 5 | `count` | `sourceOffset`、`destinationOffset` |
| `ImageBuffer.cs:119` | 4 | `tileX` | `tileY` |
| `MemoryImageSource.cs:41` | 6 | `level` | `tileX`、`tileY`、`tileSize` |

后果不是"信息不够"，而是**信息是错的**：`ImageInfo` 传了非法 `layout` 会得到 `ArgumentOutOfRangeException("width")`；
`MemoryImageSource.ReadTile` 传 `tileSize = 5000` 会得到 `ArgumentOutOfRangeException("level")`。
调用方按异常消息去查参数会查错方向。

⇒ 修法应是**加一个分段校验的辅助**（每个条件各报各的名字），而不是改 5 个字符串——
否则下一个新增参数的人会照抄这个写法。这也说明气味-4 该从"个别命名错误"升级为"校验写法待收口"。

**（d）`MemoryImageSource` 与 `ImageBuffer` 的分工：外壳合理，但一条不变式放错了类。**

`ImageBuffer`（internal）持有 `_gate` 锁、`Storage`（引用计数 + `byte[]` + 回池回调）与 `Sample` 分块采样算法；
`MemoryImageSource`（internal）**自有字段只有 1 个 `ImageBuffer`，5 个成员全是转发**，唯一新增职责是
"把 internal 类型换成 public 接口 `IImageSource`" + `ReadTile` 的参数范围校验。
外壳本身**有价值**（公开契约与存储机制解耦，`ImageSourceTests.StorageImplementationsAreNotExported`
钉住了"两个 internal 类型不导出"），不必拆掉。

问题在不变式的**位置**：`ImageBuffer.Sample` 的文档写"**调用方须保证** level 范围 0–20"，
而它自己**不校验**——真正校验的是**另一个类**（`MemoryImageSource.ReadTile`）。
`Sample` 在生产代码里**只有这一个调用方**，所以今天是对的；但这意味着
"谁再用 `Sample` 谁就得记得 0–20"这条不变式**离它需要的地方隔了一层**，
编译器与运行期都不会提醒。`Sample` 里 `1 << level` 一旦拿到越界 level 不会报错、只会算错。
⇒ 把 level 范围校验下移到 `Sample`（`ReadTile` 保留 UI 侧更友好的整体校验），代价 1 行，去掉这层隐性耦合。

### 3.6 性能三条：结论可信，但**量级未测**

三条结论均已核对（见总表）。但要强调的是：本仓**没有算法吞吐基准**。
`PERFORMANCE.md:15` 明确写着"未测…相机采集或**算法吞吐量**"，该文件的 40 进程对比全是**显示**性能。

所以：

- "小区域付整图代价"**机制成立**，但**省多少**目前无数据。20MP 上跑 50×50 区域的具体损耗需要基准。
- 本仓既有的性能纪律是"**改造前先有基准**"（见 `PERFORMANCE.md` 的 LOD 结论：收益来自复用，不是构建免费）。
  照此，性能项应先建基准再动，不要先改后测。

### 3.7 架构-5：重复度实测

| | 总行数 | 非空行 |
|---|---:|---:|
| `Winform/Canvas/VisionCanvasControl.cs` | 874 | 789 |
| `WPF/Canvas/VisionCanvasControl.cs` | 853 | 776 |
| **逐字节相同的非空行** | | **484** |

即**每个文件约六成内容与另一个逐字节相同**。"近乎复制"这个说法对得上重复的那部分质量。

但评审列举的"可上移内容"里有一条**不成立**：它说"曲线简化"可以上移，
而 `ContourLod` 早已在核心 `03.Display/ContourLod.cs`，两个画布各自只有 1 处引用。
实测两个画布对核心显示模型的使用完全一致（`RenderCache` 4 次、`CanvasPlanning` 2 次、
`CanvasFrame` 3 次、`TileRequest` 2 次…），说明**数据模型已经抽干净了**；
真正重复的是**控制器逻辑**（每帧状态机、缓存接线、请求调度、几何适配）。

这反而是好消息：上移的目标比评审设想的更明确——不是"搬数据模型"，而是"抽一个框架无关的画布控制器"。

---

## 4. 与既有决策 / 文档的关系

| 评审项 | 与仓库现状的关系 |
|---|---|
| 架构-1 模块/注册表 | **重开决定**（`ALGORITHM_MIGRATION.md:38` 明文相反） |
| 架构-3 适配层 | **已存在**（`DP.LabelInspection.Adapter.Vision`），问题变成"归属" |
| 架构-4 边界测试 | 真缺口，且同仓有现成范式 |
| 架构-5 画布去重 | `PERFORMANCE.md:60` 已把"更高效的几何适配、后台LOD预计算"列为待办，方向一致 |
| Bug-1 阈值 | 未见任何文档记录；`ALGORITHM_MODULES.md:39` 只要求"匹配必须说明分数含义、阈值和覆盖限制"，**当前未做到** |
| Bug-2 / Bug-3 / Bug-4 | 未见任何文档记录 |
| 性能三条 | 未记录；`PERFORMANCE.md:15` 明说算法吞吐未测 |
| 气味-5 `STRUCTURE.md` | 目录名与文件数确已过时；**工程清单昨天刚同步**（`8915b06`），算法工程都在列 |

---

## 5. 建议的优先级与顺序

**P0（安全 / 崩溃，改动小）**

1. **Bug-1 默认阈值**。当前默认值下"花纹模板命中平坦区"会被判为找到，属于**静默误接受**，
   对质检场景是危险方向。先定语义（§2.1 三选一），再改默认值，并补一条**用平坦区做反例**的回归用例。
2. **Bug-2 关闭后投帧**。改 `Post` 返回 `false`（与自身文档一致），`AdvanceTo` 另定。

**P1（便宜且堵真洞）**

3. **架构-4 边界测试**：把 `LegacyCaptureAbstractionTests` 的范式扩到算子工程 × `03.Display`；
   顺手处理 §3.5(a) 的 HALCON 悬空引用（删掉或钉住）。
4. **Bug-4**：契约与签名二选一（要么返回 `ImageFrame`，要么删掉那句话）。
5. **Bug-3**：`Buffer` / `Gray` 对不支持的布局**明确拒绝**，别静默按 Bgr24/RGBA 处理。

**P2（需要先定规则或先有基准）**

6. **失败报告统一（架构-2）**。这里不能简单"二选一"：格式不支持（能力不匹配）与证据不足（数据条件）
   是两类事。`ALGORITHM_MODULES.md:39` 自己的要求是"不支持时**明确返回原因**"，
   所以方向应是"能力/格式不匹配走带内状态，预算与不变量违反仍抛异常"。
   先写这条规则，再逐算子对齐；同时给那 8 个常量 `Status` 一个真实语义（或删掉该属性）。
7. **性能**：先建算法吞吐基准（小区域 vs 整图、逐像素 vs 按行），再改。别反过来。
8. **架构-1**：作为决定重开来写（问题、选项、取舍、影响面）。若采纳，复用采集侧已验证的加载器。

**P3（大改动，需单独排期）**

9. **架构-5 画布控制器上移**：目标明确（控制器，不是数据模型），但涉及两个 UI 后端 + 平台中立 UI 套件。
10. **架构-3 适配层归属**：取决于是否预期第二个消费方，先定这个再动代码。
11. **气味清理**：`CvImages`/`CvPixels` 合并（合并时必须**统一能力矩阵**，否则只是把陷阱挪个位置）、
    `RenderCache.Dispose` 改名（公开 API 破坏性变更，与 `ALGORITHM_MODULES.md` 的"要求宿主重新编译"一并考虑）、
    异常参数名、`STRUCTURE.md` 三处过时描述。

**顺序约束**：采集侧的优化项（#7 契约、#9 诊断）正在另一进程手上，`DP.Vision.UI` 刚由阶段 D 稳定下来。
上面 P0/P1 全部落在 `DP.Vision.OpenCv` / `DP.Vision.Algorithms` / `DP.Vision` 与测试工程，**与在途工作不冲突**；
P3 的 9/10 会碰 `DP.Vision.UI`，应等采集侧 #7/#9 落地后再排。

### 5.1 一个共同前置：P0-1 与 P2-7 其实是同一件事

这两条被分在两个优先级里，但它们**缺的是同一个东西**：

- P0-1 要"把默认阈值定到有判别力的区间"，可是**没有任何数据**说明"真实图像上正确匹配的分数是多少"。
  当前唯一实测到的两个点（0.914 / 1.0）都在**合成平坦图**上，不足以支撑选值。
- P2-7 要"改性能前先有算法吞吐基准"。

也就是说，**先建一个小的算法基准**（真实/半真实图上跑模板匹配与区域处理，输出分数分布 + 小区域 vs 整图耗时），
可以一次性同时解掉 P0-1 的选值依据与 P2-7 的前置。**在拿到这个基准之前不要改默认阈值**——
凭 0.914 这个孤点把默认值从 `.9` 抬到 `.99`，只是把"静默误接受"换成"静默误拒绝"，
两个方向都没被数据支持。这条与 `PERFORMANCE.md` 自己的纪律一致（LOD 的结论就是"先量再改"）。

唯一**不等基准就能做**的 P0 项是 **Bug-2（关闭后投帧返回 `false`）**：它的正确行为由邮箱自己的文档钉死，
与任何性能/阈值数据无关。

---

## 6. 未核实的边界

- 性能收益的**量级**（无算法吞吐基准）。
- `RenderCache.Dispose()` 改名对下游的实际影响面（未跨仓检索全部消费者）。
- ONNX / ZXing / Basler / Halcon 驱动、ROI 编辑器：本轮与评审同样未覆盖。
- 工业准确率、ISO 评级、16K 相机吞吐等既有验收边界不变。
