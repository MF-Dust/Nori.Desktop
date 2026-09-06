namespace Live2DCSharpSDK.App;

/// <summary>
/// 已解码的 RGBA8888 纹理像素。
/// </summary>
public sealed record TexturePixels(int Width, int Height, byte[] Data);
