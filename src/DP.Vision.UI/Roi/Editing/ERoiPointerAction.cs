using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.UI;

/// <summary>与输入设备无关的ROI指针动作，供鼠标、触笔或宿主输入适配使用。</summary>
public enum ERoiPointerAction
{
    /// <summary>指针按下。</summary>
    Down,

    /// <summary>指针移动。</summary>
    Move,

    /// <summary>指针释放。</summary>
    Up,
}
