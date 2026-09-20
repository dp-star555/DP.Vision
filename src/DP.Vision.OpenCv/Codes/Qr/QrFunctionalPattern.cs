using System;

namespace DP.Vision.OpenCv;

/// <summary>与内容无关的QR功能模块规则；格式位、版本位及数据仍视为未知。</summary>
internal static class QrFunctionalPattern
{
    internal static bool?[] Create(int dimension)
    {
        int version = (dimension - 17) / 4;
        var expected = new bool?[dimension * dimension];
        void Set(int x, int y, bool ink)
        {
            if (x >= 0 && y >= 0 && x < dimension && y < dimension)
            {
                expected[y * dimension + x] = ink;
            }
        }

        void Finder(int left, int top)
        {
            for (int y = -1; y <= 7; y++)
            {
                for (int x = -1; x <= 7; x++)
                {
                    bool inside = x >= 0 && x <= 6 && y >= 0 && y <= 6;
                    Set(
                        left + x,
                        top + y,
                        inside
                            && (
                                x == 0 || x == 6 || y == 0 || y == 6 || (x >= 2 && x <= 4 && y >= 2 && y <= 4)
                            )
                    );
                }
            }
        }

        Finder(0, 0);
        Finder(dimension - 7, 0);
        Finder(0, dimension - 7);
        for (int i = 8; i < dimension - 8; i++)
        {
            Set(i, 6, i % 2 == 0);
            Set(6, i, i % 2 == 0);
        }

        // 按QR规范要求，校正图形会覆盖其所在位置的时序图形。
        if (version >= 2)
        {
            int count = version / 7 + 2,
                step = version == 32 ? 26 : ((version * 4 + count * 2 + 1) / (count * 2 - 2)) * 2;
            var centers = new int[count];
            centers[0] = 6;
            for (int i = count - 1, position = dimension - 7; i > 0; i--, position -= step)
            {
                centers[i] = position;
            }

            for (int row = 0; row < count; row++)
            {
                for (int col = 0; col < count; col++)
                {
                    if (
                        (row == 0 && col == 0)
                        || (row == 0 && col == count - 1)
                        || (row == count - 1 && col == 0)
                    )
                    {
                        continue;
                    }

                    for (int y = -2; y <= 2; y++)
                    {
                        for (int x = -2; x <= 2; x++)
                        {
                            Set(centers[col] + x, centers[row] + y, Math.Max(Math.Abs(x), Math.Abs(y)) != 1);
                        }
                    }
                }
            }
        }

        Set(8, dimension - 8, true);
        return expected;
    }
}
