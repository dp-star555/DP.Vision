# 图像采集连接架构 V2 实施状态

实施基线：[ACQUISITION_CONNECTION_V2_PLAN.md](ACQUISITION_CONNECTION_V2_PLAN.md)（Approved design，分阶段 V2-0..V2-9）。

## V2-0：冻结证据与合并在研改动

状态：**已完成**（2026-09-21）

### 证据基线

| 项 | 值 |
|---|---|
| DP.Vision HEAD | `1361204` feat(halcon): 采集深化V1-E——真实流式长连接适配器 |
| DP.WorkFlow HEAD | `1cffe71` refactor(AR-21): 删除旧通用图像编辑页残留双轨 |
| DP.Vision 工作区 | 干净（仅剩未跟踪的 V2 计划文档，已入库本记录后为空） |
| 测试基线 | `dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**480 通过 / 0 失败** |

net8.0-windows 分套件：

- DP.Vision.Tests 115 · DP.Vision.Acquisition.Tests 110 · DP.Vision.Halcon.Tests 102
- DP.Vision.Basler.Tests 82 · DP.Vision.Algorithms.Tests 67 · DP.Vision.Acquisition.Integration.Tests 4

### 处理记录

1. **HALCON 在研流式分支**：已由并发进程提交为 `1361204`（21 文件，+2723/-108），
   构建通过、含新增流式测试（StreamSession/NeutralFrames/Faults/TriggerMapping/DeviceStream + TestDoubles）。
   本阶段未重写任何同名 HALCON 文件。
2. **工作区来源**：V2 计划文档为新增文件；HALCON 在研改动确认后合并入库。
3. **OnDemand 与 BufferedExternal 基线测试**：
   - OnDemand：`tests/DP.Vision.Acquisition.Tests/Contracts/AcquisitionModeContractTests.cs` 等契约级测试。
   - BufferedExternal：`BufferedExternalInboxTests`、`StreamingFrameSinkContractTests`、
     `HalconAcquisitionDeviceStreamTests`、`HalconStreamSessionTests`、`BaslerAcquisitionDeviceStreamTests`。

## 阶段状态总览

| 阶段 | 状态 |
|---|---|
| V2-0 冻结证据与合并在研改动 | ✅ 已完成 |
| V2-1 AcquisitionTypeCatalog 与自动 Module 发现 | ⏳ 待实施 |
| V2-2 机器相机定义与不可变 Composition | ⏳ 待实施 |
| V2-3 应用级连接生命周期 | ⏳ 待实施 |
| V2-4 TransferPolicy 与 Epoch 解耦 | ⏳ 待实施 |
| V2-5 面阵/线扫双节点模型 | ⏳ 待实施 |
| V2-6 Basler 迁移 | ⏳ 待实施 |
| V2-7 HALCON 迁移 | ⏳ 待实施 |
| V2-8 线扫与采集卡首个 Adapter | ⏳ 待实施 |
| V2-9 Acquisition UI、审计和运行优化 | ⏳ 待实施 |

每阶段完成时更新本文并记录提交、测试结果与验收证据。
