using System.Numerics;
using System.Runtime.ExceptionServices;
using Nori.Desktop.Live2D.Gl;
using Nori.Live2D;

namespace Nori.Desktop.Live2D;

/// <summary>原生模型的 GL 资源与提交器；不继承旧 SDK 模型或渲染器。</summary>
public sealed class NativeGlRenderer : IModelMeshTarget, IDisposable
{
	private readonly OpenGLApi _gl;
	private readonly MeshProgram _program;
	private readonly NativeTextureOwner _textures;
	private readonly MaskAtlas? _mask;
	private NativeGlSurface[] _targets = [];
	private int _vao, _vertices, _uvs, _indices;
	private readonly Vector2[] _positions;
	private readonly DrawableMesh[] _drawables;
	private readonly int[] _order;
	// 属性指针属于本实例独占的 VAO，不随宿主状态恢复失效。
	private int _boundSegment = -1;
	private long? _uploadedUpdate;
	private bool _passBound;
	private bool? _culling;
	private int? _texture0, _texture1, _blend, _activeTexture;
	private float _appliedAnisotropy = float.NaN;
	private bool _disposed;

	private readonly record struct DrawableMesh(int VertexBase, int VertexCount, int IndexOffset, int IndexCount,
		int SegmentBase, int Texture, bool Culling, int Blend, bool InvertedMask);

	public NativeModel NativeModel { get; }
	public Matrix4x4 Projection { get; private set; } = Matrix4x4.Identity;
	public Vector4 ModelColor { get; private set; } = Vector4.One;
	public bool IsPremultipliedAlpha => false;
	public bool UseHighPrecisionMask { get; set; }
	public float Anisotropy { get; set; }
	public MaskGroup? ClippingContextBufferForMask { get; set; }
	public MaskGroup? ClippingContextBufferForDraw { get; set; }

	public NativeGlRenderer(OpenGLApi gl, NativeModel model, IReadOnlyList<PreparedTexture> textures)
		: this(gl, model, textures, 65536) { }

	// 测试可缩小段容量，生产路径仍覆盖完整的 ushort 索引范围。
	internal unsafe NativeGlRenderer(OpenGLApi gl, NativeModel model, IReadOnlyList<PreparedTexture> textures, int segmentVertexLimit)
	{
		if (segmentVertexLimit is < 1 or > 65536)
			throw new ArgumentOutOfRangeException(nameof(segmentVertexLimit), "网格段容量必须在 1 到 65536 之间");
		_gl = gl;
		NativeModel = model;
		_order = new int[model.DrawableCount];
		_drawables = new DrawableMesh[_order.Length];
		_program = new(gl);
		_textures = new(gl);
		using var state = new GlStateScope(gl);
		try
		{
			if (!gl.SupportsVertexArrayObjects) throw new InvalidOperationException("当前 GL 上下文不支持模型顶点数组对象");
			_vao = gl.GenVertexArray();
			_vertices = gl.GenBuffer();
			_uvs = gl.GenBuffer();
			_indices = gl.GenBuffer();
			if (_vao == 0 || _vertices == 0 || _uvs == 0 || _indices == 0) throw new InvalidOperationException("模型网格缓冲创建失败");
			foreach (PreparedTexture texture in textures) _textures.Upload(texture);
			int vertexTotal = 0, indexTotal = 0, segmentBase = 0;
			for (int i = 0; i < _drawables.Length; i++)
			{
				int count = model.GetDrawableVertexCount(i), indices = model.GetDrawableVertexIndexCount(i);
				// 超出段容量的单个网格独占一段：其索引本身是 ushort，相对段基址保持原值，旧渲染器能画的模型不应被拒绝。
				segmentBase = GetSegmentBase(vertexTotal, count, segmentBase, segmentVertexLimit);
				_drawables[i] = new(vertexTotal, count, checked(indexTotal * sizeof(ushort)), indices, segmentBase,
					_textures[model.GetDrawableTextureIndex(i)], model.GetDrawableCulling(i), model.GetDrawableBlendMode(i), model.GetDrawableInvertedMask(i));
				vertexTotal = checked(vertexTotal + count);
				indexTotal = checked(indexTotal + indices);
			}
			_positions = new Vector2[vertexTotal];
			var uvs = new Vector2[vertexTotal];
			var elements = new ushort[indexTotal];
			for (int i = 0; i < _drawables.Length; i++)
			{
				ref readonly DrawableMesh mesh = ref _drawables[i];
				new ReadOnlySpan<Vector2>(model.GetDrawableVertexUvs(i), mesh.VertexCount).CopyTo(uvs.AsSpan(mesh.VertexBase));
				ushort* source = model.GetDrawableVertexIndices(i);
				for (int j = 0; j < mesh.IndexCount; j++)
				{
					if (source[j] >= mesh.VertexCount) throw new InvalidDataException("模型网格索引超出该网格的顶点范围");
					elements[mesh.IndexOffset / sizeof(ushort) + j] = checked((ushort)(source[j] + mesh.VertexBase - mesh.SegmentBase));
				}
			}
			_gl.BindVertexArray(_vao);
			_gl.BindBuffer(_gl.GL_ARRAY_BUFFER, _uvs);
			fixed (Vector2* data = uvs)
				_gl.BufferData(_gl.GL_ARRAY_BUFFER, checked(vertexTotal * sizeof(Vector2)), (nint)data, _gl.GL_STATIC_DRAW);
			_gl.BindBuffer(_gl.GL_ELEMENT_ARRAY_BUFFER, _indices);
			fixed (ushort* data = elements)
				_gl.BufferData(_gl.GL_ELEMENT_ARRAY_BUFFER, checked(indexTotal * sizeof(ushort)), (nint)data, _gl.GL_STATIC_DRAW);
			// 首帧之前即验证程序，候选失败不能先替换当前模型。
			_program.EnsureProgram();
			_program.InitializeVertices(_vertices, _uvs);
			_boundSegment = 0;
			if (model.IsUsingMasking())
			{
				_mask = new(gl, model, 1);
				SetClippingMaskBufferSize(256, 256);
			}
		}
		catch
		{
			try { Dispose(); } catch { /* 保留初始化异常，资源释放仍逐项执行。 */ }
			throw;
		}
		finally { GC.KeepAlive(model); }
	}

	internal static int GetSegmentBase(int vertexBase, int vertexCount, int segmentBase, int limit)
	{
		// 段跨度不超过 65535，重定位不会新引入固定重启索引；超出容量的单网格独占一段，保留原索引语义。
		return vertexBase - segmentBase + vertexCount > Math.Min(limit, ushort.MaxValue) ? vertexBase : segmentBase;
	}

	public void SetModelColor(float r, float g, float b, float a) => ModelColor = Vector4.Clamp(new(r, g, b, a), Vector4.Zero, Vector4.One);
	public int GetBindedTextureId(int index) => _textures[index];
	public int MaskTexture(int index) => _targets[index].ColorBuffer;
	public void BeginMask(int index, int framebuffer)
	{
		InvalidateState();
		_targets[index].BeginDraw();
	}
	public void EndMask(int index)
	{
		InvalidateState();
		_targets[index].EndDraw();
	}

	public void SetClippingMaskBufferSize(int width, int height)
	{
		if (_mask is null) return;
		var target = new NativeGlSurface(_gl);
		try
		{
			if (!target.CreateOffscreenSurface(width, height)) throw new InvalidOperationException("模型遮罩目标创建失败");
			_mask.Resize(width, height);
		}
		catch
		{
			try { target.DestroyOffscreenSurface(); } catch { /* 不覆盖创建异常。 */ }
			throw;
		}
		var previous = _targets;
		_targets = [target];
		foreach (var surface in previous) surface.DestroyOffscreenSurface();
	}

	public unsafe void Draw(Matrix4x4 projection)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Projection = projection;
		using var state = new GlStateScope(_gl);
		try
		{
			UploadPositions();
			_mask?.Update(UseHighPrecisionMask);
			if (_mask is not null && !UseHighPrecisionMask) _mask.Draw(this, state.Framebuffer, state.Viewport);
			PreDraw();
			int* orders = NativeModel.GetDrawableRenderOrders();
			for (int i = 0; i < _order.Length; i++) _order[orders[i]] = i;
			foreach (int drawable in _order)
			{
				if (!NativeModel.GetDrawableDynamicFlagIsVisible(drawable)) continue;
				MaskGroup? group = _mask?.Plan.Drawables[drawable];
				if (group is { Active: false }) continue;
				if (group is not null && UseHighPrecisionMask)
				{
					_mask!.Draw(this, state.Framebuffer, state.Viewport, group);
					PreDraw();
				}
				ClippingContextBufferForDraw = group;
				DrawMesh(drawable);
			}
		}
		finally
		{
			InvalidateState();
			ClippingContextBufferForDraw = ClippingContextBufferForMask = null;
			GC.KeepAlive(NativeModel);
		}
	}

	private unsafe void UploadPositions()
	{
		long update = NativeModel.UpdateCount;
		if (_uploadedUpdate == update) return;
		for (int i = 0; i < _drawables.Length; i++)
		{
			ref readonly DrawableMesh mesh = ref _drawables[i];
			new ReadOnlySpan<Vector2>(NativeModel.GetDrawableVertexPositions(i), mesh.VertexCount).CopyTo(_positions.AsSpan(mesh.VertexBase));
		}
		_gl.BindBuffer(_gl.GL_ARRAY_BUFFER, _vertices);
		fixed (Vector2* data = _positions)
			_gl.BufferData(_gl.GL_ARRAY_BUFFER, checked(_positions.Length * sizeof(Vector2)), (nint)data, _gl.GL_DYNAMIC_DRAW);
		_uploadedUpdate = update;
	}

	private void InvalidateState()
	{
		_passBound = false;
		_culling = null;
		_texture0 = _texture1 = _blend = _activeTexture = null;
	}

	public void PreDraw()
	{
		InvalidateState();
		_gl.BindVertexArray(_vao);
		_gl.Disable(_gl.GL_DEPTH_TEST);
		_gl.Disable(_gl.GL_STENCIL_TEST);
		_gl.Disable(_gl.GL_SCISSOR_TEST);
		_gl.Enable(_gl.GL_BLEND);
		_gl.BlendEquationSeparate(0x8006, 0x8006);
		_gl.ColorMask(true, true, true, true);
		_gl.FrontFace(_gl.GL_CCW);
		_program.Bind();
		_gl.ActiveTexture(_gl.GL_TEXTURE0);
		_activeTexture = _gl.GL_TEXTURE0;
		_passBound = true;
		if (!float.IsFinite(Anisotropy) || Anisotropy <= 0) return;
		float maximum = _gl.MaxTextureAnisotropy;
		if (!float.IsFinite(maximum) || maximum < 1) return;
		float applied = Math.Clamp(Anisotropy, 1, maximum);
		if (applied.Equals(_appliedAnisotropy)) return;
		// 部分纹理设置失败时不能保留旧缓存，下一次必须重新逐项应用。
		_appliedAnisotropy = float.NaN;
		foreach (int texture in _textures.Handles)
		{
			_gl.BindTexture(_gl.GL_TEXTURE_2D, texture);
			_gl.TexParameterf(_gl.GL_TEXTURE_2D, _gl.GL_TEXTURE_MAX_ANISOTROPY_EXT, applied);
			_gl.ThrowIfError("模型纹理各向异性设置");
		}
		_appliedAnisotropy = applied;
	}

	public void DrawMesh(int index)
	{
		try
		{
			// 先读取元数据，未构造的渲染器不能先改变宿主 GL 状态。
			ref readonly DrawableMesh mesh = ref _drawables[index];
			if (mesh.Texture == 0) return;
			if (!_passBound) PreDraw();
			if (_culling != mesh.Culling)
			{
				if (mesh.Culling) _gl.Enable(_gl.GL_CULL_FACE);
				else _gl.Disable(_gl.GL_CULL_FACE);
				_culling = mesh.Culling;
			}
			if (_boundSegment != mesh.SegmentBase)
			{
				_program.BindVertexSegment(_vertices, _uvs, mesh.SegmentBase);
				_boundSegment = mesh.SegmentBase;
			}
			bool generating = ClippingContextBufferForMask is not null;
			MaskGroup? mask = ClippingContextBufferForMask ?? ClippingContextBufferForDraw;
			BindTexture(_gl.GL_TEXTURE0, mesh.Texture, ref _texture0);
			// 生成遮罩时必须解绑采样单元一，避免与当前颜色附件构成反馈回路。
			BindTexture(_gl.GL_TEXTURE1, !generating && mask is not null ? MaskTexture(mask.BufferIndex) : 0, ref _texture1);
			int blend = generating ? -1 : mesh.Blend;
			if (_blend != blend)
			{
				if (generating)
					_gl.BlendFuncSeparate(_gl.GL_ZERO, _gl.GL_ONE_MINUS_SRC_COLOR, _gl.GL_ZERO, _gl.GL_ONE_MINUS_SRC_ALPHA);
				else
					_gl.BlendFuncSeparate(blend == 2 ? _gl.GL_DST_COLOR : _gl.GL_ONE,
						blend == 1 ? _gl.GL_ONE : _gl.GL_ONE_MINUS_SRC_ALPHA,
						blend == 0 ? _gl.GL_ONE : _gl.GL_ZERO, blend == 0 ? _gl.GL_ONE_MINUS_SRC_ALPHA : _gl.GL_ONE);
				_blend = blend;
			}
			_program.Apply(this, NativeModel, index, mesh.InvertedMask);
			_gl.DrawElements(_gl.GL_TRIANGLES, mesh.IndexCount, _gl.GL_UNSIGNED_SHORT, mesh.IndexOffset);
		}
		finally { ClippingContextBufferForDraw = ClippingContextBufferForMask = null; }
	}

	private void BindTexture(int unit, int texture, ref int? bound)
	{
		if (bound == texture) return;
		if (_activeTexture != unit)
		{
			_gl.ActiveTexture(unit);
			_activeTexture = unit;
		}
		_gl.BindTexture(_gl.GL_TEXTURE_2D, texture);
		bound = texture;
	}

	/// <summary>
	/// 上下文已丢失时放弃全部 GL 句柄而不调用删除。
	///
	/// 句柄编号只在原上下文内有效；丢失后重建的上下文会重新从小编号分配，
	/// 用旧编号删除会误删新模型的纹理和缓冲。
	/// </summary>
	public void Abandon()
	{
		if (_disposed) return;
		_disposed = true;
		InvalidateState();
		_targets = [];
		_vao = _vertices = _uvs = _indices = 0;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		InvalidateState();
		Exception? failure = null;
		Release(_program.Dispose);
		Release(_textures.Dispose);
		foreach (var target in _targets) Release(target.DestroyOffscreenSurface);
		_targets = [];
		if (_vertices != 0) Release(() => _gl.DeleteBuffer(_vertices));
		if (_uvs != 0) Release(() => _gl.DeleteBuffer(_uvs));
		if (_indices != 0) Release(() => _gl.DeleteBuffer(_indices));
		if (_vao != 0) Release(() => _gl.DeleteVertexArray(_vao));
		_vao = _vertices = _uvs = _indices = 0;
		if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

		void Release(Action action)
		{
			try { action(); }
			catch (Exception exception) { failure ??= exception; }
		}
	}
}
