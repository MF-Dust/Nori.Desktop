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
		// 每个模型独占自己的 GL 纹理。候选模型与当前模型会短暂并存；
		// 按文件名复用会漏掉候选 renderer 的绑定，并在释放旧模型时误删共享纹理。
		return CreateTextureFromPixels(model, index, fileName, lapp.DecodeTexture(fileName));
    }

	/// <summary>只上传已解码的 RGBA8888 像素，不读取或解码文件。</summary>
	public unsafe TextureInfo CreateTextureFromPixels(
		LAppModel model,
		int index,
		string fileName,
		TexturePixels pixels)
	{
		fixed (byte* data = pixels.Data)
		{
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
