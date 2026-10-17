using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nori.Desktop.Live2D.Gl;
using Nori.Desktop.Live2D;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

public sealed class GlRuntimeBoundaryTests
{
	[Fact]
	public void 恢复宿主VAO时不重新绑定EBO()
	{
		var gl = new RecordingGlApi();
		gl.BindVertexArray(0);
		gl.IntegerQueries.Clear();
		var scope = new GlStateScope(gl);
		Assert.DoesNotContain(gl.GL_ELEMENT_ARRAY_BUFFER_BINDING, gl.IntegerQueries);
		gl.BindVertexArray(21);
		gl.BindBuffer(gl.GL_ELEMENT_ARRAY_BUFFER, 23);
		gl.StateCalls.Clear();

		scope.Dispose();

		Assert.Contains("vao:0", gl.StateCalls);
		Assert.DoesNotContain(gl.StateCalls, call => call.StartsWith($"buffer:{gl.GL_ELEMENT_ARRAY_BUFFER}:", StringComparison.Ordinal));
		Assert.Equal(0, Read(gl, gl.GL_ELEMENT_ARRAY_BUFFER_BINDING));
		gl.BindVertexArray(21);
		Assert.Equal(23, Read(gl, gl.GL_ELEMENT_ARRAY_BUFFER_BINDING));
	}

	[Fact]
	public void PreDraw明确使用加法方程且作用域恢复宿主方程()
	{
		var gl = new RecordingGlApi();
		var renderer = RendererWithoutNativeModel(gl);
		gl.BlendEquationSeparate(0x800A, 0x800B);
		gl.ActiveTexture(gl.GL_TEXTURE0 + 2);
		gl.BindTexture(gl.GL_TEXTURE_2D, 43);
		using (var scope = new GlStateScope(gl))
		{
			renderer.PreDraw();
			Assert.Equal(0x8006, Read(gl, gl.GL_BLEND_EQUATION_RGB));
			Assert.Equal(0x8006, Read(gl, gl.GL_BLEND_EQUATION_ALPHA));
		}
		Assert.Equal(0x800A, Read(gl, gl.GL_BLEND_EQUATION_RGB));
		Assert.Equal(0x800B, Read(gl, gl.GL_BLEND_EQUATION_ALPHA));
		Assert.Equal(gl.GL_TEXTURE0 + 2, Read(gl, gl.GL_ACTIVE_TEXTURE));
		Assert.Equal(43, Read(gl, gl.GL_TEXTURE_BINDING_2D));
	}

	[Theory]
	[InlineData(false, 31, 32, 61, 62)]
	[InlineData(true, 31, 31, 61, 61)]
	public void 离屏绘制恢复入口读写FBO且下一帧重新捕获(bool es2, int expectedDraw, int expectedRead, int nextDraw, int nextRead)
	{
		var gl = new RecordingGlApi { Es2Context = es2 };
		gl.BindFramebuffer(gl.GL_FRAMEBUFFER, 31);
		if (!es2) gl.BindFramebuffer(0x8CA8, 32);
		gl.Viewport(3, 5, 720, 480);
		var surface = SurfaceWithHandle(gl, 51);

		surface.BeginDraw();
		Assert.Equal(51, Read(gl, gl.GL_FRAMEBUFFER_BINDING));
		gl.Viewport(0, 0, 256, 256);
		surface.EndDraw();

		Assert.Equal(expectedDraw, Read(gl, gl.GL_FRAMEBUFFER_BINDING));
		Assert.Equal(expectedRead, Read(gl, 0x8CAA));
		int[] viewport = new int[4];
		gl.GetIntegerv(gl.GL_VIEWPORT, viewport);
		Assert.Equal(new[] { 3, 5, 720, 480 }, viewport);

		gl.BindFramebuffer(gl.GL_FRAMEBUFFER, nextDraw);
		if (!es2) gl.BindFramebuffer(0x8CA8, nextRead);
		surface.BeginDraw();
		Assert.Equal(51, Read(gl, gl.GL_FRAMEBUFFER_BINDING));
		surface.EndDraw();
		Assert.Equal(nextDraw, Read(gl, gl.GL_FRAMEBUFFER_BINDING));
		Assert.Equal(nextRead, Read(gl, 0x8CAA));
	}

	[Fact]
	public void 网格绘制入口异常也清空裁剪上下文()
	{
		var gl = new RecordingGlApi();
		var renderer = RendererWithoutNativeModel(gl);
		var group = new MaskGroup([0]);
		renderer.ClippingContextBufferForDraw = group;
		renderer.ClippingContextBufferForMask = group;

		// 不调用原生模型：入口异常仍必须走网格绘制的 finally。
		Assert.Throws<NullReferenceException>(() => renderer.DrawMesh(0));

		Assert.Null(renderer.ClippingContextBufferForDraw);
		Assert.Null(renderer.ClippingContextBufferForMask);
		Assert.Empty(gl.StateCalls);
		Assert.Equal(0, Read(gl, gl.GL_CURRENT_PROGRAM));
		Assert.Equal(0, Read(gl, gl.GL_VERTEX_ARRAY_BINDING));
	}

	[Fact]
	public void 遮罩重建失败保留旧目标与尺寸()
	{
		var gl = new RecordingGlApi();
		var renderer = RendererWithoutNativeModel(gl);
		var atlas = (MaskAtlas)RuntimeHelpers.GetUninitializedObject(typeof(MaskAtlas));
		SetField(atlas, "<RenderTextureCount>k__BackingField", 1);
		SetField(atlas, "<ClippingMaskBufferSize>k__BackingField", new Vector2(256));
		SetField(renderer, "_mask", atlas);
		NativeGlSurface[] previous = [SurfaceWithHandle(gl, 51)];
		SetField(renderer, "_targets", previous);

		// 替身的 GenTexture 返回零，覆盖首次新目标创建失败的回滚。
		Assert.Throws<InvalidOperationException>(() => renderer.SetClippingMaskBufferSize(512, 384));

		Assert.Same(previous, GetField(renderer, "_targets"));
		Assert.True(previous[0].IsValid());
		Assert.Equal(new Vector2(256), atlas.ClippingMaskBufferSize);
	}

	[Fact]
	public void 渲染器释放清空全部资源且可重复调用()
	{
		var gl = new RecordingGlApi();
		var renderer = RendererWithoutNativeModel(gl);
		((MeshProgram)GetField(renderer, "_program")).EnsureProgram();
		SetField(renderer, "_vertices", 18);
		SetField(renderer, "_indices", 19);
		SetField(renderer, "_uvs", 20);
		var surface = SurfaceWithHandle(gl, 51);
		SetField(surface, "<ColorBuffer>k__BackingField", 41);
		SetField(renderer, "_targets", new[] { surface });

		renderer.Dispose();
		renderer.Dispose();

		Assert.Equal(0, GetField(renderer, "_vao"));
		Assert.Equal(0, GetField(renderer, "_vertices"));
		Assert.Equal(0, GetField(renderer, "_indices"));
		Assert.Equal(0, GetField(renderer, "_uvs"));
		Assert.Equal(new[] { 18, 19, 20 }, gl.DeletedBuffers.Order());
		Assert.False(surface.IsValid());
		Assert.Equal(0, surface.ColorBuffer);
		Assert.Single(gl.DeletedPrograms);
	}

	// 现有替身不生成资源句柄；只注入托管状态，不依赖本机原生模型或 GPU。
	private static NativeGlRenderer RendererWithoutNativeModel(RecordingGlApi gl)
	{
		var renderer = (NativeGlRenderer)RuntimeHelpers.GetUninitializedObject(typeof(NativeGlRenderer));
		SetField(renderer, "_gl", gl);
		SetField(renderer, "_textures", new NativeTextureOwner(gl));
		SetField(renderer, "_program", new MeshProgram(gl));
		SetField(renderer, "_targets", Array.Empty<NativeGlSurface>());
		SetField(renderer, "_vao", 17);
		return renderer;
	}

	private static NativeGlSurface SurfaceWithHandle(RecordingGlApi gl, int handle)
	{
		var surface = new NativeGlSurface(gl);
		SetField(surface, "_framebuffer", handle);
		SetField(surface, "<ColorBuffer>k__BackingField", handle + 100);
		return surface;
	}

	private static void SetField(object target, string name, object value)
	{
		FieldInfo? field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		field.SetValue(target, value);
	}

	private static object GetField(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

	private static int Read(RecordingGlApi gl, int parameter)
	{
		gl.GetIntegerv(parameter, out int value);
		return value;
	}
}
