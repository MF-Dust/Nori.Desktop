namespace Live2DCSharpSDK.App;

/// <summary>
/// 画像情報構造体
/// </summary>
public abstract class TextureInfo
{
    /// <summary>
    /// テクスチャID
    /// </summary>
    public int Id;
    /// <summary>
    /// ファイル名
    /// </summary>
    public string FileName;

    /// <summary>
    /// 画像の解放
    /// </summary>
    public abstract void Dispose();
};