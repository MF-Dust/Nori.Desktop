using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework.Model;
using Live2DCSharpSDK.Framework.Rendering;

namespace Live2DCSharpSDK.OpenGL;

public class LAppDelegateOpenGL : LAppDelegate
{
    public OpenGLApi GL { get; }

    private readonly Func<string, TexturePixels> _decodeTexture;

    public LAppDelegateOpenGL(OpenGLApi gl, Func<string, TexturePixels> decodeTexture)
    {
        GL = gl;
        _decodeTexture = decodeTexture;

        //テクスチャサンプリング設定
        GL.TexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MAG_FILTER, GL.GL_LINEAR);
        GL.TexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MIN_FILTER, GL.GL_LINEAR);

        //透過設定
        GL.Enable(GL.GL_BLEND);
        GL.BlendFunc(GL.GL_SRC_ALPHA, GL.GL_ONE_MINUS_SRC_ALPHA);

        InitApp();
    }

    public override TexturePixels DecodeTexture(string fileName)
    {
        return _decodeTexture(fileName);
    }

    public override CubismRenderer CreateRenderer(CubismModel model)
    {
        return new CubismRenderer_OpenGLES2(GL, this, model);
    }

    public override TextureInfo CreateTexture(LAppModel model, int index, int width, int height, nint data)
    {
        int textureId = GL.GenTexture();
        GL.BindTexture(GL.GL_TEXTURE_2D, textureId);
        GL.TexImage2D(GL.GL_TEXTURE_2D, 0, GL.GL_RGBA, width, height, 0, GL.GL_RGBA, GL.GL_UNSIGNED_BYTE, data);
        GL.GenerateMipmap(GL.GL_TEXTURE_2D);
        GL.TexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MIN_FILTER, GL.GL_LINEAR_MIPMAP_LINEAR);
        GL.TexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MAG_FILTER, GL.GL_LINEAR);
        GL.BindTexture(GL.GL_TEXTURE_2D, 0);

        (model.Renderer as CubismRenderer_OpenGLES2)?.BindTexture(index, textureId);

        return new TextureInfoOpenGL(GL)
        {
            Id = textureId
        };
    }
}
