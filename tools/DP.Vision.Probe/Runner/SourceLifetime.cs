using System;
using System.Drawing;
using DP.Vision.UI;

namespace DP.Vision.Probe;

internal static partial class Program
{
    /// <summary>在真实原生画布上验证客户释放、换图和清空待显示帧的源租约，不使用模拟控件。</summary>
    /// <param name="canvas">当前UI线程上的原生画布。</param>
    /// <param name="capture">执行实际绘制并返回独立截图。</param>
    private static void CheckSourceLifetime(IVisionCanvas canvas, Func<Bitmap> capture)
    {
        using var pool = new FrameBufferPool(new ImageInfo(1, 1, EPixelLayout.Gray8), 1, 1);
        if (!pool.TryRent(out var rented))
            throw new InvalidOperationException("No initial source slot.");
        using var writer = rented!;
        writer.Write(0, new byte[] { 160 }, 0, 1);
        var source = writer.Publish();
        canvas.SetImage(source, "source-owned", 200);
        source.Dispose();
        canvas.FitToWindow();
        if (pool.TryRent(out var premature))
        {
            premature!.Dispose();
            throw new InvalidOperationException("Canvas returned a live source prematurely.");
        }
        using (var image = capture())
        {
            if (image.GetPixel(image.Width / 2, image.Height / 2).R != 160)
                throw new InvalidOperationException("Canvas cannot read after caller disposal.");
        }

        using (
            var replacement = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray8), new byte[] { 73 })
        )
            canvas.SetImage(replacement, "source-replacement", 201);
        if (!pool.TryRent(out var reused))
            throw new InvalidOperationException("Old canvas source was not returned.");
        using var nextWriter = reused!;
        using (var pending = nextWriter.Publish())
        {
            if (!canvas.PostImage(pending, "source-pending", 202))
                throw new InvalidOperationException("Pending source rejected.");
            if (canvas.PostImage(pending, "source-stale", 201))
                throw new InvalidOperationException("Stale source accepted.");
        }
        canvas.ClearImage();
        if (canvas.DisplayedFrameId != null)
            throw new InvalidOperationException("Canvas was not cleared.");
        if (!pool.TryRent(out var returned))
            throw new InvalidOperationException("Pending source leaked on clear.");
        returned!.Dispose();
        Console.WriteLine(
            "PASS: unified source caller-disposal, replacement, stale-post and clear: "
                + canvas.GetType().FullName
        );
    }
}
