using System;
using System.Collections.Generic;
using System.Linq;
using DP.Vision.UI;
using Controls = System.Windows.Controls;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace DP.Vision.Demo;

internal static partial class Program
{
    private sealed class PatternSource : IImageSource
    {
        internal PatternSource(int width, int height)
        {
            Info = new ImageInfo(width, height, EPixelLayout.Gray8);
        }

        public ImageInfo Info { get; }

        public IImageSource Retain()
        {
            return new PatternSource(Info.Width, Info.Height);
        }

        public IImageSource ReadTile(int level, int x, int y, int size)
        {
            int factor = 1 << level,
                width = Math.Min(size, (Info.Width + factor - 1) / factor - x * size),
                height = Math.Min(size, (Info.Height + factor - 1) / factor - y * size);
            var pixels = new byte[width * height];
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    pixels[row * width + col] = (byte)(
                        (((x * size + col) * factor / 32 + (y * size + row) * factor / 32) % 2) == 0 ? 50 : 80
                    );
                }
            }

            return VisionImage.CopyFrom(new ImageInfo(width, height, EPixelLayout.Gray8), pixels);
        }

        /// <summary>按原图字节位置生成棋盘像素，无需分配完整原图。</summary>
        public void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (
                sourceOffset < 0
                || destinationOffset < 0
                || count < 0
                || (long)sourceOffset + count > Info.ByteLength
                || (long)destinationOffset + count > destination.Length
            )
                throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count; i++)
            {
                int offset = sourceOffset + i;
                destination[destinationOffset + i] = (byte)(
                    (offset % Info.Width / 32 + offset / Info.Width / 32) % 2 == 0 ? 50 : 80
                );
            }
        }

        // 此演示源只根据坐标计算像素，没有需要归还的数组或外部句柄。
        public void Dispose() { }
    }
}
