using Nori.Desktop.Live2D.Gl;

namespace Nori.Desktop.Tests;

/// <summary>无设备的 GL 状态与调用替身，用于故障清理验证，不模拟光栅化。</summary>
internal sealed unsafe class RecordingGlApi : OpenGLApi
{
	private int _next;
	private readonly Dictionary<int, int> _integers = [];
	private readonly Dictionary<int, int> _textures = [];
	private readonly Dictionary<int, int> _elements = [];
	private readonly Dictionary<(int Vao, int Index), bool> _attributes = [];
	private readonly HashSet<int> _enabled = [];
	private readonly int[] _viewport = new int[4];
	private readonly bool[] _colorMask = [true, true, true, true];
	private readonly float[] _clearColor = new float[4];
	public bool Es2Context { get; set; }
	public bool VertexArraysSupported { get; set; } = true;
	public override bool IsES2 => Es2Context;
	public override bool SupportsVertexArrayObjects => VertexArraysSupported;
	public List<int> IntegerQueries { get; } = [];
	public List<string> StateCalls { get; } = [];
	public int VertexAttributeQueries { get; private set; }

	public RecordingGlApi()
	{
		_integers[GL_CURRENT_PROGRAM] = 0;
		_integers[GL_VERTEX_ARRAY_BINDING] = 0;
		_integers[GL_ARRAY_BUFFER_BINDING] = 0;
		_integers[GL_FRAMEBUFFER_BINDING] = 0;
		_integers[0x8CAA] = 0;
		_integers[GL_ACTIVE_TEXTURE] = GL_TEXTURE0;
		_integers[GL_FRONT_FACE] = GL_CCW;
		_integers[GL_BLEND_SRC_RGB] = _integers[GL_BLEND_SRC_ALPHA] = GL_ONE;
		_integers[GL_BLEND_DST_RGB] = _integers[GL_BLEND_DST_ALPHA] = GL_ZERO;
		_integers[GL_BLEND_EQUATION_RGB] = _integers[GL_BLEND_EQUATION_ALPHA] = 0x8006;
		_integers[0x8869] = 4;
	}
	public int FailShader { get; set; } = -1;
	public bool FailLink { get; set; }
	public List<int> CreatedShaders { get; } = [];
	public List<int> DeletedShaders { get; } = [];
	public List<int> CreatedPrograms { get; } = [];
	public List<int> DeletedPrograms { get; } = [];
	public List<string> Sources { get; } = [];
	public bool CreateResources { get; set; }
	public int FailTextureUploadAt { get; set; } = -1;
	private int _uploads;
	public List<int> CreatedTextures { get; } = [];
	public List<int> DeletedTextures { get; } = [];
	public List<int> CreatedBuffers { get; } = [];
	public List<int> DeletedBuffers { get; } = [];
	public List<int> CreatedVertexArrays { get; } = [];
	public List<int> DeletedVertexArrays { get; } = [];
	public List<int> CreatedFramebuffers { get; } = [];
	public List<int> DeletedFramebuffers { get; } = [];
	public int DrawCalls { get; private set; }
	public bool CaptureBufferContents { get; set; }
	public List<BufferUpload> BufferUploads { get; } = [];
	public List<AttributePointer> AttributePointers { get; } = [];
	public List<ElementDraw> ElementDraws { get; } = [];
	public List<(int Program, string Name, int Location)> UniformQueries { get; } = [];
	public List<(int Location, int Value)> IntegerUniforms { get; } = [];
	public List<(int Location, float X, float Y, float Z, float W)> VectorUniforms { get; } = [];
	public int VectorUniformCalls { get; private set; }
	public int MatrixUniformCalls { get; private set; }
	private readonly Dictionary<(int Program, string Name), int> _uniformLocations = [];
	internal readonly record struct BufferUpload(int Target, int Size, int Usage, int Buffer, byte[] Data);
	internal readonly record struct AttributePointer(int Vao, int Attribute, int Buffer, int Stride, nint Offset);
	internal readonly record struct ElementDraw(int Vao, int Program, int Texture, int MaskTexture,
		int SrcRgb, int DstRgb, int SrcAlpha, int DstAlpha, bool Culling, int FrontFace, int Count, nint Offset);
	public override bool AlwaysClear => false;
	public override void Viewport(int x, int y, int w, int h) => new[] { x, y, w, h }.CopyTo(_viewport, 0);
	public override void ClearColor(float r, float g, float b, float a) => new[] { r, g, b, a }.CopyTo(_clearColor, 0);
	public override void Clear(int bit) {  }
	public override void Enable(int bit) { StateCalls.Add($"enable:{bit}"); _enabled.Add(bit); }
	public override void Disable(int bit) { StateCalls.Add($"disable:{bit}"); _enabled.Remove(bit); }
	public override void EnableVertexAttribArray(int index) => _attributes[(_integers[GL_VERTEX_ARRAY_BINDING], index)] = true;
	public override void DisableVertexAttribArray(int index) => _attributes[(_integers[GL_VERTEX_ARRAY_BINDING], index)] = false;
	public override void GetIntegerv(int bit, out int data)
	{
		IntegerQueries.Add(bit);
		if (bit == GL_VERTEX_ARRAY_BINDING && !SupportsVertexArrayObjects)
			throw new InvalidOperationException("当前上下文不支持 VAO 查询");
		data = bit == GL_TEXTURE_BINDING_2D ? _textures.GetValueOrDefault(_integers[GL_ACTIVE_TEXTURE])
			: bit == GL_ELEMENT_ARRAY_BUFFER_BINDING ? _elements.GetValueOrDefault(_integers[GL_VERTEX_ARRAY_BINDING])
			: _integers[bit];
	}
	public override void GetIntegerv(int bit, int[] data)
	{
		if (bit != GL_VIEWPORT) throw new InvalidOperationException("未模拟的 GL 整数数组查询");
		_viewport.CopyTo(data, 0);
	}
	public override void GetFloatv(int bit, float[] data)
	{
		if (bit != GL_COLOR_CLEAR_VALUE) throw new InvalidOperationException("未模拟的 GL 浮点查询");
		_clearColor.CopyTo(data, 0);
	}
	public override void ActiveTexture(int bit) { StateCalls.Add($"active:{bit}"); _integers[GL_ACTIVE_TEXTURE] = bit; }
	public override void GetVertexAttribiv(int index, int bit, out int data)
	{
		if (bit != GL_VERTEX_ATTRIB_ARRAY_ENABLED) throw new InvalidOperationException("未模拟的顶点属性查询");
		VertexAttributeQueries++;
		data = _attributes.GetValueOrDefault((_integers[GL_VERTEX_ARRAY_BINDING], index)) ? 1 : 0;
	}
	public override bool IsEnabled(int bit) => _enabled.Contains(bit);
	public override void GetBooleanv(int bit, bool[] data)
	{
		if (bit != GL_COLOR_WRITEMASK) throw new InvalidOperationException("未模拟的 GL 布尔查询");
		_colorMask.CopyTo(data, 0);
	}
	public override void UseProgram(int index) { StateCalls.Add($"program:{index}"); _integers[GL_CURRENT_PROGRAM] = index; }
	public override void FrontFace(int data) { StateCalls.Add($"front:{data}"); _integers[GL_FRONT_FACE] = data; }
	public override void ColorMask(bool a, bool b, bool c, bool d) => new[] { a, b, c, d }.CopyTo(_colorMask, 0);
	public override void BindBuffer(int bit, int index)
	{
		StateCalls.Add($"buffer:{bit}:{index}");
		if (bit == GL_ELEMENT_ARRAY_BUFFER) _elements[_integers[GL_VERTEX_ARRAY_BINDING]] = index;
		else if (bit == GL_ARRAY_BUFFER) _integers[GL_ARRAY_BUFFER_BINDING] = index;
		else throw new InvalidOperationException("未模拟的缓冲绑定");
	}
	public override void BindTexture(int bit, int index)
	{
		if (bit != GL_TEXTURE_2D) throw new InvalidOperationException("未模拟的纹理绑定");
		StateCalls.Add($"texture:{_integers[GL_ACTIVE_TEXTURE]}:{index}");
		_textures[_integers[GL_ACTIVE_TEXTURE]] = index;
	}
	public override void BlendFuncSeparate(int a, int b, int c, int d)
	{
		StateCalls.Add($"blend:{a}:{b}:{c}:{d}");
		_integers[GL_BLEND_SRC_RGB] = a;
		_integers[GL_BLEND_DST_RGB] = b;
		_integers[GL_BLEND_SRC_ALPHA] = c;
		_integers[GL_BLEND_DST_ALPHA] = d;
	}
	public override void BlendEquationSeparate(int rgb, int alpha)
	{
		_integers[GL_BLEND_EQUATION_RGB] = rgb;
		_integers[GL_BLEND_EQUATION_ALPHA] = alpha;
	}
	public override void DeleteProgram(int index) { DeletedPrograms.Add(index); }
	public override int GetAttribLocation(int index, string attr) => attr == "position" ? 0 : 1;
	public override int GetUniformLocation(int index, string uni)
	{
		if (!_uniformLocations.TryGetValue((index, uni), out int location))
			_uniformLocations.Add((index, uni), location = ++_next);
		UniformQueries.Add((index, uni, location));
		return location;
	}
	public override void Uniform1i(int index, int data) => IntegerUniforms.Add((index, data));
	public override void VertexAttribPointer(int index, int length, int type, bool b, int size, nint arr) =>
		AttributePointers.Add(new(_integers[GL_VERTEX_ARRAY_BINDING], index, _integers[GL_ARRAY_BUFFER_BINDING], size, arr));
	public override void Uniform4f(int index, float a, float b, float c, float d)
	{
		VectorUniformCalls++;
		VectorUniforms.Add((index, a, b, c, d));
	}
	public override void UniformMatrix4fv(int index, int length, bool b, ReadOnlySpan<float> data) { MatrixUniformCalls++; }
	public override int CreateProgram() { CreatedPrograms.Add(++_next); return _next; }
	public override void AttachShader(int a, int b) {  }
	public override void DeleteShader(int index) { DeletedShaders.Add(index); }
	public override void DetachShader(int index, int data) {  }
	public override int CreateShader(int type) { CreatedShaders.Add(++_next); return _next; }
	public override void ShaderSource(int a, string source) { Sources.Add(source); }
	public override void CompileShader(int index) {  }
	public override void GetShaderiv(int index, int type, int* length) { *length = CreatedShaders.IndexOf(index) == FailShader ? 0 : 1; }
	public override void GetShaderInfoLog(int index, out string log) { log = "注入的 GPU 故障"; }
	public override void LinkProgram(int index) {  }
	public override void GetProgramiv(int index, int type, int* length) { *length = FailLink ? 0 : 1; }
	public override void GetProgramInfoLog(int index, out string log) { log = "注入的 GPU 故障"; }
	public override void DrawElements(int type, int count, int type1, nint arry)
	{
		DrawCalls++;
		ElementDraws.Add(new(_integers[GL_VERTEX_ARRAY_BINDING], _integers[GL_CURRENT_PROGRAM],
			_textures.GetValueOrDefault(GL_TEXTURE0), _textures.GetValueOrDefault(GL_TEXTURE1),
			_integers[GL_BLEND_SRC_RGB], _integers[GL_BLEND_DST_RGB], _integers[GL_BLEND_SRC_ALPHA], _integers[GL_BLEND_DST_ALPHA],
			_enabled.Contains(GL_CULL_FACE), _integers[GL_FRONT_FACE], count, arry));
	}
	public override void TexParameterf(int type, int type1, float value) {  }
	public override void BindFramebuffer(int type, int data)
	{
		if (type == GL_FRAMEBUFFER || type == 0x8CA9) _integers[GL_FRAMEBUFFER_BINDING] = data;
		if (type == GL_FRAMEBUFFER || type == 0x8CA8) _integers[0x8CAA] = data;
	}
	public override int GenTexture() { if (!CreateResources) return 0; CreatedTextures.Add(++_next); return _next; }
	public override void TexImage2D(int type, int a, int type1, int w, int h, int size, int type2, int type3, IntPtr data) { if (_uploads++ == FailTextureUploadAt) throw new InvalidOperationException("纹理上传失败"); }
	public override void TexParameteri(int a, int b, int c) {  }
	public override int GenFramebuffer() { if (!CreateResources) return 0; CreatedFramebuffers.Add(++_next); return _next; }
	public override void FramebufferTexture2D(int a, int b, int c, int buff, int data) {  }
	public override void DeleteTexture(int data) { DeletedTextures.Add(data); }
	public override void DeleteFramebuffer(int fb) { DeletedFramebuffers.Add(fb); }
	public override void BlendFunc(int a, int b) => BlendFuncSeparate(a, b, a, b);
	public override void GenerateMipmap(int a) {  }
	public override int GenBuffer() { if (!CreateResources) return 0; CreatedBuffers.Add(++_next); return _next; }
	public override void DeleteBuffer(int buffer) { DeletedBuffers.Add(buffer); }
	public override void BufferData(int type, int v1, nint v2, int type1) =>
		BufferUploads.Add(new(type, v1, type1, type == GL_ARRAY_BUFFER ? _integers[GL_ARRAY_BUFFER_BINDING]
			: _elements.GetValueOrDefault(_integers[GL_VERTEX_ARRAY_BINDING]),
			CaptureBufferContents ? new ReadOnlySpan<byte>((void*)v2, v1).ToArray() : []));
	public override int GenVertexArray() { if (!CreateResources) return 0; CreatedVertexArrays.Add(++_next); return _next; }
	public override void DeleteVertexArray(int vertexArray) { DeletedVertexArrays.Add(vertexArray); }
	public override void BindVertexArray(int vertexArray)
	{
		if (!SupportsVertexArrayObjects) throw new InvalidOperationException("当前上下文不支持 VAO 绑定");
		StateCalls.Add($"vao:{vertexArray}");
		_integers[GL_VERTEX_ARRAY_BINDING] = vertexArray;
	}
	public override int CheckFramebufferStatus(int target) { return CreateResources ? GL_FRAMEBUFFER_COMPLETE : 0; }
}
