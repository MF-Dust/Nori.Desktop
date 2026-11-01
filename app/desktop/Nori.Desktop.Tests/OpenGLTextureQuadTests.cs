using Nori.Desktop.Live2D;
using Nori.Desktop.Live2D.Gl;
using Xunit.Abstractions;

namespace Nori.Desktop.Tests;

/// <summary>无需模型资源的 GLES 合成回归测试，与真实模型测试共用 EGL 上下文。</summary>
public sealed class TextureQuadGlTheoryAttribute : TheoryAttribute
{
	public TextureQuadGlTheoryAttribute()
	{
		if ((!System.OperatingSystem.IsWindows() && !System.OperatingSystem.IsLinux()) || !Environment.Is64BitProcess
			|| Environment.GetEnvironmentVariable("NORI_TEST_NATIVE_GL") != "1")
			Skip = "Windows x64 或 Linux EGL 下设置 NORI_TEST_NATIVE_GL=1。";
	}
}

// 同一 EGL display 的初始化/释放不能与模型测试交错。
[Collection("Native settings")]
public sealed class OpenGLTextureQuadTests(ITestOutputHelper output)
{
	[TextureQuadGlTheory]
	[InlineData(2, "compose")]
	[InlineData(2, "shadow")]
	[InlineData(2, "hit")]
	[InlineData(3, "compose")]
	[InlineData(3, "shadow")]
	[InlineData(3, "hit")]
	public unsafe void NonDefaultEntryStateDoesNotChangePixelsAndIsRestored(int version, string drawKind)
	{
		using var context = new EglTestContext(version);
		context.MakeCurrent(0);
		output.WriteLine($"GLES {version} 渲染器：{context.Renderer}");
		var gl = context.CreateApi();
		using var source = new NativeGlSurface(gl);
		using var target = new NativeGlSurface(gl);
		using var quad = new OpenGLTextureQuad(gl);
		Assert.True(quad.IsHitMaskAvailable);
		Assert.True(source.CreateOffscreenSurface(8, 8));
		Assert.True(target.CreateOffscreenSurface(8, 8));
		source.BeginDraw();
		source.Clear(0.25f, 0.125f, 0.0625f, 0.5f);
		source.EndDraw();
		target.BeginDraw();
		try
		{
			gl.Viewport(0, 0, 8, 8);
			byte[] expected = DrawAndRead(false);
			Assert.Contains(expected.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
			Assert.Equal(expected, DrawAndRead(true));
			Assert.Equal(0, context.GetError());
		}
		finally { target.EndDraw(); }

		byte[] DrawAndRead(bool dirty)
		{
			gl.Disable(gl.GL_SCISSOR_TEST);
			gl.ColorMask(true, true, true, true);
			target.Clear(0.0625f, 0.125f, 0.25f, 0.25f);
			if (dirty)
			{
				// 默认零尺寸 scissor 会挡住所有像素；非加法混合和部分颜色写入也必须被覆盖。
				gl.Enable(gl.GL_DEPTH_TEST);
				gl.Enable(gl.GL_STENCIL_TEST);
				gl.Enable(gl.GL_SCISSOR_TEST);
				gl.Enable(gl.GL_CULL_FACE);
				gl.Enable(gl.GL_BLEND);
				gl.FrontFace(0x0900); // GL_CW
				gl.ColorMask(false, true, false, false);
				gl.BlendEquationSeparate(0x800A, 0x800B); // GL_FUNC_SUBTRACT / GL_FUNC_REVERSE_SUBTRACT
				gl.BlendFuncSeparate(gl.GL_ZERO, gl.GL_ONE, gl.GL_ZERO, gl.GL_ONE);
				gl.ActiveTexture(gl.GL_TEXTURE1);
				gl.BindTexture(gl.GL_TEXTURE_2D, target.ColorBuffer);
				gl.ActiveTexture(gl.GL_TEXTURE0 + 2);
				gl.BindTexture(gl.GL_TEXTURE_2D, target.ColorBuffer);
			}
			var entry = Capture(gl);
			Assert.True(drawKind switch
			{
				"compose" => quad.Draw(source.ColorBuffer, 1, 1, 1, 1),
				"shadow" => quad.DrawShadow(source.ColorBuffer),
				"hit" => quad.DrawHitMask(source.ColorBuffer),
				_ => throw new ArgumentOutOfRangeException(nameof(drawKind)),
			});
			Assert.Equal(entry, Capture(gl));
			byte[] pixels = new byte[8 * 8 * 4];
			fixed (byte* pointer = pixels)
				gl.GLReadPixels(0, 0, 8, 8, gl.GL_RGBA, gl.GL_UNSIGNED_BYTE, (nint)pointer);
			return pixels;
		}
	}

	[Theory]
	[InlineData(-1, false, true)]
	[InlineData(0, false, true)]
	[InlineData(1, false, true)]
	[InlineData(2, false, true)]
	[InlineData(3, false, true)]
	[InlineData(-1, true, true)]
	[InlineData(-1, false, false)]
	public void InitializationFailuresAndRepeatedDisposePreserveCleanup(int failShader, bool failLink, bool createResources)
	{
		var gl = new RecordingGlApi { CreateResources = createResources, FailShader = failShader, FailLink = failLink };
		gl.Enable(gl.GL_SCISSOR_TEST);
		gl.ColorMask(false, true, false, true);
		gl.BlendEquationSeparate(0x800A, 0x800B);
		gl.ActiveTexture(gl.GL_TEXTURE0 + 2);
		var entry = Capture(gl);
		using var quad = new OpenGLTextureQuad(gl);
		Assert.Equal(entry, Capture(gl));
		Assert.Equal(createResources && !failLink && (failShader == -1 || failShader >= 2), quad.IsAvailable);
		Assert.Equal(createResources && !failLink && failShader == -1, quad.IsHitMaskAvailable);
		quad.Dispose();
		quad.Dispose();
		Assert.False(quad.IsAvailable);
		Assert.False(quad.Draw(1, 1, 1, 1, 1));
		Assert.False(quad.DrawShadow(1));
		Assert.False(quad.DrawHitMask(1));
		Assert.Equal(gl.CreatedShaders.Order(), gl.DeletedShaders.Order());
		Assert.Equal(gl.CreatedPrograms.Order(), gl.DeletedPrograms.Order());
		Assert.Equal(gl.CreatedBuffers.Order(), gl.DeletedBuffers.Order());
		Assert.Equal(gl.CreatedVertexArrays.Order(), gl.DeletedVertexArrays.Order());
		Assert.Equal(entry, Capture(gl));
	}

	private static int[] Capture(OpenGLApi gl)
	{
		List<int> values = [];
		int[] parameters = [gl.GL_ACTIVE_TEXTURE, gl.GL_CURRENT_PROGRAM, gl.GL_VERTEX_ARRAY_BINDING,
			gl.GL_ARRAY_BUFFER_BINDING, gl.GL_ELEMENT_ARRAY_BUFFER_BINDING, gl.GL_FRAMEBUFFER_BINDING,
			gl.GL_BLEND_SRC_RGB, gl.GL_BLEND_DST_RGB, gl.GL_BLEND_SRC_ALPHA, gl.GL_BLEND_DST_ALPHA,
			gl.GL_BLEND_EQUATION_RGB, gl.GL_BLEND_EQUATION_ALPHA, gl.GL_FRONT_FACE];
		foreach (int parameter in parameters)
		{
			gl.GetIntegerv(parameter, out int value);
			values.Add(value);
		}
		int active = values[0];
		for (int unit = 0; unit < 3; unit++)
		{
			gl.ActiveTexture(gl.GL_TEXTURE0 + unit);
			gl.GetIntegerv(gl.GL_TEXTURE_BINDING_2D, out int texture);
			values.Add(texture);
		}
		gl.ActiveTexture(active);
		foreach (int capability in new[] { gl.GL_BLEND, gl.GL_DEPTH_TEST, gl.GL_STENCIL_TEST, gl.GL_SCISSOR_TEST, gl.GL_CULL_FACE })
			values.Add(gl.IsEnabled(capability) ? 1 : 0);
		bool[] mask = new bool[4];
		gl.GetBooleanv(gl.GL_COLOR_WRITEMASK, mask);
		values.AddRange(mask.Select(value => value ? 1 : 0));
		int[] viewport = new int[4];
		gl.GetIntegerv(gl.GL_VIEWPORT, viewport);
		values.AddRange(viewport);
		return values.ToArray();
	}
}
