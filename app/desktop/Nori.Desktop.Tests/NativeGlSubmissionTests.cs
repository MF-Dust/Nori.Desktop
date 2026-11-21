using System.Numerics;
using System.Reflection;
using Nori.Desktop.Live2D;
using Nori.Desktop.Live2D.Gl;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

[Collection("Native settings")]
public sealed class NativeGlSubmissionTests
{
	[Fact]
	public void 链接初始化采样单元后保留宿主当前程序()
	{
		var gl = new RecordingGlApi();
		gl.UseProgram(27);
		using var program = new MeshProgram(gl);
		program.EnsureProgram();
		gl.GetIntegerv(gl.GL_CURRENT_PROGRAM, out int current);
		Assert.Equal(27, current);
		Assert.Equal(11, gl.UniformQueries.Count);
		Assert.Equal(new[] { 0, 1 }, gl.IntegerUniforms.Select(call => call.Value));
		program.EnsureProgram();
		Assert.Equal(11, gl.UniformQueries.Count);
		Assert.Equal(2, gl.IntegerUniforms.Count);
	}

	[Live2DAssetsFact]
	public void 原生渲染器声明预乘Alpha并向着色器提交预乘标志()
	{
		var gl = new RecordingGlApi { CreateResources = true };
		using var host = new NativeModelHost(gl, NativeGlRendererBufferTests.Assets("arg-nori"));
		Assert.True(host.Renderer.IsPremultipliedAlpha);
		host.Model.Update();
		host.Renderer.PreDraw();
		host.Renderer.DrawMesh(0);
		int premultipliedLocation = Assert.Single(gl.UniformQueries, item => item.Name == "premultiplied").Location;
		Assert.Contains(gl.IntegerUniforms, call => call.Location == premultipliedLocation && call.Value == 1);
	}

	[Live2DAssetsFact]
	public void 同一pass相同网格不重复提交状态且PreDraw使上下文缓存失效()
	{
		var gl = new RecordingGlApi { CreateResources = true };
		using var host = new NativeModelHost(gl, NativeGlRendererBufferTests.Assets("arg-nori"));
		host.Model.Update();
		var renderer = host.Renderer;
		renderer.PreDraw();
		renderer.DrawMesh(0);
		var expected = gl.ElementDraws[^1];
		int integers = gl.IntegerUniforms.Count, vectors = gl.VectorUniformCalls, matrices = gl.MatrixUniformCalls;
		int pointers = gl.AttributePointers.Count, queries = gl.UniformQueries.Count;
		gl.StateCalls.Clear();
		renderer.DrawMesh(0);
		Assert.Empty(gl.StateCalls);
		Assert.Equal(expected, gl.ElementDraws[^1]);
		Assert.Equal(integers, gl.IntegerUniforms.Count);
		Assert.Equal(vectors, gl.VectorUniformCalls);
		Assert.Equal(matrices, gl.MatrixUniformCalls);

		Perturb(gl, renderer.MaskTexture(0));
		gl.StateCalls.Clear();
		renderer.PreDraw();
		renderer.DrawMesh(0);
		Assert.Equal(expected, gl.ElementDraws[^1]);
		AssertBindings(gl, expected);
		Assert.Equal(pointers, gl.AttributePointers.Count);
		Assert.Equal(queries, gl.UniformQueries.Count);
		// 恢复宿主不会改变本程序的 uniform，跨 pass 仍不重复写入。
		Assert.Equal(integers, gl.IntegerUniforms.Count);
		Assert.Equal(vectors, gl.VectorUniformCalls);
		Assert.Equal(matrices, gl.MatrixUniformCalls);
		renderer.SetModelColor(0.4f, 0.5f, 0.6f, 0.7f);
		renderer.DrawMesh(0);
		Assert.Equal(vectors + 1, gl.VectorUniformCalls);
		host.Model.Opacity = 0.3f;
		renderer.DrawMesh(0);
		Assert.Equal(vectors + 2, gl.VectorUniformCalls);
	}

	[Live2DAssetsFact]
	public void BeginMask和EndMask后未知缓存仍显式绑定完整状态并避免反馈()
	{
		var gl = new RecordingGlApi { CreateResources = true };
		using var host = new NativeModelHost(gl, NativeGlRendererBufferTests.Assets("arg-nori"));
		host.Model.Update();
		var renderer = host.Renderer;
		MaskAtlas atlas = Atlas(renderer);
		atlas.Update(false);
		MaskGroup group = atlas.Plan.Groups.First(item => item.Active);
		renderer.PreDraw();
		renderer.DrawMesh(0);
		var expected = gl.ElementDraws[^1];
		Perturb(gl, renderer.MaskTexture(0));
		renderer.BeginMask(0, 0);
		try
		{
			gl.StateCalls.Clear();
			renderer.ClippingContextBufferForMask = group;
			// 刻意不调用 PreDraw，覆盖未知缓存的直接网格入口。
			renderer.DrawMesh(0);
			var generating = gl.ElementDraws[^1];
			Assert.Equal(expected.Vao, generating.Vao);
			Assert.Equal(expected.Program, generating.Program);
			Assert.Equal(expected.Texture, generating.Texture);
			Assert.Equal(0, generating.MaskTexture);
			Assert.Equal(expected.Culling, generating.Culling);
			Assert.Equal(gl.GL_CCW, generating.FrontFace);
			Assert.Equal((gl.GL_ZERO, gl.GL_ONE_MINUS_SRC_COLOR, gl.GL_ZERO, gl.GL_ONE_MINUS_SRC_ALPHA),
				(generating.SrcRgb, generating.DstRgb, generating.SrcAlpha, generating.DstAlpha));
			AssertBindings(gl, generating);
		}
		finally { renderer.EndMask(0); }
		gl.StateCalls.Clear();
		renderer.DrawMesh(0);
		Assert.Equal(expected, gl.ElementDraws[^1]);
		AssertBindings(gl, expected);
		Assert.Null(renderer.ClippingContextBufferForDraw);
		Assert.Null(renderer.ClippingContextBufferForMask);
	}

	[Live2DAssetsFact]
	public void Draw结束后未知缓存不能复用已恢复的宿主状态()
	{
		var gl = new RecordingGlApi { CreateResources = true };
		using var host = new NativeModelHost(gl, NativeGlRendererBufferTests.Assets("arg-nori"));
		host.Model.Update();
		host.Draw(Matrix4x4.Identity);
		host.Renderer.DrawMesh(0);
		var expected = gl.ElementDraws[^1];
		host.Draw(Matrix4x4.Identity);
		Perturb(gl, host.Renderer.MaskTexture(0));
		gl.StateCalls.Clear();
		host.Renderer.DrawMesh(0);
		Assert.Equal(expected, gl.ElementDraws[^1]);
		AssertBindings(gl, expected);
	}

	[Live2DAssetsFact]
	public void Uniform位置及采样单元仅链接时设置且重新链接清空值缓存()
	{
		var gl = new RecordingGlApi { CreateResources = true };
		using var host = new NativeModelHost(gl, NativeGlRendererBufferTests.Assets("arg-nori"));
		host.Model.Update();
		using var program = new MeshProgram(gl);
		program.EnsureProgram();
		int handle = gl.CreatedPrograms[^1];
		Assert.Equal(11, gl.UniformQueries.Count(query => query.Program == handle));
		CheckSamplers(handle);
		program.Bind();
		program.Apply(host.Renderer, host.Model, 0, false);
		int integers = gl.IntegerUniforms.Count, vectors = gl.VectorUniformCalls, matrices = gl.MatrixUniformCalls;
		program.EnsureProgram();
		program.Apply(host.Renderer, host.Model, 0, false);
		Assert.Equal(11, gl.UniformQueries.Count(query => query.Program == handle));
		Assert.Equal(integers, gl.IntegerUniforms.Count);
		Assert.Equal(vectors, gl.VectorUniformCalls);
		Assert.Equal(matrices, gl.MatrixUniformCalls);
		program.Dispose();
		program.Dispose();
		program.EnsureProgram();
		int replacement = gl.CreatedPrograms[^1];
		Assert.NotEqual(handle, replacement);
		CheckSamplers(replacement);
		program.Bind();
		program.Apply(host.Renderer, host.Model, 0, false);
		Assert.Equal(integers + 4, gl.IntegerUniforms.Count);
		Assert.Equal(vectors + 3, gl.VectorUniformCalls);
		Assert.Equal(matrices + 2, gl.MatrixUniformCalls);

		void CheckSamplers(int programHandle)
		{
			foreach ((string name, int unit) in new[] { ("image", 0), ("maskImage", 1) })
			{
				int location = Assert.Single(gl.UniformQueries, query => query.Program == programHandle && query.Name == name).Location;
				Assert.Equal(unit, Assert.Single(gl.IntegerUniforms, call => call.Location == location).Value);
			}
		}
	}

	[Live2DAssetsFact]
	public void 每帧提交数等于可见有效网格加遮罩且高精度不遗漏重复遮罩()
	{
		foreach (string name in new[] { "arg-nori", "nori" })
		foreach (bool withTextures in new[] { true, false })
		foreach (bool high in new[] { false, true })
		{
			var gl = new RecordingGlApi { CreateResources = true };
			using var host = new NativeModelHost(gl, NativeGlRendererBufferTests.Assets(name, withTextures));
			host.Animation.RandomMotion = false;
			host.Renderer.UseHighPrecisionMask = high;
			for (int frame = 0; frame < 3; frame++)
			{
				host.Update(1 / 60f);
				int before = gl.DrawCalls;
				gl.BufferUploads.Clear();
				host.Draw(Matrix4x4.Identity);
				var upload = Assert.Single(gl.BufferUploads);
				Assert.Equal(gl.GL_ARRAY_BUFFER, upload.Target);
				Assert.Equal(gl.GL_DYNAMIC_DRAW, upload.Usage);
				MaskPlan plan = Atlas(host.Renderer).Plan;
				int expected = 0;
				for (int i = 0; i < host.Model.DrawableCount; i++)
				{
					if (!host.Model.GetDrawableDynamicFlagIsVisible(i) || plan.Drawables[i] is { Active: false }) continue;
					if (Valid(i)) expected++;
					if (high && plan.Drawables[i] is { } group)
						expected += group.Masks.Count(Valid);
				}
				if (!high)
					expected += plan.Groups.Where(group => group.Active).Sum(group => group.Masks.Count(Valid));
				Assert.Equal(expected, gl.DrawCalls - before);
				Assert.Null(host.Renderer.ClippingContextBufferForDraw);
				Assert.Null(host.Renderer.ClippingContextBufferForMask);
			}
			bool Valid(int index) => host.Renderer.GetBindedTextureId(host.Model.GetDrawableTextureIndex(index)) != 0;
		}
	}

	private static MaskAtlas Atlas(NativeGlRenderer renderer) => (MaskAtlas)typeof(NativeGlRenderer)
		.GetField("_mask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;

	private static void Perturb(RecordingGlApi gl, int maskTexture)
	{
		gl.BindVertexArray(998);
		gl.UseProgram(999);
		gl.ActiveTexture(gl.GL_TEXTURE0);
		gl.BindTexture(gl.GL_TEXTURE_2D, 997);
		gl.ActiveTexture(gl.GL_TEXTURE1);
		gl.BindTexture(gl.GL_TEXTURE_2D, maskTexture);
		gl.BlendFuncSeparate(gl.GL_ZERO, gl.GL_ZERO, gl.GL_ZERO, gl.GL_ZERO);
		gl.FrontFace(0x0900);
		gl.Enable(gl.GL_CULL_FACE);
		gl.Disable(gl.GL_BLEND);
		gl.Enable(gl.GL_DEPTH_TEST);
		gl.Enable(gl.GL_STENCIL_TEST);
		gl.Enable(gl.GL_SCISSOR_TEST);
		gl.ColorMask(false, false, false, false);
		gl.BlendEquationSeparate(0x800A, 0x800B);
	}

	private static void AssertBindings(RecordingGlApi gl, RecordingGlApi.ElementDraw draw)
	{
		Assert.Contains($"vao:{draw.Vao}", gl.StateCalls);
		Assert.Contains($"program:{draw.Program}", gl.StateCalls);
		Assert.Contains($"texture:{gl.GL_TEXTURE0}:{draw.Texture}", gl.StateCalls);
		Assert.Contains($"texture:{gl.GL_TEXTURE1}:{draw.MaskTexture}", gl.StateCalls);
		Assert.Contains($"blend:{draw.SrcRgb}:{draw.DstRgb}:{draw.SrcAlpha}:{draw.DstAlpha}", gl.StateCalls);
		Assert.Contains($"{(draw.Culling ? "enable" : "disable")}:{gl.GL_CULL_FACE}", gl.StateCalls);
		Assert.True(gl.IsEnabled(gl.GL_BLEND));
		Assert.False(gl.IsEnabled(gl.GL_DEPTH_TEST));
		Assert.False(gl.IsEnabled(gl.GL_STENCIL_TEST));
		Assert.False(gl.IsEnabled(gl.GL_SCISSOR_TEST));
		gl.GetIntegerv(gl.GL_BLEND_EQUATION_RGB, out int equation);
		Assert.Equal(0x8006, equation);
		bool[] colors = new bool[4];
		gl.GetBooleanv(gl.GL_COLOR_WRITEMASK, colors);
		Assert.All(colors, Assert.True);
	}
}
