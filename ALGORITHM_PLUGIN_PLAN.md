# 视觉算法插件与 Workflow 接入实施方案

状态：基础设施、现有 13 个算法节点、检查/准备诊断、资源目录、显式配置升级及条码/水平单行 OCR 独立节点包已实现。2026-10-01。实际部署与当前边界见 [ALGORITHM_PLUGINS.md](ALGORITHM_PLUGINS.md)；以下保留设计与分批验收约束。真实 OCR 识别模型及实机许可仍需现场复核。

目标：启动时发现节点和引擎；节点按配置选择算法实现；新增节点、能力和实现通过新增插件完成，已有宿主不列举具体引擎。功能分类、算法能力和部署包是三个独立维度，不建立“功能 × 引擎”的空项目矩阵。

## 1. 已有代码与真正缺口

| 已有位置 | 现状 | 实施动作 |
| --- | --- | --- |
| [算法契约](src/DP.Vision.Algorithms/DP.Vision.Algorithms.csproj) | netstandard2.0，中立接口与部分纯 C# 算法 | 增加插件契约，不搬迁全部已有算法 |
| [模板定位契约](src/DP.Vision.Algorithms/Location/TemplateLocation.cs) / [OpenCV 实现](src/DP.Vision.OpenCv/Location/OpenCvTemplateLocator.cs) | 平移模板匹配已经存在 | 作为首个接入实例，不重复开发模板算法 |
| [块异常检测](src/DP.Vision.Algorithms/Anomaly/IPatchAnomalyDetector.cs) | OpenCvPatchAnomalyDetector 与 OpenCvCnnPatchAnomalyDetector 已实现同一接口 | 验证同一能力多个实现；既有模型兼容检查保留 |
| [采集模块加载器](src/DP.Vision.Acquisition.Runtime/VisionAcquisitionDriverModuleLoader.cs) | 扫描 DLL 找 Driver Module；Manifest 不是发现前提 | 保留领域规则，底层加载委托共用运行时 |
| [Workflow 节点入口](../DP.WorkFlow/src/Workflow/Nodes/DP.WorkFlow.Nodes.Vision/Catalog/WorkflowImageRuntimePluginModule.cs) | Runtime Module 同时登记节点模型和 Handler | 复用现有插件入口，新增节点不需要新的 Workflow 插件协议 |
| [Workflow 插件加载器](../DP.WorkFlow/src/Workflow/Kernel/DP.WorkFlow.Abstractions/Plugins/WorkflowPluginLoader.cs) | Manifest 分 runtime/studio/winForms/wpf 组；已有包级 ALC 与依赖解析 | 保留分组规则，抽取或接入共用包加载机制 |
| [样例宿主](../DP.WorkFlow/samples/DP.WorkFlow.WinForms.Sample/Form1.cs) | 手动 new 模块与 OpenCV 实现；按接口注册全局实例 | 改为发现模块和提供节点算法绑定服务；两个桌面宿主同步 |
| [能力解析](../DP.WorkFlow/src/Workflow/Kernel/DP.WorkFlow.Runtime/Execution/WorkflowNodeExecutionContext.cs) | GetRequiredCapability 只查全局 IServiceProvider | 算法改由节点绑定服务按位置和槽位取得；保留通用宿主服务 |
| [运行准备](../DP.WorkFlow/src/Workflow/Kernel/DP.WorkFlow.Abstractions/Execution/IWorkflowRunPreparationService.cs) | 已分准备、旧资源退役，嵌套运行不能退役根资源 | 接入候选算法计划，保留所有权约束 |
| [运行宿主](../DP.WorkFlow/src/Workflow/Kernel/DP.WorkFlow.Runtime/Hosting/WorkflowRuntimeHost.cs) | 先检查全局能力，再 PrepareAsync；准备输入是平铺节点列表 | 调整算法能力声明，并补计划位置与提交/回滚协议 |
| [文档持久化](../DP.WorkFlow/src/Workflow/Persistence/DP.WorkFlow.Persistence.Json/WorkflowDocumentJsonStore.cs) | 支持 Unknown 节点保留原始配置 | 复用缺节点插件时的保存与诊断行为 |

本次未改动用户已有的 EAlgorithmStatus.cs 修改。

## 2. 工程归属与依赖

拟新增两个固定基础设施工程：

- `DP.Plugins.Runtime`：领域中立的包加载会话、程序集与依赖解析、共享契约身份、加载诊断。初期放在本仓库 src，命名与公开契约不依赖 Vision 或 Workflow。后续可独立发布。
- `DP.Vision.Algorithms.Runtime`：算法模块发现、目录组合、配置绑定、资源租约、准备与执行计划。引用算法契约和共用加载运行时，不引用 Workflow/UI 或具体引擎。

共用加载层只处理“如何加载一个包”，不解释节点参数、相机 Source、算法能力或检测结论。算法目录与采集目录仍各自拥有；Workflow 的 Manifest 分组适配也仍留在 Workflow。

这是对之前“只增加一个共用运行时”的数量估计的修正：若同时保持契约不承担加载/生命周期，共用加载与算法运行职责应分别放置。两个工程数量固定，不随能力和引擎相乘。Workflow 桥接先放入已有 Nodes.Vision 工程的独立目录，不为了桥接再新增一个 DLL。

建议目标框架：

| 工程 | 目标 | 原因 |
| --- | --- | --- |
| 算法契约及新增能力契约 | netstandard2.0 | 已有 net48 与现代 .NET 调用方共用 |
| DP.Plugins.Runtime | netstandard2.0;net8.0 | 标准版本供现有采集运行时/net48 使用；现代版本实现 ALC/AssemblyDependencyResolver |
| DP.Vision.Algorithms.Runtime | netstandard2.0 | 算法编排本身无需平台加载 API；通过共用加载层取得模块 |
| 既有 OpenCV/ONNX 等引擎 | 保留现有目标框架 | 不因插件化统一重定向所有项目 |
| Workflow 视觉节点与桥接 | 保留 net8.0 | 当前节点工程是 net8.0，不能直接引用仅 net8.0-windows 的运行时 |

net48 不支持本系统的插件私有依赖多版本隔离；共同依赖统一到验证过的版本。net8 包私有托管依赖按包级 ALC 加载；共享契约复用宿主/契约目录的同一份程序集，不承诺原生 SDK 的完整隔离。跨目标构建及实际运行测试通过后才确认上述目标组合，不把 netstandard 构建成功等同于 net48 运行通过。

## 3. 扩展协议

以下职责对应已实现的插件契约与运行时；精确签名以源码为准。

| 概念 | 负责的事情 | 不负责的事情 |
| --- | --- | --- |
| VisionCapability 元数据 | 稳定能力 Id、分类、显示名；中立接口表达调用语义 | 推断 Workflow 节点、选择具体实现 |
| IVisionAlgorithmModule | 明确登记实现描述与工厂；无重资源的入口 | 在登记时加载模型或申请设备 |
| VisionAlgorithmDescriptor | 实现 Id/版本、能力接口、附加特征、参数结构版本、输入约束 | 假定某台机器某份模型一定可用 |
| 算法工厂 | 解释私有配置，声明依赖与资源身份，验证和创建，声明共享/并发策略 | 自行选择其他引擎、永久静态缓存 |
| 算法目录 | 组合并冻结描述，验证标识、接口和工厂一致性 | 持有执行中模型和图像 |
| 算法准备/资源管理 | 明确绑定、候选资源租约、兼容校验、回滚与释放 | 厂商构造细节与业务 NG 判断 |
| Workflow 算法绑定服务 | 将节点配置映射到算法请求，向 Handler 提供已绑定能力 | 修改算法标准接口、创建厂商对象 |

模块使用显式登记列表，不引入 Source Generator。中立接口不改成统一 object 输入/输出；工厂协议可以用 Type 做适配，但登记和绑定必须验证实际对象满足声明的接口，Handler 最终通过强类型接口执行。

工厂依赖必须可声明和解析。例如 OCR 声明预处理能力及配置要求；准备过程绑定明确选择的预处理实现后传给工厂。检测依赖循环，缓存身份包含影响行为的依赖绑定。

IMaskedBarcodeReader 属于读码能力的附加特征，不自动生成第二个读码节点。不能仅凭接口继承关系合并所有节点；一维码/QR 质量检查仍按各自能力语义定义。

## 4. 加载与插件包

宿主创建一个长期存活的包加载会话，Workflow、采集和算法发现适配器注入同一会话。不得各自 new 加载器并建立三份包上下文。模块类型只实例化一次并缓存；算法实例在准备阶段另行管理。

发现协议保留差异：

1. 采集仍按 Driver Module 接口扫描，不把 plugin.json 恢复为强制要求。
2. Workflow 继续接受当前 Manifest 分组；实现“投放节点 DLL 即发现”时增加 runtime 模块扫描适配，并与 Manifest 发现去重。
3. 算法按 IVisionAlgorithmModule 发现；依赖 DLL 与原生 DLL 不当作算法入口。
4. 包索引可由构建生成，声明入口、平台分组、契约/依赖及版本，不重复声明所有算法。模块登记是算法目录的来源。
5. 必须保证 DLL 扫描不会实例化未请求的 UI Module，也不以加载 WPF 依赖为发现算法的必要条件。

可约定每个 plugins 直接子目录是一个包；根目录散放 DLL 作为一个兼容包处理。包内可包含节点、契约、实现、原生库和资源。相同程序集副本按已识别身份去重；不同内容但相同身份报告部署冲突，不以先加载者获胜。

新能力契约先建立共享目录，再加载引用它的节点与引擎。多个包携带同一扩展契约时仍复用一份；不同私有引擎依赖不能因名字相同就强制共享。明确的共享契约规则替代“所有 Default 已加载程序集都共享”。

单包故障报告原始 LoaderExceptions 等原因；依赖该包的包标记不可用，无关包可继续发现。模块登记到私有候选列表，成功后合并；失败不留下半套能力。重复实现 Id 或能力 Id 的不兼容定义列出全部来源，相关项不可用，不自动选赢家。

首版插件更新重启生效，不实现程序集热替换和卸载。

## 5. 节点配置与绑定

节点模型包含每个算法槽位的选择：能力 Id、实现 Id、参数结构版本、初始化配置、资源引用及必要的依赖选择。标准调用参数仍保持既有强类型模型；第三方特有参数独立保存。

解析优先级：节点明确选择 → 配方/机器明确配置的默认实现 → 缺选择错误。不按目录顺序或“随便选唯一项”隐式决定。旧配方没有选择时，由显式迁移规则固定到此前宿主使用的实现，不能每次运行动态猜测。

节点绑定键至少区分：准备计划身份、稳定子计划路径、NodeId、算法槽位。执行计划还区分根/嵌套运行身份。节点循环重入和并行 Token 是否需要独立执行状态，由共享策略决定，不把所有调用都锁到同一个可变实例。

现有 WorkflowRunPreparationContext.Nodes 是平铺列表，缺少子计划路径。实施时增加位置描述，或向视觉桥接传递可遍历的绑定计划；不能只用 NodeId 建表，也不能借助 UI 的节点实例引用充当稳定键。

首版建议：Handler 从全局服务取得中立的 Workflow 算法绑定服务，再按执行计划位置/槽位取得已准备的类型化能力。现有 GetRequiredCapability 保留用于采集、帧作用域、IO 等全局宿主服务；不让通用 Workflow 内核认识 OpenCV 或模型格式。

## 6. 准备顺序与提交

当前 WorkflowRuntimeHost 在 PrepareAsync 之前执行全局能力校验。如果仍声明 ITemplateLocator 必须已是全局实例，将在工厂创建前错误失败。

首批迁移节点保留算法接口能力声明；IWorkflowNodeCapabilityProvider 让全局校验识别节点槽位，实际实现仍由算法准备阶段逐节点校验。未迁移节点保留原能力验证。不得删除整个通用能力校验，也不把“目录里有实现”伪装成“已经拥有实例”。

准备事务顺序：

1. 编译并冻结节点配置及计划位置；确定选择、参数迁移和依赖图。
2. 校验描述、支持特征、静态资源及模型兼容约束。
3. 工厂校验并创建/申请资源；必要时预热。资源引用解析到固定版本/不可变快照。
4. 全部必需槽位成功后得到候选算法计划；其中包含租约和绑定表。
5. 所有其他准备参与者也成功后，根运行在明确边界提交候选计划并退役旧计划。
6. 绑定完成后执行首节点；运行结束/停止时归还本轮租约。

当前 PrepareAsync 返回 ValueTask，没有显式候选对象/Abort。不能简单把新候选写入共享服务：后续采集准备失败、取消或资源退役失败时会残留。需要一个准备协调者与可提交/回滚的参与者协议，或在宿主中显式管理候选租约；候选 ownership 在成功提交前不能转给正式绑定表。

已有采集的“根运行拥有退役责任、嵌套运行只准备”的规则保留。根和嵌套请求共用计划资源引用，但嵌套请求不能释放根计划，不能误把新的子运行 RunId 当作不存在的根资源。

首版不增加运行中无缝切换：当前 WorkflowRuntimeHost 已拒绝运行中 Configure，先在停止/新一轮开始边界准备与提交。后续需要在线切换时再增加代际绑定；旧在途调用仍完成后才释放。

## 7. 资源、并发与输入

资源共享由工厂声明，不规定同模型必然共享同一个算法对象。模型只读数据、推理会话、节点参数及临时状态分别考虑。

资源身份包含实现/插件版本、资源内容版本、影响构造的初始化设置、依赖绑定和运行后端。由工厂规范化，运行时不凭 JSON 字符串或文件路径猜测。阈值等调用参数不进入资源键，也不写到共享对象的可变字段。

同一键并发申请只创建一次；申请者取消独立，全部无人需要时才取消可取消的创建。失败可在修复后重试，未完成/部分创建对象由创建方清理。避免持有全局缓存锁执行模型 IO 或原生创建。

共享策略至少支持独占、共享但串行、共享且并发安全。首版不强制实现实例池；如果某实现需要池而运行时尚不支持，应报告不支持，不能违反并发契约。

计划租约与在途调用都结束后才释放；所有者由运行时确定，工厂提供销毁逻辑。首版最后使用者退出即释放，空闲缓存以后按性能证据增加。

模型文件按快照加载；验哈希和实际读取必须针对同一份字节/不可变文件。仅“预检路径存在”不能保证运行时还是同一模型。

上游动态输入不能在启动前全部验证。例如现有模板节点从上游取得模板 ImageFrame，准备时只能检查绑定和静态约束；实际模板尺寸、帧身份、区域及像素格式在调用前校验。输入图像和返回证据租约保持现有语义。不能承诺预检消除所有运行异常。

## 8. 参数面板与配方兼容

首版提供基础类型、默认值、范围、枚举、说明与结构版本。工厂最终校验；UI 校验用于提前反馈。提供资源类型/角色而不是仅裸字符串路径。

已有标准节点属性继续显示；实现专有参数显示在独立分组。保存时保留实现 Id、参数版本及完整配置，Unknown 节点沿用已有原始配置保留机制。

切换实现后标准参数按明确语义复用；专有参数经迁移或重置确认，不直接传给其他引擎。参数缺失、旧版本、新版本及未知字段的策略写入各工厂规则；未知字段不能静默丢失。纯调整分类与显示名不要求改配方。

## 9. 分批实施与验收

### P1：共用加载会话

交付 DP.Plugins.Runtime，现有采集与 Workflow 加载器保留公开外观但转委托底层。独立编译测试插件包覆盖共享契约、副本去重、包私有依赖和诊断；不能仅复制已由宿主加载的测试程序集来证明插件加载成功。

验收：同包多种入口只有一个上下文；新扩展契约跨包保持类型身份；net48 不允许的版本冲突被诊断；net8 私有托管依赖加载符合策略；采集不需要 Manifest 的现有回归继续通过。

### P2：算法目录与工厂

交付插件契约、不可变目录、模块发现与类型化工厂适配。OpenCV 登记模板定位与两个块异常检测实现，ZXing 登记读码及掩码特征；纯 C# 实现由内置模块显式贡献。新增类型只在引擎内登记，发现阶段不创建算法资源。

验收：真实模块发现，描述/接口一致，重复标识及登记中途异常原子处理；同能力多个实现并存；通过独立扩展契约 + 实现 DLL 证明运行时无需修改能力白名单。

### P3：资源准备与事务

交付租约、资源规范身份、失败回滚、并发调度和计划提交。测试 fake 工厂可计数模型创建/释放及阻塞调用，真实 OpenCV CNN/ONNX 模型用已有可用资源另做验证。

验收：同资源多申请只创建一次；不同初始化/版本不误共享；末个计划租约与调用退出后才释放；创建失败/取消/后续参与者失败没有泄漏；嵌套准备不释放根资源。

### P4：Workflow 最小端到端接入

首个生产接入节点使用已有 Vision.LocateTemplate，不改其检测语义；增加实现选择、桥接准备和 Handler 绑定解析。同步 WinForms/WPF 样例启动装配，移除这条路径的具体引擎 new。第二个真实验证使用块异常检测的两个实现，覆盖节点选型与模型不兼容。

增加参数选择、持久化迁移、准备失败诊断和按计划路径的绑定。注册复合准备协调者，不用 IServiceProvider.Add 覆盖已有采集准备/释放服务。

验收：两个同类节点可选择不同实现/初始化；同名子流程节点不串绑定；算法工厂尚未创建时不会被全局校验误判；准备失败旧绑定不变；输入输出、图像租约和既有坐标系回归通过。

### P5：证明新增能力只增加插件

建立一个独立的测试能力契约 DLL、Workflow 节点模块 DLL 和引擎模块 DLL。宿主测试工程只引用既有基础设施，通过目录发现与持久化加载节点，不编译期引用新能力程序集。

验收：投放后出现节点、配置实现、完成准备与调用；缺引擎可编辑但运行阻断；缺节点保留 Unknown 原始配置；模板参数与特有设置升级可诊断。生产新增算法以同样方式发布。这个验收区别于仅证明“新增已有能力的引擎”。

之后再逐个迁移其他既有节点，不一次性改完全部接口/Handler，不声称不存在的 HALCON 算法已实现。

## 10. 首批异常验收矩阵

| 场景 | 检查阶段 | 期望 |
| --- | --- | --- |
| 缺依赖、目标框架/架构不匹配 | 包发现/加载 | 原因与来源明确；无关包可发现 |
| 同包同时有节点、算法、采集入口 | 加载 | 复用包上下文和契约；按所需接口发现 |
| 扩展契约出现在多个包 | 加载 | 身份一致复用；内容/版本冲突不靠顺序选取 |
| Module 登记一半抛异常 | 目录组合 | 撤销该 Module 候选登记 |
| 实现 Id 重复 | 目录组合 | 冲突来源完整，相关实现不进入可用目录 |
| 节点未指定且没有明确默认值 | 准备 | 报缺选择，不隐式挑引擎 |
| 两个节点同资源不同调用阈值 | 准备/执行 | 资源按策略共享，参数分别传入 |
| 相同路径文件被覆盖 | 准备/执行 | 新版本重新准备，旧计划持有固定快照 |
| CNN 骨干/放大倍数与参考模型不匹配 | 准备或动态输入校验 | 拒绝；不自动重新训练或换实现 |
| 根/子计划使用相同 NodeId | 绑定/执行 | 按计划位置与槽位区分，不串绑 |
| 准备算法后采集准备失败 | 准备回滚 | 新候选释放，旧正式绑定不变 |
| 串行共享对象遭并发调用 | 执行 | 统一调度，等待可取消 |
| 取消发生在原生调用期间 | 停止 | 不提前 Dispose 或归还输入图像 |
| 原生调用不返回 | 停止 | 保持在途状态，不伪造已释放；硬停止另用进程策略 |
| 动态模板尺寸不适用 | 执行前校验 | 明确输入错误，不冒充准备已保证成功 |
| 升级插件参数或缺节点插件 | 文档/准备 | 迁移或保留配置，失败可诊断 |

## 11. 构建与发布验证

Vision 新代码测试使用现有 MSTest 约定，Workflow 使用原有 xUnit 约定。按阶段运行关联项目测试；加载层变更必须包含采集模块回归与 Workflow 插件加载回归。资源管理必须包含并发、取消和回滚测试，不能只验证正常构造。

测试覆盖 net48 和现代 .NET 实际进程；新扩展测试插件单独构建输出。针对仅 net8.0 的 Workflow 保持它的原目标，不为了 Windows 引擎引用将通用内核整体改成 Windows 专用。

真实引擎测试使用已有资产与明确环境；没有模型、许可或设备时报告未验证项目，不以 fake 工厂测试替代原生验证。每阶段在文档记录交付、实际命令与结果，再将对应状态由“待实施”改为“已验证”。

## 12. 首批实施记录（2026-10-01）

| 阶段 | 已交付与实际验证 |
| --- | --- |
| P1 | DP.Plugins.Runtime；采集与 Workflow 加载器复用会话；无 Manifest Runtime 节点扫描；contracts 扩展契约预注册；独立 EngineV1/EngineV2 包证明 net8 私有依赖隔离、net48 统一版本拒绝、同身份不同内容冲突 |
| P2 | 30 个契约的能力元数据；Managed、OpenCV、ZXing、PP-OCR 模块；不可变目录；失败模块登记回滚与重复实现拒绝；读码仍是一个能力附加 masked 特征 |
| P3 | 版本化配置快照、资源键、单次并发创建、独占/串行/并发策略、调用与计划租约、取消/失败回滚；创建中的依赖持续持有；后续准备/旧资源退役/运行作用域取得失败不发布候选 |
| P4 | Vision.LocateTemplate 的节点选择与 Handler 绑定；通用能力槽位预检；根、子流程、恢复与联合参与者准备事务；WinForms/WPF 示例部署算法包；通用实现候选与按描述生成的参数编辑；真实模板/图像证据、原生 CNN 模型快照/共享/兼容拒绝测试 |
| P5 | DP.WorkFlow/tests/PluginFixtures/Intensity.Contracts、Intensity.Engine、Intensity.Nodes 独立构建；测试宿主不引用新增能力，目录发现后经 JSON 保存/加载及运行成功；缺引擎、缺节点、版本错误、并行宿主与同名子流程绑定测试 |

验证命令（在各仓库根目录执行）：

```powershell
# DP.Vision
dotnet test tests/DP.Vision.Algorithms.Runtime.Tests/DP.Vision.Algorithms.Runtime.Tests.csproj -f net8.0-windows
dotnet test tests/DP.Vision.Algorithms.Runtime.Tests/DP.Vision.Algorithms.Runtime.Tests.csproj -f net48
dotnet test tests/DP.Vision.Acquisition.Tests/DP.Vision.Acquisition.Tests.csproj -f net8.0-windows
dotnet test tests/DP.Vision.Acquisition.Tests/DP.Vision.Acquisition.Tests.csproj -f net48

# DP.WorkFlow
dotnet test tests/Workflow/DP.WorkFlow.Core.Tests/DP.WorkFlow.Core.Tests.csproj
dotnet test tests/Workflow/DP.WorkFlow.Runtime.Tests/DP.WorkFlow.Runtime.Tests.csproj
dotnet test tests/Workflow/DP.WorkFlow.Nodes.Vision.Tests/DP.WorkFlow.Nodes.Vision.Tests.csproj
dotnet test tests/Workflow/DP.WorkFlow.UI.Windows.Tests/DP.WorkFlow.UI.Windows.Tests.csproj
dotnet build samples/DP.WorkFlow.WinForms.Sample/WinFormsApp_test.csproj
dotnet build samples/Legacy/WpfApptest/WpfApptest.csproj
```

已通过：算法运行时两个框架各 16 项，采集两个框架各 199 项，Workflow Core 46 项、Runtime 84 项、Nodes.Vision 54 项、Windows/UI 355 项，UI.Shared 63 项、Composite 7 项。两个桌面示例构建为 0 警告、0 错误。

本节为首批历史记录；既有节点后续迁移结果见下一节。后续已补资源根目录、诊断界面与独立条码/单行 OCR 节点包。当前帧预览重复 Id 限制、机器默认实现表及真实 PP-OCR/许可/设备验证边界，详见 [使用文档](ALGORITHM_PLUGINS.md)。本记录不把算法租约验收等同于真实相机和许可验收。

## 13. 既有节点迁移（2026-10-01）

在首批平移模板定位基础上，迁移了文件/文件夹、预处理、阈值/形态学、Blob/筛选、颜色、边缘、卡尺/鲁棒直线、旋转尺度模板定位，共 13 个算法节点。节点持久化实现选择，旧配方缺字段时保留原实现；未注册绑定的旧宿主仅支持默认选择及空初始化配置。相机采集 2 个及直接几何运算 4 个保持原机制。

文件夹增加解码回调接口，保留旧入口和根/嵌套游标规则。制作界面的文件解码通过运行时适配器调用，不再直接创建 OpenCV。两个示例移除全局算子实例注册及引擎编译引用；三个引擎携带第三方依赖用于独立包投放。

属性面板根据槽位生成实现选择、配置版本、完整初始化/依赖及专有参数；回滚和撤销后读取当前配置对象，修复编辑项持有旧选择的问题。保留未安装选择并在准备阶段阻断。

本轮通过：Nodes.Vision 73 项、Windows/UI 362 项、UI.Shared 63 项、算法运行时 net48/net8 各 16 项，共 530 项；两个桌面示例构建 0 警告、0 错误。独立进程以实际部署的三个算法包、无引擎编译引用完成文件→预处理→Blob→颜色，确认私有包上下文及托管/原生依赖解析。

## 14. 嵌套配置与节点工作台（2026-10-02）

按工厂声明逐层生成依赖候选及专有参数，包含未选择、缺插件、未知槽位、循环和声明失败提示。保留原始 JSON 修复入口；显式清空当前层配置后才能切换有配置的实现，操作可撤销。动态声明变更不会丢弃旧依赖。属性投影不执行工厂准备或加载模型。

Workflow 的节点编辑上下文和属性页携带宿主候选/附加属性扩展，WinForms/WPF 工作台使用隔离编辑副本。取消保持原配方，应用整体提交为一次撤销步骤。保留旧构造入口及默认无扩展行为。WinForms 在候选或描述变化时重建编辑器；值变化时同步相关参数，保留焦点。

验证包含嵌套选择、参数范围失败回滚、撤销后旧属性条目、清空/切换/删除、动态声明及未知字段保留、循环/声明异常、稳定属性身份、隔离编辑及两套真实控件创建。实际独立 OCR 包通过目录发现后显示模型文件与预处理依赖，无识别模型也能完成配置和撤销。

本阶段关联回归：Windows/UI 386 项、UI.Shared 65 项，共 451 项通过；WinForms/WPF 示例均为 0 警告、0 错误。真实模型识别、相机采集与许可继续按现场条件复核。
