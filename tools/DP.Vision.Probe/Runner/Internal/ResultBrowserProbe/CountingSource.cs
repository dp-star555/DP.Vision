namespace DP.Vision.Probe;

internal static partial class ResultBrowserProbe
{
    /// <summary>原生回归专用源：统计实际图块读取，验证只切换图层时底图缓存未失效。</summary>
    private sealed class CountingSource : IImageSource
    {
        private readonly IImageSource _source;

        internal CountingSource(IImageSource source) => _source = source;

        public ImageInfo Info => _source.Info;

        public IImageSource Retain() => new CountingSource(_source.Retain());

        public IImageSource ReadTile(int level, int tileX, int tileY, int tileSize)
        {
            _readTiles++;
            return _source.ReadTile(level, tileX, tileY, tileSize);
        }

        public void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count) =>
            _source.CopyTo(sourceOffset, destination, destinationOffset, count);

        public void Dispose() => _source.Dispose();
    }
}
