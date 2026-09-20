using System;
using System.IO;
using System.Linq;
using DP.Vision.UI;
using Controls = System.Windows.Controls;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace DP.Vision.Demo;

internal static partial class Program
{
    private sealed class ToolChoice
    {
        internal readonly ERoiTool Tool;
        private readonly string _label;

        internal ToolChoice(ERoiTool tool, string label)
        {
            Tool = tool;
            _label = label;
        }

        public override string ToString()
        {
            return _label;
        }
    }
}
