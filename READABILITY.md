# 代码排版与中文注释

本文记录全量注释整理批次。后续已完成单类型拆文件、功能目录及枚举E前缀整理，最新文件入口和测试数字见[STRUCTURE.md](STRUCTURE.md)及[VALIDATION.md](VALIDATION.md)。

## 全量整理范围

两个解决方案的源码、测试、工具、示例及源构建脚本中的现有说明性注释已统一为中文：

- C#普通注释和XML文档注释，包括算法、契约、运行时、存储、WinForms/WPF及回归测试。
- Python模块文档字符串、PowerShell注释及项目XML注释。
- 保留类型名、方法名、参数名、状态码和必要技术名称，便于对应实际代码；不改写运行时提示字符串。
- `bin`、`obj`中的生成代码以及`dist`、`artifacts`中的历史交付/验证副本不作为源码翻译对象。

本次转换了1037行C#和脚本注释，并另行整理Python/XML及脚本行尾说明。针对186个源码公开方法/构造函数新增495条参数说明，包含坐标系、单位、布局、资源所有权、固定版本、取消及默认值等。继承实现继续使用对应接口的XML文档，不重复维护两份语义。

参数和返回值XML标签分行，避免多个标签挤在一行。图像归一化、候选制作与正式验收、读取与质量、执行阻断与定位缺陷等边界均保留明确说明。未把此改动当作算法功能或工业精度升级。

## 一致的排版

两个项目均提交了`.editorconfig`、`.csharpierrc.json`和固定CSharpier 1.3.0的本地工具清单。在各解决方案目录执行：

```powershell
dotnet tool restore
dotnet csharpier format src tests tools samples
dotnet csharpier check src tests tools samples
```

采用4空格缩进、代码块换行及长参数折行；简单自动属性允许保留一行。CSharpier是排版检查基准，工具不是SDK运行时依赖。

## 验证

- 检查1538个C#注释块，并覆盖默认、net48、net8及netstandard条件编译视图：无英文说明候选，源码公开方法无缺失参数文档。
- 另检查5个Python/XML/CMD文档或注释块、2个PowerShell注释块，无英文说明候选；Python和PowerShell语法检查通过。
- 批量注释翻译及参数补充前后，C#语法标记一致；未修改标识符、字符串字面量、检测阈值或执行逻辑。
- CSharpier检查157个C#/项目文件通过。
- 两解决方案双框架构建零警告、零错误。
- Vision每框架68项共享测试、24项算法测试及原生控件探针通过。
- 标签每框架203项测试，真实OCR、物理分割/外观、DB发现及原生工作台回归通过。

日志：

- `DP.Vision/artifacts/comments-translation.log`
- `DP.Vision/artifacts/comments-parameters.log`
- `DP.Vision/artifacts/comments-audit.log`
- 两项目各自的`artifacts/comments-format.log`和`artifacts/comments-verification.log`

本次未重新发布安装包。WPF物理输入、工业准确率及相机吞吐的既有验证边界不变。
