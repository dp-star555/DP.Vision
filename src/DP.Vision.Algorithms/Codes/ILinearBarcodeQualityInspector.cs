using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>可独立于QR质量和码读取替换的一维码质量接口。</summary>
public interface ILinearBarcodeQualityInspector : IBarcodeQualityInspector { }
