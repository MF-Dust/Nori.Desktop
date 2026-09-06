using Live2DCSharpSDK.OpenGL;

namespace Nori.Desktop.Live2D;

/// <summary>
/// GLES2 纹理合成四边形。
///
/// 原生伴侣视窗先渲染到纹理，再由这里合成到 Avalonia 的默认帧缓冲；同一条路径也用于生成低分辨率命中掩码。
/// </summary>
internal sealed class OpenGLTextureQuad : IDisposable
{
	private const string VertexShaderSource = """
		attribute vec2 a_position;
		attribute vec2 a_texCoord;
		varying vec2 v_texCoord;
		uniform vec4 u_offsetScale;
		void main()
		{
			vec2 position = a_position * u_offsetScale.zw + u_offsetScale.xy;
			gl_Position = vec4(position, 0.0, 1.0);
			v_texCoord = a_texCoord;
		}
		""";

	private const string FragmentShaderSource = """
		precision mediump float;
		varying vec2 v_texCoord;
		uniform sampler2D s_texture0;
		uniform vec4 u_tint;
		void main()
		{
			vec4 color = texture2D(s_texture0, v_texCoord);
			gl_FragColor = vec4(color.rgb * u_tint.rgb, color.a * u_tint.a);
		}
		""";

	// 每个输出掩码格内取中心和四个 20%/80% 位置，避免单点采样漏掉细部件。
	// 命中 FBO 固定为 96x128，偏移保持在当前格内，不会把相邻透明格膨胀成可点击区域。
	private const string HitMaskFragmentShaderSource = """
		precision mediump float;
		varying vec2 v_texCoord;
		uniform sampler2D s_texture0;
		void main()
		{
			vec2 sampleOffset = vec2(0.3 / 96.0, 0.3 / 128.0);
			float alpha = texture2D(s_texture0, v_texCoord).a;
			alpha = max(alpha, texture2D(s_texture0, v_texCoord + vec2(-sampleOffset.x, -sampleOffset.y)).a);
			alpha = max(alpha, texture2D(s_texture0, v_texCoord + vec2(sampleOffset.x, -sampleOffset.y)).a);
			alpha = max(alpha, texture2D(s_texture0, v_texCoord + vec2(-sampleOffset.x, sampleOffset.y)).a);
			alpha = max(alpha, texture2D(s_texture0, v_texCoord + vec2(sampleOffset.x, sampleOffset.y)).a);
			gl_FragColor = vec4(1.0, 1.0, 1.0, alpha);
		}
		""";

	private static readonly float[] Vertices =
	[
		-1.0f, -1.0f, 0.0f, 0.0f,
		1.0f, -1.0f, 1.0f, 0.0f,
		1.0f, 1.0f, 1.0f, 1.0f,
		-1.0f, 1.0f, 0.0f, 1.0f,
	];

	private static readonly ushort[] Indices = [0, 1, 2, 0, 2, 3];

	private readonly OpenGLApi _gl;
	private int _program;
	private int _hitMaskProgram;
	private int _vertexBuffer;
	private int _indexBuffer;
	private int _vertexArray;
	private int _positionLocation;
	private int _texCoordLocation;
	private int _textureLocation;
	private int _offsetScaleLocation;
	private int _tintLocation;
	private int _hitMaskPositionLocation;
	private int _hitMaskTexCoordLocation;
	private int _hitMaskTextureLocation;
	private int _hitMaskOffsetScaleLocation;

	public OpenGLTextureQuad(OpenGLApi gl)
	{
		_gl = gl;
		TryInitialize();
	}

	public bool IsAvailable => _program != 0 && _vertexBuffer != 0 && _indexBuffer != 0;

	public bool IsHitMaskAvailable => IsAvailable && _hitMaskProgram != 0;

	/// <summary>把纹理画到当前帧缓冲；纹理颜色默认已经是预乘 alpha。</summary>
	public bool Draw(int texture, float tintR, float tintG, float tintB, float tintA, float offsetX = 0, float offsetY = 0)
	{
		return DrawTexture(
			_program,
			_positionLocation,
			_texCoordLocation,
			_textureLocation,
			_offsetScaleLocation,
			_tintLocation,
			texture,
			tintR,
			tintG,
			tintB,
			tintA,
			offsetX,
			offsetY,
			useBlend: true);
	}

	/// <summary>
	/// 把纹理绘制为低分辨率命中掩码；每个输出格取五个纹理采样点的 alpha 最大值。
	/// </summary>
	public bool DrawHitMask(int texture)
	{
		return DrawTexture(
			_hitMaskProgram,
			_hitMaskPositionLocation,
			_hitMaskTexCoordLocation,
			_hitMaskTextureLocation,
			_hitMaskOffsetScaleLocation,
			-1,
			texture,
			1,
			1,
			1,
			1,
			0,
			0,
			useBlend: false);
	}

	private bool DrawTexture(
		int program,
		int positionLocation,
		int texCoordLocation,
		int textureLocation,
		int offsetScaleLocation,
		int tintLocation,
		int texture,
		float tintR,
		float tintG,
		float tintB,
		float tintA,
		float offsetX,
		float offsetY,
		bool useBlend)
	{
		if (!IsAvailable || program == 0 || texture == 0) return false;

		_gl.GetIntegerv(_gl.GL_ARRAY_BUFFER_BINDING, out int oldArrayBuffer);
		_gl.GetIntegerv(_gl.GL_ELEMENT_ARRAY_BUFFER_BINDING, out int oldElementBuffer);
		_gl.GetIntegerv(_gl.GL_CURRENT_PROGRAM, out int oldProgram);
		_gl.GetIntegerv(_gl.GL_ACTIVE_TEXTURE, out int oldActiveTexture);
		_gl.ActiveTexture(_gl.GL_TEXTURE0);
		_gl.GetIntegerv(_gl.GL_TEXTURE_BINDING_2D, out int oldTexture);
		bool oldBlend = _gl.IsEnabled(_gl.GL_BLEND);
		bool oldCull = _gl.IsEnabled(_gl.GL_CULL_FACE);
		bool oldDepth = _gl.IsEnabled(_gl.GL_DEPTH_TEST);
		_gl.GetIntegerv(_gl.GL_BLEND_SRC_RGB, out int oldBlendSrcRgb);
		_gl.GetIntegerv(_gl.GL_BLEND_DST_RGB, out int oldBlendDstRgb);
		_gl.GetIntegerv(_gl.GL_BLEND_SRC_ALPHA, out int oldBlendSrcAlpha);
		_gl.GetIntegerv(_gl.GL_BLEND_DST_ALPHA, out int oldBlendDstAlpha);

		try
		{
			_gl.Disable(_gl.GL_CULL_FACE);
			_gl.Disable(_gl.GL_DEPTH_TEST);
			if (useBlend)
			{
				_gl.Enable(_gl.GL_BLEND);
				// Cubism 的颜色已经是预乘 alpha；否则合成会再次乘 alpha 造成透明部件变暗。
				_gl.BlendFuncSeparate(_gl.GL_ONE, _gl.GL_ONE_MINUS_SRC_ALPHA, _gl.GL_ONE, _gl.GL_ONE_MINUS_SRC_ALPHA);
			}
			else
			{
				_gl.Disable(_gl.GL_BLEND);
			}
			_gl.UseProgram(program);
			_gl.BindVertexArray(_vertexArray);
			_gl.BindBuffer(_gl.GL_ARRAY_BUFFER, _vertexBuffer);
			_gl.BindBuffer(_gl.GL_ELEMENT_ARRAY_BUFFER, _indexBuffer);
			_gl.EnableVertexAttribArray(positionLocation);
			_gl.EnableVertexAttribArray(texCoordLocation);
			_gl.VertexAttribPointer(positionLocation, 2, _gl.GL_FLOAT, false, 4 * sizeof(float), 0);
			_gl.VertexAttribPointer(texCoordLocation, 2, _gl.GL_FLOAT, false, 4 * sizeof(float), 2 * sizeof(float));
			_gl.ActiveTexture(_gl.GL_TEXTURE0);
			_gl.BindTexture(_gl.GL_TEXTURE_2D, texture);
			_gl.Uniform1i(textureLocation, 0);
			_gl.Uniform4f(offsetScaleLocation, offsetX, offsetY, 1.0f, 1.0f);
			if (tintLocation >= 0) _gl.Uniform4f(tintLocation, tintR, tintG, tintB, tintA);
			_gl.DrawElements(_gl.GL_TRIANGLES, Indices.Length, _gl.GL_UNSIGNED_SHORT, 0);
		}
		finally
		{
			_gl.DisableVertexAttribArray(positionLocation);
			_gl.DisableVertexAttribArray(texCoordLocation);
			_gl.BindVertexArray(0);
			_gl.BindBuffer(_gl.GL_ARRAY_BUFFER, oldArrayBuffer);
			_gl.BindBuffer(_gl.GL_ELEMENT_ARRAY_BUFFER, oldElementBuffer);
			_gl.UseProgram(oldProgram);
			_gl.ActiveTexture(_gl.GL_TEXTURE0);
			_gl.BindTexture(_gl.GL_TEXTURE_2D, oldTexture);
			_gl.ActiveTexture(oldActiveTexture);
			_gl.BlendFuncSeparate(oldBlendSrcRgb, oldBlendDstRgb, oldBlendSrcAlpha, oldBlendDstAlpha);
			SetEnabled(_gl.GL_BLEND, oldBlend);
			SetEnabled(_gl.GL_CULL_FACE, oldCull);
			SetEnabled(_gl.GL_DEPTH_TEST, oldDepth);
		}

		return true;
	}

	/// <summary>绘制模型阴影；只读模型纹理 alpha，不会污染命中掩码。</summary>
	public bool DrawShadow(int texture)
	{
		if (!IsAvailable || texture == 0) return false;

		// 多个微小偏移比依赖扩展模糊更容易在 GLES2 / 三个平台保持一致。
		const float alpha = 0.08f;
		return Draw(texture, 0, 0, 0, alpha, 0.012f, -0.014f)
			&& Draw(texture, 0, 0, 0, alpha, -0.006f, -0.010f)
			&& Draw(texture, 0, 0, 0, alpha, 0.018f, -0.004f);
	}

	public void Dispose()
	{
		if (_hitMaskProgram != 0)
		{
			_gl.DeleteProgram(_hitMaskProgram);
			_hitMaskProgram = 0;
		}
		if (_program != 0)
		{
			_gl.DeleteProgram(_program);
			_program = 0;
		}
		if (_vertexBuffer != 0)
		{
			_gl.DeleteBuffer(_vertexBuffer);
			_vertexBuffer = 0;
		}
		if (_indexBuffer != 0)
		{
			_gl.DeleteBuffer(_indexBuffer);
			_indexBuffer = 0;
		}
		if (_vertexArray != 0)
		{
			_gl.DeleteVertexArray(_vertexArray);
			_vertexArray = 0;
		}
	}

	private void TryInitialize()
	{
		try
		{
			_program = CreateProgram(VertexShaderSource, FragmentShaderSource);
			if (_program == 0) return;

			_positionLocation = _gl.GetAttribLocation(_program, "a_position");
			_texCoordLocation = _gl.GetAttribLocation(_program, "a_texCoord");
			_textureLocation = _gl.GetUniformLocation(_program, "s_texture0");
			_offsetScaleLocation = _gl.GetUniformLocation(_program, "u_offsetScale");
			_tintLocation = _gl.GetUniformLocation(_program, "u_tint");

			// 命中掩码单独使用五点 alpha 覆盖采样；编译失败时仍可保留普通画面，
			// PetGlControl 会退回场景纹理 CPU 采样路径。
			_hitMaskProgram = CreateProgram(VertexShaderSource, HitMaskFragmentShaderSource);
			if (_hitMaskProgram != 0)
			{
				_hitMaskPositionLocation = _gl.GetAttribLocation(_hitMaskProgram, "a_position");
				_hitMaskTexCoordLocation = _gl.GetAttribLocation(_hitMaskProgram, "a_texCoord");
				_hitMaskTextureLocation = _gl.GetUniformLocation(_hitMaskProgram, "s_texture0");
				_hitMaskOffsetScaleLocation = _gl.GetUniformLocation(_hitMaskProgram, "u_offsetScale");
			}

			_vertexArray = _gl.GenVertexArray();
			_vertexBuffer = _gl.GenBuffer();
			_indexBuffer = _gl.GenBuffer();
			if (_vertexArray == 0 || _vertexBuffer == 0 || _indexBuffer == 0)
			{
				Dispose();
				return;
			}

			_gl.BindVertexArray(_vertexArray);
			_gl.BindBuffer(_gl.GL_ARRAY_BUFFER, _vertexBuffer);
			unsafe
			{
				fixed (float* pointer = Vertices)
				{
					_gl.BufferData(_gl.GL_ARRAY_BUFFER, Vertices.Length * sizeof(float), (nint)pointer, _gl.GL_STATIC_DRAW);
				}
				_gl.BindBuffer(_gl.GL_ELEMENT_ARRAY_BUFFER, _indexBuffer);
				fixed (ushort* pointer = Indices)
				{
					_gl.BufferData(_gl.GL_ELEMENT_ARRAY_BUFFER, Indices.Length * sizeof(ushort), (nint)pointer, _gl.GL_STATIC_DRAW);
				}
			}
			_gl.BindVertexArray(0);
			_gl.BindBuffer(_gl.GL_ARRAY_BUFFER, 0);
			_gl.BindBuffer(_gl.GL_ELEMENT_ARRAY_BUFFER, 0);
		}
		catch
		{
			Dispose();
		}
	}

	private int CreateProgram(string vertexSource, string fragmentSource)
	{
		int vertex = CreateShader(_gl.GL_VERTEX_SHADER, vertexSource);
		if (vertex == 0) return 0;
		int fragment = CreateShader(_gl.GL_FRAGMENT_SHADER, fragmentSource);
		if (fragment == 0)
		{
			_gl.DeleteShader(vertex);
			return 0;
		}

		int program = _gl.CreateProgram();
		if (program == 0)
		{
			_gl.DeleteShader(vertex);
			_gl.DeleteShader(fragment);
			return 0;
		}

		_gl.AttachShader(program, vertex);
		_gl.AttachShader(program, fragment);
		_gl.LinkProgram(program);
		unsafe
		{
			int status;
			_gl.GetProgramiv(program, _gl.GL_LINK_STATUS, &status);
			if (status == _gl.GL_FALSE)
			{
				_gl.DeleteProgram(program);
				program = 0;
			}
		}
		if (program != 0)
		{
			_gl.DetachShader(program, vertex);
			_gl.DetachShader(program, fragment);
		}
		_gl.DeleteShader(vertex);
		_gl.DeleteShader(fragment);
		return program;
	}

	private int CreateShader(int type, string source)
	{
		int shader = _gl.CreateShader(type);
		if (shader == 0) return 0;
		_gl.ShaderSource(shader, source);
		_gl.CompileShader(shader);
		unsafe
		{
			int status;
			_gl.GetShaderiv(shader, _gl.GL_COMPILE_STATUS, &status);
			if (status == _gl.GL_FALSE)
			{
				_gl.DeleteShader(shader);
				return 0;
			}
		}
		return shader;
	}

	private void SetEnabled(int capability, bool enabled)
	{
		if (enabled) _gl.Enable(capability);
		else _gl.Disable(capability);
	}
}
