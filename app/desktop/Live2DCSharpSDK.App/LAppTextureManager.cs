namespace Live2DCSharpSDK.App;

/// <summary>
/// 画像読み込み、管理を行うクラス。
/// </summary>
public class LAppTextureManager(LAppDelegate lapp)
{
    private readonly List<TextureInfo> _textures = [];

    /// <summary>
    /// 画像読み込み
    /// </summary>
    /// <param name="fileName">読み込む画像ファイルパス名</param>
    /// <returns>画像情報。読み込み失敗時はNULLを返す</returns>
    public unsafe TextureInfo CreateTextureFromPngFile(LAppModel model, int index, string fileName)
    {
        //search loaded texture already.
        var item = _textures.FirstOrDefault(a => a.FileName == fileName);
        if (item != null)
        {
            return item;
        }
        TexturePixels pixels = lapp.DecodeTexture(fileName);
        fixed (byte* data = pixels.Data)
        {
            // OpenGL用のテクスチャを生成する
            var info = lapp.CreateTexture(model, index, pixels.Width, pixels.Height, (nint)data);
            info.FileName = fileName;

            _textures.Add(info);
            return info;
        }
    }

    /// <summary>
    /// 指定したテクスチャIDの画像を解放する
    /// </summary>
    /// <param name="textureId">解放するテクスチャID</param>
    public void ReleaseTexture(TextureInfo info)
    {
        info.Dispose();
        _textures.Remove(info);
    }
}
