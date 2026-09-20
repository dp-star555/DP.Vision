using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Algorithms;

/// <summary>文件解码能力，与相机采集和文件夹游标分离。</summary>
public interface IImageFileReader
{
    /// <summary>读取并解码单张图像，不自动降位深或更换后端。</summary>
    /// <param name="path">本地图像文件路径。</param>
    /// <param name="token">读取及解码边界取消令牌；原生解码不可抢占。</param>
    /// <returns>由调用者释放的只读像素租约。</returns>
    Task<IImageSource> ReadAsync(string path, CancellationToken token = default);
}
