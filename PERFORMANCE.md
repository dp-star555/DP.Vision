# 同机优化复测：HALCON / 旧GDI / DP.Vision

测试工具保留在`../DP.LabelInspection/tools/DP.LabelInspection.CanvasBenchmark`，通过独立适配器引用DP.Vision。HALCON只在测试进程中使用，DP.Vision核心和控件不引用它。

## 方法与可比性

- Intel Core 7 250H / 20逻辑处理器，约16GB；Windows10；Intel Graphics；60Hz桌面。
- net48、x64、Release、1000×700控件；Gray8；相同原始HALCON图像/Region/XLD夹具。
- 5场景×4模式×2轮，共40独立进程；第二轮反转顺序；32/64位、原始点/游程数量及DwmFlush返回检查。
- `unified`是旧ImageViewerControl，`vision`是新DP.Vision.Winform完整XLD，`vision-lod`是同一新控件开启显示LOD。
- 新控件使用两槽只读租约输入池，保留Gray8，分块/缩略层、Region掩码和图层缓存。**这些是实现/显示策略变化，不是同一个像素管线的纯绘图库比较**。原始几何相同，不保证不同后端在缩小、抗锯齿、Region细碎像素覆盖上逐像素相同。
- 原图是4MP/16MP实在的输入字节，不是8K/16K按需生成源。两侧进程都保留相同HALCON测试源；内存不能解释成纯无HALCON部署基数。
- 各阶段预热3次；重绘/视图变化50次，几何重新适配16次，换图20次，桌面同步30次。
- 表格P50为两轮各自中位数均值；P95取较差轮。这里只给少量重复结果，不给跨机器或统计置信度承诺。
- 提交耗时不是屏幕FPS；未测GPU显存、输入到光子延迟、相机采集或算法吞吐量。

## 缓存重绘 P50，ms

| 场景 | HALCON | 旧GDI | 新控件完整XLD | 新控件显示LOD |
|---|---:|---:|---:|---:|
| 4MP纯图 | 0.68 | 7.60 | 3.83 | 3.67 |
| 16MP纯图 | 0.64 | 21.25 | 3.41 | 3.62 |
| 4MP＋10万Region游程 | 16.57 | 14.63 | 5.13 | 5.16 |
| 4MP＋10万XLD点 | 11.48 | 33.60 | 30.14 | 8.86 |
| 4MP＋4千游程＋2万点 | 4.00 | 12.57 | 8.90 | 5.42 |

## 连续换图 P50，ms

| 场景 | HALCON | 旧GDI | 新控件完整XLD | 新控件显示LOD |
|---|---:|---:|---:|---:|
| 4MP纯图 | 1.37 | 17.82 | 5.98 | 5.67 |
| 16MP纯图 | 3.77 | 61.35 | 7.20 | 7.44 |
| 4MP＋10万Region游程 | 17.37 | 28.16 | 7.19 | 7.42 |
| 4MP＋10万XLD点 | 11.75 | 43.67 | 30.96 | 10.60 |
| 混合 | 4.13 | 22.31 | 11.52 | 7.51 |

同一几何引用在换图时可复用显示缓存；每帧图像内容身份改变，图像块仍需刷新。新输入池不是无限队列，算法是否允许丢帧与预览策略无关。

## 内存和GC

全流程峰值Working Set的两轮均值：

| 场景 | 旧GDI | 新控件完整XLD |
|---|---:|---:|
| 4MP纯图 | 120.87MiB | 69.97MiB |
| 16MP纯图 | 334.38MiB | 116.03MiB |
| 10万XLD点 | 139.60MiB | 98.06MiB |
| 混合 | 143.64MiB | 82.63MiB |

新控件两轮、各场景的20次换图阶段Gen2次数均为0；不代表永远无GC、无泄漏或长期内存完全恒定。原始输入、HALCON源、转换、一次控件截图都计入全流程峰值；图块预算只约束缓存记账载荷。

## LOD不适合无条件开启

10万点每次重新适配/生成显示路径：

- 旧GDI：42.44ms。
- 新控件完整XLD：45.81ms。
- 新控件显示LOD：**68.60ms**。

这里包含HALCON提取、旧中立数据→DP.Vision适配，以及新路径/LOD构建。该阶段新几何对象每次都变化，所以不能命中旧显示缓存。说明**LOD的收益来自后续复用，不是构建本身免费**。新控件没有在所有项目上超过旧实现或HALCON。后台LOD预计算、更高效的几何适配、GPU后端还需要后续实现并复测。

## 8K/16K显示探针（不同口径）

`artifacts/native-net48/large-16384.txt`：按需生成Gray8图块的16384²源，首次显示约8.327ms、缓存重绘中位数约4.788ms；放大到1:1并平移后，记账显示缓存4MiB，配置总预算8MiB。WPF和net8也完成原生分块显示检查。

**这个测试没有分配整幅16K输入，不能与上表或原生HALCON整图测试直接比较，也不能用来宣称16K检测帧率。** 它证明图块请求与显示缓存不必随着整图像素数分配巨大BGR Bitmap。真实16K Gray8完整原图仍需要256MiB；真实文件I/O/解码/算法处理另测。

## 复现与原始数据

```powershell
# 从工作区根目录运行；比较工具需要兼容HALCON及许可。
dotnet build DP.LabelInspection/tools/DP.LabelInspection.CanvasBenchmark -c Release
powershell -ExecutionPolicy Bypass -File DP.Vision/tools/run-comparison.ps1 -OutputDirectory <新目录>
python DP.Vision/tools/summarize-comparison.py <新目录>
```

- 最终数据：[完整统计表](artifacts/comparison-final/tables.md)、[CSV](artifacts/comparison-final/summary.csv)。
- 40份原始JSON/控件PNG：`artifacts/comparison-final/`；不截取整个桌面。
- 精确与LOD的图像证据：`xld_100k-vision-r1.png`、`xld_100k-vision-lod-r1.png`。
- 原始历史数据保留在DP.LabelInspection/artifacts；没有用新数据覆盖旧基准。
- WPF当前做过功能/大图原生验证，**尚未完成同口径40进程性能对比**，不能声称其耗时等于上表WinForms数据。
