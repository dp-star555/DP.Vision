# PP-OCR 任务与业务处理拆分方案（待实施）

## 目标与当前事实

目标：Vision 内的 PP-OCR 执行模块只负责**准备好的模型输入 → 模型输出证据**；文字候选的提取、筛选、ROI 编排及标签判定由业务层拥有。ONNX Runtime 是当前执行端，OpenVINO 是未来可能的执行端，不因假设中的实现提前制造空工程。这里的“纯粹”指不掺入候选策略、OpenCV 类型与标签规则；推理模块仍必须拥有模型加载、输入/输出契约校验、资源释放与诊断。

现状：

- `src/DP.Vision.Onnx/Recognition/OnnxTextLineRecognizer.cs`：PP-OCRv4 识别模型校验、ONNX 推理、CTC 解码，返回 `TextLineRecognition`；预处理由注入的 `ITextLinePreprocessor` 完成。
- `src/DP.Vision.OnnxDetection/Detection/OnnxTextRegionDetector.cs`：DB 检测图像缩放/归一化、ONNX 推理、OpenCV 轮廓处理及候选框筛选全部放在同一类。输出仅为 `PixelBounds`，概率图证据丢失。
- `DP.Vision.Algorithms` 中 `ITextRegionDetector` 返回候选框，`ITextLineRecognizer` 返回解码后的文字。这两个接口是**面向检测/识别使用者的接口**，不是原始模型任务接口。
- `DP.LabelInspection.Runtime` 实际构造 `OnnxTextLineRecognizer` 和 OpenCV 预处理器（唯一构造点：`DP.LabelInspection.Runtime/Hosting/LabelInspectionHost.cs:84`）；当前对 `OnnxTextRegionDetector` 只有项目引用，在业务源码 `src/` 内没有实例化它的调用点（仓库外的盘点范围见「待决项」#6）。

跨仓库事实（本方案的实际边界）：

- 业务侧代码位于**另一个仓库** `..\DP.LabelInspection`（独立 git 仓库，与 `DP.Vision` 平级）。`DP.LabelInspection.Runtime` 通过跨仓 `ProjectReference` 直接引用 `DP.Vision.Onnx`、`DP.Vision.OnnxDetection`、`DP.Vision.OpenCv`、`DP.Vision.Zxing`，这些跨仓引用**没有版本锁定**（既非子模块也非包）。因此本方案跨两个仓库、两个 `.sln`；凡涉及“更新项目引用/锁文件”的阶段都必须分别指明改哪个仓库。
- 该仓库已存在职责为 Vision 适配的 `DP.LabelInspection.Adapter.Vision` 工程（已引用 `DP.Vision.Algorithms`，现有 5 个类：算法契约适配、字形参考图转换、显示适配）。本文把检测候选处理与 `ITextRegionDetector` 适配器放在 `DP.LabelInspection.Runtime`，与这一既有分层的取舍见「待决项」#3。

## 职责及依赖方向

| 位置 | 负责 | 不负责 |
|---|---|---|
| `DP.Vision` 核心 | 中立图像、坐标、租约 | 模型、ONNX/OpenVINO、OpenCV、标签策略 |
| PP-OCR 执行端 | 加载/校验 PP-OCR 模型、执行推理、校验并返回有所有权的模型输出与身份 | OpenCV 轮廓、文字候选阈值、标签业务判定 |
| `DP.LabelInspection.Runtime` | 根据模型证据提取候选、筛选/排序/去重、选择 ROI、接入标签流程；必要时用 OpenCV | 管理 ONNX Session 或重新解释推理运行时对象 |
| `DP.Vision.Algorithms` 既有接口 | 对调用方提供文字检测/识别语义（保留兼容时） | 充当所有模型的通用张量运行时 |

调用方向：标签业务（位于另一个仓库，见上） → PP-OCR 执行端、OpenCV 工具；PP-OCR 执行端 → Vision 中立数据。Vision 核心与 PP-OCR 执行端都不引用标签业务。检测候选策略若后来有第二个真实业务消费者，再以共同需求为依据提取；目前不为假想消费者建通用后处理工程。

## 工程目标形态

第一阶段将 `DP.Vision.Onnx` 和 `DP.Vision.OnnxDetection` 的**推理代码**归为 `DP.Vision.PPOcr.Onnx`（或团队统一的 `PPocr` 命名形式；命名见「待决项」#1）：仅依赖 `DP.Vision`/必要的中立契约与 `Microsoft.ML.OnnxRuntime`，不依赖 OpenCV。识别、检测可作为工程内目录，不按一个类一个工程拆分。检测的 OpenCV 后处理移动到 `DP.LabelInspection.Runtime`（落点见「待决项」#3）；不应因为后处理迁移而删除其他 Vision 模块需要的 OpenCV 依赖。

如果实际部署明确要求“仅识别时不能携带检测模型/代码”，再考虑拆成 `DP.Vision.PPOcr.Onnx.Recognition`、`DP.Vision.PPOcr.Onnx.Detection` 两个发布工程；这是**部署诉求**而非抽象原则（触发条件见「待决项」#2）。未来真的需要 OpenVINO 时再新增 `DP.Vision.PPOcr.OpenVino`，只提取 ONNX/OpenVINO 确实共用的预/后处理或模型契约。不要预建通用 `IInferenceEngine`、张量插件目录或空 OpenVINO 工程。

## 两个模型任务的接口草案

下列名称仅表示职责，最终在设计评审时以真实调用点命名；不要把第三方 `Mat`/`Tensor`/`InferenceSession` 暴露给外部。

1. `PPOcrDetectionInput`：调用者准备的连续 `float` NCHW 输入、模型宽高、原图宽高和显式坐标映射。维度、通道顺序、归一化约定与允许的尺寸由模型执行端验证；不得在任务中隐式重做缩放。预处理可先留在标签运行时，必要时用其现有 OpenCV 工具。
2. `PPOcrDetectionOutput`：模型身份（实际模型字节哈希）、概率图的宽高及行主序数值、输入/原图尺寸和映射元数据。复制出模型会话所有的内存；不可返回指向 ONNX 原生缓冲区的悬空引用。概率值非有限、维度不匹配、超预算时明确失败。**不包含候选框或业务阈值。**
3. `PPOcrRecognitionInput`：现有 `TextLineInput` 能表达归一化的识别输入及内容宽度，可复用或迁移到更恰当位置；原图 ROI 身份须由组合者保留。
4. `PPOcrRecognitionOutput`：实际模型哈希、识别输出时序/类别数、类别概率（或经证明足够且不丢所需证据的时间步观测）及经模型元数据验证的字典。CTC 解码和 `TextLineRecognition` 的形成由外部组合者负责；若现有下游依赖时间步置信度、token 与文本，不得在迁移中丢失或改变其定义。

内存/资源约定：任务输出为脱离会话的独立只读**快照**——原先并列的“或显式 `IDisposable` 租约”不再作为备选保留：本方案的模型输出尺寸可控，快照优先；仅当实测字节预算不可控时再单独复议。同时明确最大尺寸/字节预算、调用与访问的并发规则（快照只读、无 `Dispose` 生命周期）、取消仅为协作式；异常时不返回半成品。对外不宣称跨执行端逐位相同结果。

## 业务侧的组合与兼容

- 在 `DP.LabelInspection.Runtime` 新建 DB 候选处理实现（落点见「待决项」#3）：消费概率图，执行二值化、轮廓/旋转过滤、均值筛选、边框扩张、去重与排序。目前的 `.3` / `.6`、水平角度、最多 64 候选等规则**先原样搬迁并通过回归锁定**，不要在搬迁时重新调参。候选框保持原图坐标与当前边界行为。
- 如业务仍以 `ITextRegionDetector` 接入，则由标签运行时的组合适配器实现它（落点见「待决项」#3）：`输入准备 → PP-OCR 检测任务 → DB 候选处理 → PixelBounds`。此接口不应由纯模型任务直接实现。若没有生产调用点，先不造一个没有调用方的适配器。
- `ITextLineRecognizer` 同理是已解码结果接口；`LabelInspectionHost.CreateEngine()` 可在业务侧组合识别预处理、模型任务和 CTC 解码。**批次与阶段的对应关系**（批次口径以本段为准）：第一批 = 阶段 2–3，识别的现有接口、行为与 CTC 解码位置全部不动，只做**检测的职责拆分**，以及识别/检测**推理代码的工程归属与命名**迁移——注意识别代码此时已随工程一并搬迁，但职责不拆；第二批 = 阶段 4 才拆识别职责，避免一次性改动影响真实 OCR 流程。
- 不把 PP-OCRv4 的输入尺寸、字典规则、CTC/DB 假设伪装成通用 ONNX 类型；模型版本不匹配时拒绝加载而不是静默复用旧后处理。

## 分阶段实施与验收

1. **证据基线**：冻结现有真实检测模型的概率图、最终候选数量/坐标、识别的 52 行 OCR/CTC 结果；盘点仓库外的 `OnnxTextRegionDetector` 消费者与构建发布方式（冻结样本的具体内容与存放位置见「待决项」#5，盘点范围见「待决项」#6）。原有文档提到的 37 个候选只是历史记录，验收应以可重跑的冻结样本为准。
2. **先拆检测**：提取检测输入准备、模型任务及标签侧 DB 候选处理；模型任务移除 OpenCV 包引用。使用冻结概率图单测后处理，无需启动 ONNX；真实模型集成测试验证坐标、顺序与候选结果不变。
3. **收敛工程与引用**：将执行端放入 `DP.Vision.PPOcr.Onnx`（名称见「待决项」#1），更新**两个仓库各自的 `.sln`**、跨仓项目引用、锁文件、架构测试、README/源码索引；在业务侧更新 `DP.LabelInspection.Runtime`，移除对旧检测工程 `DP.Vision.OnnxDetection` 的引用，**保留**其 `DP.Vision.OpenCv` 引用（DB 后处理落到业务侧后仍需 OpenCV）。不保留长期并行的同名旧实现；外部二进制兼容若有要求则另行计划迁移窗口（见「待决项」#4）。
4. **再拆识别（独立提交）**：在标签侧组合输入准备、模型任务、CTC 解码；真实 OCR 文本、token、置信度及模型身份回归不变。检查其他消费者再决定旧 `ITextLineRecognizer` 的兼容周期。
5. **第二执行端触发条件**：实际落地 OpenVINO 模型格式、可运行样本和基线后，再验证输出映射、字典、概率尺度及性能；只有两种真实实现存在时才抽取共享的内部执行 seam。业务候选处理应能消费两端同一语义的概率图，不要求像素级相同。

验收不只看编译：Vision 核心与 PPOcr 执行端无 OpenCV/标签引用；检测任务不返回候选框；概率图独立于已释放推理会话；相同输入的业务候选与现有实现一致；真实识别回归不退化；**两个仓库各自**的 net48 与 net8.0-windows 构建及相关测试通过。此文是待实施方案，不表示代码已完成迁移。

## 待决项

下列各项在正文中以「待决项 #N」标出引用位置。表中「阻塞」列说明未定会卡住哪个阶段——这些是需要人来拍的决定，不能由实施过程自行选择；未定之前按「临时口径」先行。

| # | 待决内容 | 阻塞 | 决定依据 | 临时口径（未定前） |
|---|---|---|---|---|
| 1 | 工程命名：`DP.Vision.PPOcr.Onnx` 还是团队统一的 `PPocr` 形式 | 阶段 3 | 团队/仓库命名约定 | 全文暂用 `DP.Vision.PPOcr.Onnx` |
| 2 | 是否按部署诉求拆为 `.Recognition` / `.Detection` 两个发布工程 | 阶段 3 | 实际部署是否要求“仅识别时不携带检测模型/代码” | 不拆；单工程内以目录区分 |
| 3 | 检测候选处理与 `ITextRegionDetector` 适配器的落点：`DP.LabelInspection.Runtime` 还是该仓库既有的 `DP.LabelInspection.Adapter.Vision` | 阶段 2 | `DP.LabelInspection` 既有分层约定 | 暂按正文写 `Runtime` |
| 4 | 旧 `ITextLineRecognizer` / `ITextRegionDetector` 的兼容周期与迁移窗口 | 阶段 3、阶段 4 | 仓库外消费者的实际情况 | 无生产调用点则不造适配器；有则另行计划窗口 |
| 5 | 冻结样本的具体内容与存放位置（概率图、候选数量/坐标、52 行 OCR/CTC 结果） | 阶段 1 | 阶段 1 实施者产出 | 以可重跑为准，不引用历史 37 候选 |
| 6 | 仓库外 `OnnxTextRegionDetector` 消费者的盘点范围（`DP.WorkFlow`、`DP.LabelInspection-baseline`、`samples/` 等） | 阶段 1 | 阶段 1 盘点结果 | 本仓库 `src/` 内已确认无使用点 |
