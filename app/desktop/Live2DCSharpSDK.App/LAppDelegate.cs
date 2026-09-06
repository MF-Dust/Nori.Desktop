using Live2DCSharpSDK.Framework.Model;
using Live2DCSharpSDK.Framework.Rendering;

namespace Live2DCSharpSDK.App;

/// <summary>
/// アプリケーションクラス。
/// Cubism SDK の管理を行う。
/// </summary>
public abstract class LAppDelegate : IDisposable
{
    /// <summary>
    /// テクスチャマネージャー
    /// </summary>
    public LAppTextureManager TextureManager { get; private set; }

    public LAppLive2DManager Live2dManager { get; private set; }

    public CubismTextureColor BGColor { get; set; } = new(0, 0, 0, 0);

    public abstract CubismRenderer CreateRenderer(CubismModel model);
    public abstract TextureInfo CreateTexture(LAppModel model, int index, int width, int height, IntPtr data);
    public abstract TexturePixels DecodeTexture(string fileName);

    public void InitApp()
    {
        TextureManager = new LAppTextureManager(this);
        Live2dManager = new LAppLive2DManager(this);
        LAppPal.DeltaTime = 0;
    }

    /// <summary>
    /// 解放する。
    /// </summary>
    public void Dispose()
    {
        Live2dManager.Dispose();
    }
}
