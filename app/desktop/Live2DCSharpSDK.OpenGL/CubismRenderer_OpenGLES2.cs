// [Nori Modification] Per-frame vertex/index buffers use GL_DYNAMIC_DRAW.
using System.Runtime.InteropServices;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Model;
using Live2DCSharpSDK.Framework.Rendering;

namespace Live2DCSharpSDK.OpenGL;

public class CubismRenderer_OpenGLES2 : CubismRenderer
{
    private readonly OpenGLApi GL;

    /// <summary>
    /// モデルが参照するテクスチャとレンダラでバインドしているテクスチャとのマップ
    /// </summary>
    private readonly Dictionary<int, int> _textures;
    /// <summary>
    /// 描画オブジェクトのインデックスを描画順に並べたリスト
    /// </summary>
    private readonly int[] _sortedDrawableIndexList;
    /// <summary>
    /// OpenGLのステートを保持するオブジェクト
    /// </summary>
    private readonly CubismRendererProfile_OpenGLES2 _rendererProfile;
    /// <summary>
    /// クリッピングマスク管理オブジェクト
    /// </summary>
    private CubismClippingManager_OpenGLES2? _clippingManager;
    /// <summary>
    /// マスクテクスチャに描画するためのクリッピングコンテキスト
    /// </summary>
    internal CubismClippingContext? ClippingContextBufferForMask { get; set; }
    /// <summary>
    /// 画面上描画するためのクリッピングコンテキスト
    /// </summary>
    internal CubismClippingContext? ClippingContextBufferForDraw { get; set; }

    internal CubismShader_OpenGLES2 Shader;

    /// <summary>
    /// マスク描画用のフレームバッファ
    /// </summary>
    private readonly List<CubismOffscreenSurface_OpenGLES2> _offscreenFrameBuffers = [];

    internal int VertexArray { get; private set; }
    internal int VertexBuffer { get; private set; }
    internal int IndexBuffer { get; private set; }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct VBO
    {
        public float ver0;
        public float ver1;
        public float uv0;
        public float uv1;
    }

    internal VBO[] vbo = new VBO[512];

    private readonly LAppDelegate _lapp;

    public unsafe CubismRenderer_OpenGLES2(OpenGLApi gl, LAppDelegate lapp, CubismModel model, int maskBufferCount = 1) : base(model)
    {
        GL = gl;
        Shader = new(gl);
        _lapp = lapp;
        _rendererProfile = new(gl);
        _textures = new Dictionary<int, int>(32);

        VertexArray = GL.GenVertexArray();
        VertexBuffer = GL.GenBuffer();
        IndexBuffer = GL.GenBuffer();

        // 1未満は1に補正する
        if (maskBufferCount < 1)
        {
            maskBufferCount = 1;
            CubismLog.Warning("[Live2D OpenGL]The number of render textures must be an integer greater than or equal to 1. Set the number of render textures to 1.");
        }

        if (model.IsUsingMasking())
        {
            _clippingManager = new CubismClippingManager_OpenGLES2(GL);  //クリッピングマスク・バッファ前処理方式を初期化
            _clippingManager.Initialize(model, maskBufferCount);

            _offscreenFrameBuffers.Clear();
            for (int i = 0; i < maskBufferCount; ++i)
            {
                var offscreenSurface = new CubismOffscreenSurface_OpenGLES2(GL);
                offscreenSurface.CreateOffscreenSurface((int)_clippingManager.ClippingMaskBufferSize.X, (int)_clippingManager.ClippingMaskBufferSize.Y);
                _offscreenFrameBuffers.Add(offscreenSurface);
            }
        }

        _sortedDrawableIndexList = new int[model.GetDrawableCount()];
    }

    /// <summary>
    /// OpenGLテクスチャのバインド処理
    /// CubismRendererにテクスチャを設定し、CubismRenderer中でその画像を参照するためのIndex値を戻り値とする
    /// </summary>
    /// <param name="modelTextureNo">セットするモデルテクスチャの番号</param>
    /// <param name="glTextureNo">OpenGLテクスチャの番号</param>
    public void BindTexture(int modelTextureNo, int glTextureNo)
    {
        _textures[modelTextureNo] = glTextureNo;
    }

    /// <summary>
    /// クリッピングマスクバッファのサイズを設定する
    /// マスク用のFrameBufferを破棄・再作成するため処理コストは高い。
    /// </summary>
    /// <param name="width">クリッピングマスクバッファのサイズ</param>
    /// <param name="height">クリッピングマスクバッファのサイズ</param>
    public unsafe void SetClippingMaskBufferSize(float width, float height)
    {
        if (_clippingManager == null)
        {
            return;
        }

        // インスタンス破棄前にレンダーテクスチャの数を保存
        int renderTextureCount = _clippingManager.RenderTextureCount;

        foreach (var surface in _offscreenFrameBuffers)
        {
            surface.DestroyOffscreenSurface();
        }
        _offscreenFrameBuffers.Clear();

        _clippingManager = new CubismClippingManager_OpenGLES2(GL);

        _clippingManager.SetClippingMaskBufferSize(width, height);
        _clippingManager.Initialize(Model, renderTextureCount);

        for (int i = 0; i < renderTextureCount; ++i)
        {
            var offscreenSurface = new CubismOffscreenSurface_OpenGLES2(GL);
            offscreenSurface.CreateOffscreenSurface((int)_clippingManager.ClippingMaskBufferSize.X, (int)_clippingManager.ClippingMaskBufferSize.Y);
            // 即使平台 FBO 不可用也保留槽位, 避免裁剪管理器的索引与数组错位。
            _offscreenFrameBuffers.Add(offscreenSurface);
        }
    }

    /// <summary>
    /// クリッピングマスクのバッファを取得する
    /// </summary>
    /// <returns>クリッピングマスクのバッファへのポインタ</returns>
    public CubismOffscreenSurface_OpenGLES2 GetMaskBuffer(int index)
    {
        return _offscreenFrameBuffers[index];
    }

    public bool IsGeneratingMask()
    {
        return ClippingContextBufferForMask != null;
    }

    /// <summary>
    /// [オーバーライド]
    /// 描画オブジェクト（アートメッシュ）を描画する。
    /// ポリゴンメッシュとテクスチャ番号をセットで渡す。
    /// </summary>
    /// <param name="textureNo">描画するテクスチャ番号</param>
    /// <param name="indexCount">描画オブジェクトのインデックス値</param>
    /// <param name="vertexCount">ポリゴンメッシュの頂点数</param>
    /// <param name="indexArray">ポリゴンメッシュのインデックス配列</param>
    /// <param name="vertexArray">ポリゴンメッシュの頂点配列</param>
    /// <param name="uvArray">uv配列</param>
    /// <param name="multiplyColor">乗算色</param>
    /// <param name="screenColor">スクリーン色</param>
    /// <param name="opacity">不透明度</param>
    /// <param name="colorBlendMode">カラー合成タイプ</param>
    /// <param name="invertedMask">マスク使用時のマスクの反転使用</param>
    internal unsafe void DrawMeshOpenGL(CubismModel model, int index)
    {
        if (_textures[model.GetDrawableTextureIndex(index)] == 0) return;    // モデルが参照するテクスチャがバインドされていない場合は描画をスキップする

        // 裏面描画の有効・無効
        if (IsCulling)
        {
            GL.Enable(GL.GL_CULL_FACE);
        }
        else
        {
            GL.Disable(GL.GL_CULL_FACE);
        }

        GL.FrontFace(GL.GL_CCW);    // Cubism SDK OpenGLはマスク・アートメッシュ共にCCWが表面

        var vertexCount = model.GetDrawableVertexCount(index);
        var vertexArray = model.GetDrawableVertices(index);
        var uvArray = (float*)model.GetDrawableVertexUvs(index);

        GL.BindVertexArray(VertexArray);

        if (vbo == null || vbo.Length < vertexCount)
        {
            vbo = new VBO[vertexCount];
        }

        for (int a = 0; a < vertexCount; a++)
        {
            vbo[a].ver0 = vertexArray[a * 2];
            vbo[a].ver1 = vertexArray[a * 2 + 1];
            vbo[a].uv0 = uvArray[a * 2];
            vbo[a].uv1 = uvArray[a * 2 + 1];
        }

        int indexCount = model.GetDrawableVertexIndexCount(index);
        ushort* indexArray = model.GetDrawableVertexIndices(index);

        // [Nori Modification] 每帧对同一个缓冲重新指定数据必须用 GL_DYNAMIC_DRAW。
        // 原本是 GL_STATIC_DRAW: 那是"上传一次、反复读取"的用途提示, 驱动会把数据放进
        // 不便写入的显存, 于是这里每帧上百次的重新上传都会和紧邻的上一次 DrawElements
        // 形成写后读冲突, 只能串行等待 —— 表现为视口很小却要几十毫秒一帧、GPU 占用居高不下。
        GL.BindBuffer(GL.GL_ARRAY_BUFFER, VertexBuffer);
        fixed (void* p = vbo)
        {
            GL.BufferData(GL.GL_ARRAY_BUFFER, vertexCount * sizeof(VBO), new IntPtr(p), GL.GL_DYNAMIC_DRAW);
        }

        GL.BindBuffer(GL.GL_ELEMENT_ARRAY_BUFFER, IndexBuffer);
        GL.BufferData(GL.GL_ELEMENT_ARRAY_BUFFER, indexCount * sizeof(ushort), new IntPtr(indexArray), GL.GL_DYNAMIC_DRAW);

        if (IsGeneratingMask())  // マスク生成時
        {
            Shader.SetupShaderProgramForMask(this, model, index);
        }
        else
        {
            Shader.SetupShaderProgramForDraw(this, model, index);
        }

        // ポリゴンメッシュを描画する
        GL.DrawElements(GL.GL_TRIANGLES, indexCount, GL.GL_UNSIGNED_SHORT, 0);

        // 後処理
        GL.UseProgram(0);
        GL.BindBuffer(GL.GL_ARRAY_BUFFER, 0);
        GL.BindBuffer(GL.GL_ELEMENT_ARRAY_BUFFER, 0);
        GL.BindVertexArray(0);
        ClippingContextBufferForDraw = null;
        ClippingContextBufferForMask = null;
    }

    /// <summary>
    /// 描画開始時の追加処理。
    /// モデルを描画する前にクリッピングマスクに必要な処理を実装している。
    /// </summary>
    internal void PreDraw()
    {
        GL.Disable(GL.GL_SCISSOR_TEST);
        GL.Disable(GL.GL_STENCIL_TEST);
        GL.Disable(GL.GL_DEPTH_TEST);

        GL.Enable(GL.GL_BLEND);
        GL.ColorMask(true, true, true, true);

        GL.BindBuffer(GL.GL_ELEMENT_ARRAY_BUFFER, 0);
        GL.BindBuffer(GL.GL_ARRAY_BUFFER, 0); //前にバッファがバインドされていたら破棄する必要がある

        //異方性フィルタリング。プラットフォームのOpenGLによっては未対応の場合があるので、未設定のときは設定しない
        if (Anisotropy > 0.0f)
        {
            for (int i = 0; i < _textures.Count; i++)
            {
                GL.BindTexture(GL.GL_TEXTURE_2D, _textures[i]);
                GL.TexParameterf(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MAX_ANISOTROPY_EXT, Anisotropy);
            }
        }
    }

    /// <summary>
    /// モデル描画直前のOpenGLES2のステートを保持する
    /// </summary>
    protected override void SaveProfile()
    {
        _rendererProfile.Save();
    }

    /// <summary>
    /// モデル描画直前のOpenGLES2のステートを保持する
    /// </summary>
    protected override void RestoreProfile()
    {
        _rendererProfile.Restore();
    }

    protected override unsafe void DoDrawModel()
    {
        //------------ クリッピングマスク・バッファ前処理方式の場合 ------------
        if (_clippingManager != null)
        {
            PreDraw();

            // サイズが違う場合はここで作成しなおし
            for (int i = 0; i < _clippingManager.RenderTextureCount; ++i)
            {
                if (_offscreenFrameBuffers[i].BufferWidth != (uint)_clippingManager.ClippingMaskBufferSize.X ||
                    _offscreenFrameBuffers[i].BufferHeight != (uint)_clippingManager.ClippingMaskBufferSize.Y)
                {
                    _offscreenFrameBuffers[i].CreateOffscreenSurface(
                        (int)_clippingManager.ClippingMaskBufferSize.X, (int)_clippingManager.ClippingMaskBufferSize.Y);
                }
            }

            if (UseHighPrecisionMask)
            {
                _clippingManager.SetupMatrixForHighPrecision(Model, false);
            }
            else
            {
                _clippingManager.SetupClippingContext(Model, this, _rendererProfile.LastFBO, _rendererProfile.LastViewport);
            }
        }

        // 上記クリッピング処理内でも一度PreDrawを呼ぶので注意!!
        PreDraw();

        var drawableCount = Model.GetDrawableCount();
        var renderOrder = Model.GetDrawableRenderOrders();

        // インデックスを描画順でソート
        for (int i = 0; i < drawableCount; ++i)
        {
            var order = renderOrder[i];
            _sortedDrawableIndexList[order] = i;
        }

        if (GL.AlwaysClear)
        {
            var color = _lapp.BGColor;
            GL.ClearColor(color.R, color.G, color.B, color.A);
            GL.Clear(GL.GL_COLOR_BUFFER_BIT | GL.GL_DEPTH_BUFFER_BIT);
        }

        // 描画
        for (int i = 0; i < drawableCount; ++i)
        {
            var drawableIndex = _sortedDrawableIndexList[i];

            // Drawableが表示状態でなければ処理をパスする
            if (!Model.GetDrawableDynamicFlagIsVisible(drawableIndex))
            {
                continue;
            }

            // クリッピングマスク
            var clipContext = _clippingManager?.ClippingContextListForDraw[drawableIndex];

            if (clipContext != null && UseHighPrecisionMask) // マスクを書く必要がある
            {
                if (clipContext.IsUsing) // 書くことになっていた
                {
                    // 生成したFrameBufferと同じサイズでビューポートを設定
                    GL.Viewport(0, 0, (int)_clippingManager!.ClippingMaskBufferSize.X, (int)_clippingManager.ClippingMaskBufferSize.Y);

                    PreDraw(); // バッファをクリアする

                    // ---------- マスク描画処理 ----------
                    // マスク用RenderTextureをactiveにセット
                    GetMaskBuffer(clipContext.BufferIndex).BeginDraw(_rendererProfile.LastFBO);

                    // マスクをクリアする
                    // 1が無効（描かれない）領域、0が有効（描かれる）領域。（シェーダで Cd*Csで0に近い値をかけてマスクを作る。1をかけると何も起こらない）
                    GL.ClearColor(1.0f, 1.0f, 1.0f, 1.0f);
                    GL.Clear(GL.GL_COLOR_BUFFER_BIT);
                }

                {
                    var clipDrawCount = clipContext.ClippingIdCount;
                    for (int index = 0; index < clipDrawCount; index++)
                    {
                        var clipDrawIndex = clipContext.ClippingIdList[index];

                        // 頂点情報が更新されておらず、信頼性がない場合は描画をパスする
                        if (!Model.GetDrawableDynamicFlagVertexPositionsDidChange(clipDrawIndex))
                        {
                            continue;
                        }

                        IsCulling = Model.GetDrawableCulling(clipDrawIndex);

                        // 今回専用の変換を適用して描く
                        // チャンネルも切り替える必要がある(A,R,G,B)
                        ClippingContextBufferForMask = clipContext;

                        DrawMeshOpenGL(Model, clipDrawIndex);
                    }
                }

                {
                    // --- 後処理 ---
                    GetMaskBuffer(clipContext.BufferIndex).EndDraw();
                    ClippingContextBufferForMask = null;
                    GL.Viewport(_rendererProfile.LastViewport[0], _rendererProfile.LastViewport[1], _rendererProfile.LastViewport[2], _rendererProfile.LastViewport[3]);

                    PreDraw(); // バッファをクリアする
                }
            }

            // クリッピングマスクをセットする
            ClippingContextBufferForDraw = clipContext;

            IsCulling = Model.GetDrawableCulling(drawableIndex);

            DrawMeshOpenGL(Model, drawableIndex);
        }
    }

    public override void Dispose()
    {
        Shader.ReleaseShaderProgram();
        foreach (var surface in _offscreenFrameBuffers)
        {
            surface.DestroyOffscreenSurface();
        }
        _offscreenFrameBuffers.Clear();

        if (VertexBuffer != 0)
        {
            GL.DeleteBuffer(VertexBuffer);
            VertexBuffer = 0;
        }
        if (IndexBuffer != 0)
        {
            GL.DeleteBuffer(IndexBuffer);
            IndexBuffer = 0;
        }
        if (VertexArray != 0)
        {
            GL.DeleteVertexArray(VertexArray);
            VertexArray = 0;
        }
    }

    public int GetBindedTextureId(int textureId)
    {
        return (_textures[textureId] != 0) ? _textures[textureId] : -1;
    }
}
