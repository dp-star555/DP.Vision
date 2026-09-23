namespace DP.Vision;

/// <summary>客户创建不可变图像的统一入口；隐藏内部像素缓冲区及临时租约。</summary>
public static class VisionImage
{
    /// <summary>复制紧密排列的原图像素；调用方随后修改输入数组不会改变图像。</summary>
    /// <param name="info">原图尺寸及像素布局。</param>
    /// <param name="pixels">调用方拥有的数组，长度必须等于info.ByteLength。</param>
    /// <returns>由调用方释放的图像源；检测、显示可以各自在内部保留独立租约。</returns>
    public static IImageSource CopyFrom(ImageInfo info, byte[] pixels)
    {
        return new MemoryImageSource(ImageBuffer.CopyFrom(info, pixels));
    }
}
