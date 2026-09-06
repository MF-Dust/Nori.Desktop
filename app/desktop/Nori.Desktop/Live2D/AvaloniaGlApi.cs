using System.Runtime.InteropServices;
using System.Text;
using Avalonia.OpenGL;
using Live2DCSharpSDK.OpenGL;

namespace Nori.Desktop.Live2D;

/// <summary>
/// Avalonia OpenGL 适配层
///
/// 实现 Live2DCSharpSDK.OpenGL.OpenGLApi，将 SDK 的 OpenGL 调用转接到 Avalonia GlInterface 及动态加载的 OpenGL 函数。
/// </summary>
public sealed class AvaloniaGlApi(GlInterface gl) : OpenGLApi
{
	public override bool AlwaysClear => true;

	public delegate void Func1(int a, int b);
	public delegate void Func2(int a, int b, int c, int d);
	public delegate void Func4(bool a, bool b, bool c, bool d);
	public delegate void Func5(int a);
	public delegate void Func6(int a, int b, int c, int d, int e);
	public unsafe delegate void Func7(int a, bool* b);
	public unsafe delegate void Func8(int a, int* b);
	public delegate void Func9(int a, int b, out int c);
	public delegate bool Func10(int a);
	public delegate void Func11(int a, int b, float c);
	public delegate void Func12(int a, float b, float c, float d, float e);
	public delegate void GlReadPixelsFunc(int x, int y, int width, int height, int format, int type, nint pixels);
	public delegate int GlCheckFramebufferStatusFunc(int target);

	public Func1 GLBlendFunc = Marshal.GetDelegateForFunctionPointer<Func1>(gl.GetProcAddress("glBlendFunc"));
	public Func2 GLBlendFuncSeparate = Marshal.GetDelegateForFunctionPointer<Func2>(gl.GetProcAddress("glBlendFuncSeparate"));
	public Func4 GLColorMask = Marshal.GetDelegateForFunctionPointer<Func4>(gl.GetProcAddress("glColorMask"));
	public Func1 GLDetachShader = Marshal.GetDelegateForFunctionPointer<Func1>(gl.GetProcAddress("glDetachShader"));
	public Func5 GLDisable = Marshal.GetDelegateForFunctionPointer<Func5>(gl.GetProcAddress("glDisable"));
	public Func5 GLDisableVertexAttribArray = Marshal.GetDelegateForFunctionPointer<Func5>(gl.GetProcAddress("glDisableVertexAttribArray"));
	public Func5 GLFrontFace = Marshal.GetDelegateForFunctionPointer<Func5>(gl.GetProcAddress("glFrontFace"));
	public Func5 GLGenerateMipmap = Marshal.GetDelegateForFunctionPointer<Func5>(gl.GetProcAddress("glGenerateMipmap"));
	public Func7 GLGetBooleanv = Marshal.GetDelegateForFunctionPointer<Func7>(gl.GetProcAddress("glGetBooleanv"));
	public Func8 GLGetIntegerv = Marshal.GetDelegateForFunctionPointer<Func8>(gl.GetProcAddress("glGetIntegerv"));
	public Func9 GLGetVertexAttribiv = Marshal.GetDelegateForFunctionPointer<Func9>(gl.GetProcAddress("glGetVertexAttribiv"));
	public Func10 GLIsEnabled = Marshal.GetDelegateForFunctionPointer<Func10>(gl.GetProcAddress("glIsEnabled"));
	public Func11 GLTexParameterf = Marshal.GetDelegateForFunctionPointer<Func11>(gl.GetProcAddress("glTexParameterf"));
	public Func1 GLUniform1i = Marshal.GetDelegateForFunctionPointer<Func1>(gl.GetProcAddress("glUniform1i"));
	public Func12 GLUniform4f = Marshal.GetDelegateForFunctionPointer<Func12>(gl.GetProcAddress("glUniform4f"));
	public GlReadPixelsFunc GLReadPixels = Marshal.GetDelegateForFunctionPointer<GlReadPixelsFunc>(gl.GetProcAddress("glReadPixels"));
	public GlCheckFramebufferStatusFunc GLCheckFramebufferStatus = Marshal.GetDelegateForFunctionPointer<GlCheckFramebufferStatusFunc>(gl.GetProcAddress("glCheckFramebufferStatus"));

	public override void ActiveTexture(int bit) => gl.ActiveTexture(bit);
	public override void AttachShader(int a, int b) => gl.AttachShader(a, b);
	public override void BindBuffer(int bit, int index) => gl.BindBuffer(bit, index);
	public override void BindFramebuffer(int type, int data) => gl.BindFramebuffer(type, data);
	public override void BindTexture(int bit, int index) => gl.BindTexture(bit, index);
	public override void BlendFunc(int a, int b) => GLBlendFunc(a, b);
	public override void BlendFuncSeparate(int a, int b, int c, int d) => GLBlendFuncSeparate(a, b, c, d);
	public override void Clear(int bit) => gl.Clear(bit);
	public override void ClearColor(float r, float g, float b, float a) => gl.ClearColor(r, g, b, a);
	public override void ColorMask(bool a, bool b, bool c, bool d) => GLColorMask(a, b, c, d);
	public override void CompileShader(int index) => gl.CompileShader(index);
	public override int CreateProgram() => gl.CreateProgram();
	public override int CreateShader(int type) => gl.CreateShader(type);
	public override void DeleteFramebuffer(int fb) => gl.DeleteFramebuffer(fb);
	public override void DeleteProgram(int index) => gl.DeleteProgram(index);
	public override void DeleteShader(int index) => gl.DeleteShader(index);
	public override void DeleteTexture(int data) => gl.DeleteTexture(data);
	public override void DetachShader(int index, int data) => GLDetachShader(index, data);
	public override void Disable(int bit) => GLDisable(bit);
	public override void DisableVertexAttribArray(int index) => GLDisableVertexAttribArray(index);
	public override unsafe void DrawElements(int type, int count, int type1, nint arry) => gl.DrawElements(type, count, type1, arry);
	public override void Enable(int bit) => GLEnable(bit);
	private void GLEnable(int bit) => gl.Enable(bit);
	public override void EnableVertexAttribArray(int index) => gl.EnableVertexAttribArray(index);
	public override void FramebufferTexture2D(int a, int b, int c, int buff, int data) => gl.FramebufferTexture2D(a, b, c, buff, data);
	public override void FrontFace(int data) => GLFrontFace(data);
	public override void GenerateMipmap(int a) => GLGenerateMipmap(a);
	public override int GenFramebuffer() => gl.GenFramebuffer();
	public override int GenTexture() => gl.GenTexture();
	public override int GetAttribLocation(int index, string attr) => gl.GetAttribLocationString(index, attr);

	public override unsafe void GetBooleanv(int bit, bool[] data)
	{
		fixed (bool* ptr = data) GLGetBooleanv(bit, ptr);
	}

	public override void GetIntegerv(int bit, out int data) => gl.GetIntegerv(bit, out data);

	public override unsafe void GetIntegerv(int bit, int[] data)
	{
		fixed (int* ptr = data) GLGetIntegerv(bit, ptr);
	}

	public override unsafe void GetProgramInfoLog(int index, out string log)
	{
		int logLength;
		gl.GetProgramiv(index, GL_INFO_LOG_LENGTH, &logLength);
		byte[] logData = new byte[logLength];
		int len;
		fixed (void* ptr = logData) gl.GetProgramInfoLog(index, logLength, out len, ptr);
		log = Encoding.UTF8.GetString(logData, 0, len);
	}

	public override unsafe void GetProgramiv(int index, int type, int* length) => gl.GetProgramiv(index, type, length);

	public override unsafe void GetShaderInfoLog(int index, out string log)
	{
		int logLength;
		gl.GetShaderiv(index, GL_INFO_LOG_LENGTH, &logLength);
		byte[] logData = new byte[logLength];
		int len;
		fixed (void* ptr = logData) gl.GetShaderInfoLog(index, logLength, out len, ptr);
		log = Encoding.UTF8.GetString(logData, 0, len);
	}

	public override unsafe void GetShaderiv(int index, int type, int* length) => gl.GetShaderiv(index, type, length);
	public override int GetUniformLocation(int index, string uni) => gl.GetUniformLocationString(index, uni);
	public override void GetVertexAttribiv(int index, int bit, out int data) => GLGetVertexAttribiv(index, bit, out data);
	public override bool IsEnabled(int bit) => GLIsEnabled(bit);
	public override void LinkProgram(int index) => gl.LinkProgram(index);
	public override void ShaderSource(int a, string source) => gl.ShaderSourceString(a, source);
	public override void TexImage2D(int type, int a, int type1, int w, int h, int size, int type2, int type3, nint data) => gl.TexImage2D(type, a, type1, w, h, size, type2, type3, data);
	public override void TexParameterf(int type, int type1, float value) => GLTexParameterf(type, type1, value);
	public override void TexParameteri(int a, int b, int c) => gl.TexParameteri(a, b, c);
	public override void Uniform1i(int index, int data) => GLUniform1i(index, data);
	public override void Uniform4f(int index, float a, float b, float c, float d) => GLUniform4f(index, a, b, c, d);

	public override unsafe void UniformMatrix4fv(int index, int length, bool b, float[] data)
	{
		fixed (float* ptr = data) gl.UniformMatrix4fv(index, length, b, ptr);
	}

	public override void UseProgram(int index) => gl.UseProgram(index);

	public override unsafe void VertexAttribPointer(int index, int length, int type, bool b, int size, nint arr)
	{
		gl.VertexAttribPointer(index, length, type, b ? 1 : 0, size, arr);
	}

	public override void Viewport(int x, int y, int w, int h) => gl.Viewport(x, y, w, h);
	public override int GenBuffer() => gl.GenBuffer();
	public override void DeleteBuffer(int buffer) => gl.DeleteBuffer(buffer);
	public override void BufferData(int type, int v1, nint v2, int type1) => gl.BufferData(type, v1, v2, type1);
	public override int GenVertexArray() => gl.GenVertexArray();
	public override void DeleteVertexArray(int vertexArray) => gl.DeleteVertexArray(vertexArray);
	public override void BindVertexArray(int vertexArray) => gl.BindVertexArray(vertexArray);
	public override int CheckFramebufferStatus(int target) => GLCheckFramebufferStatus(target);
}
