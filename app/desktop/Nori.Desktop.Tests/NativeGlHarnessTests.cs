using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Nori.Core.Live2D;
using Nori.Desktop.Live2D;
using Xunit.Abstractions;

namespace Nori.Desktop.Tests;

/// <summary>显式启用的 Windows ANGLE / Linux EGL 双上下文真实光栅化验证。</summary>
public sealed class NativeGlAssetsTheoryAttribute : TheoryAttribute
{
	public NativeGlAssetsTheoryAttribute()
	{
		if ((!System.OperatingSystem.IsWindows() && !System.OperatingSystem.IsLinux()) || !Environment.Is64BitProcess
			|| Environment.GetEnvironmentVariable("NORI_TEST_NATIVE_GL") != "1"
			|| Environment.GetEnvironmentVariable("NORI_TEST_LIVE2D_ASSETS") != "1")
			Skip = "Windows x64 或 Linux EGL 下设置 NORI_TEST_NATIVE_GL=1 与 NORI_TEST_LIVE2D_ASSETS=1，并提供 NORI_LIVE2D_FIXTURES。";
	}
}

[Collection("Native settings")]
public sealed class NativeGlHarnessTests(ITestOutputHelper output)
{
	[NativeGlAssetsTheory]
	[InlineData(2)]
	[InlineData(3)]
	public async Task 真实模型在GLES双上下文绘制并隔离释放(int version)
	{
		PreparedModel? pet = null, preview = null;
		await BridgeCommandsTests.WithSettingsUiAsync(async () =>
		{
			pet = await Task.Run(() => ModelPreparation.PrepareAsync("arg-nori",
				Path.GetDirectoryName(PreparedModelAssetsTests.FindFixture("arg-nori", "ARGNori.model3.json"))!, 1, CancellationToken.None));
			preview = await Task.Run(() => ModelPreparation.PrepareAsync("nori",
				Path.GetDirectoryName(PreparedModelAssetsTests.FindFixture("nori", "Nori.model3.json"))!, 1, CancellationToken.None));
		});
		// 后续不 await，所有创建、绘制、回读和释放均留在当前 EGL 线程。
		using var context = new EglTestContext(version);
		context.MakeCurrent(0);
		output.WriteLine($"GLES {version} 渲染器：{context.Renderer}");
		var petGl = context.CreateApi();
		using var first = new NativeModelHost(petGl, pet!.Assets);
		first.Animation.RandomMotion = false;
		context.MakeCurrent(1);
		var previewGl = context.CreateApi();
		using var second = new NativeModelHost(previewGl, preview!.Assets);
		second.Animation.RandomMotion = false;
		try
		{
			foreach ((int width, int height) in new[] { (720, 480), (1920, 1080) })
			{
				context.MakeCurrent(0);
				first.Update(0.016f);
				byte[] pixels = DrawAndRead(first, petGl, width, height);
				Assert.Equal(0, context.GetError());
				AssertCoverage(pixels, first);
				context.MakeCurrent(1);
				second.Update(0.016f);
				byte[] previewPixels = DrawAndRead(second, previewGl, width, height);
				Assert.Equal(0, context.GetError());
				AssertCoverage(previewPixels, second);
			}
			context.MakeCurrent(1);
			byte[] before = DrawAndRead(second, previewGl, 720, 480);
			context.MakeCurrent(0);
			first.Dispose();
			Assert.Equal(0, context.GetError());
			context.MakeCurrent(1);
			Assert.Equal(before, DrawAndRead(second, previewGl, 720, 480));
			Assert.False(second.Model.IsDisposed);
			Assert.Equal(0, context.GetError());
		}
		finally
		{
			context.MakeCurrent(0);
			first.Dispose();
			context.MakeCurrent(1);
			second.Dispose();
		}
	}

	private static unsafe void AssertCoverage(byte[] pixels, NativeModelHost model)
	{
		int visible = 0;
		for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) visible++;
		int drawables = model.Model.DrawableCount, active = 0;
		float opacity = 0;
		Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
		for (int i = 0; i < drawables; i++)
		{
			if (model.Model.GetDrawableDynamicFlagIsVisible(i)) active++;
			opacity += model.Model.GetDrawableOpacity(i);
			Vector2* vertices = model.Model.GetDrawableVertexPositions(i);
			for (int v = 0; v < model.Model.GetDrawableVertexCount(i); v++)
			{
				min = Vector2.Min(min, vertices[v]);
				max = Vector2.Max(max, vertices[v]);
			}
		}
		Assert.True(visible >= pixels.Length / 4000 && visible < pixels.Length / 4,
			$"可见像素={visible}，网格={active}/{drawables}，不透明度和={opacity}，顶点范围={min}..{max}");
	}

	internal static unsafe byte[] DrawAndRead(NativeModelHost model, AvaloniaGlApi gl, int width, int height)
	{
		using var target = new NativeGlSurface(gl);
		Assert.True(target.CreateOffscreenSurface(width, height));
		model.Renderer.SetClippingMaskBufferSize(1024, 1024);
		model.Renderer.Anisotropy = 4;
		target.BeginDraw();
		try
		{
			gl.Viewport(0, 0, width, height);
			target.Clear(0, 0, 0, 0);
			PetViewportProjection projection = PetViewportMapping.CalculateFitProjection(width, height,
				model.Model.CanvasSize.X, model.Model.CanvasSize.Y, 1);
			model.Draw(Matrix4x4.CreateScale((float)projection.ScaleX, (float)projection.ScaleY, 1));
			// 合成真实场景纹理，覆盖与宠物/预览共用的四边形资源和状态边界。
			using var composed = new NativeGlSurface(gl);
			using var quad = new OpenGLTextureQuad(gl);
			Assert.True(quad.IsAvailable);
			Assert.True(composed.CreateOffscreenSurface(width, height));
			composed.BeginDraw();
			try
			{
				gl.Viewport(0, 0, width, height);
				composed.Clear(0, 0, 0, 0);
				Assert.True(quad.Draw(target.ColorBuffer, 1, 1, 1, 1));
				byte[] pixels = new byte[checked(width * height * 4)];
			fixed (byte* data = pixels)
					gl.GLReadPixels(0, 0, width, height, gl.GL_RGBA, gl.GL_UNSIGNED_BYTE, (nint)data);
				return pixels;
			}
			finally { composed.EndDraw(); }
		}
		finally { target.EndDraw(); }
	}
}

/// <summary>Windows 复用 Avalonia ANGLE，Linux 使用系统 EGL；两个 GLES 上下文不共享资源。</summary>
internal sealed unsafe class EglTestContext : IDisposable
{
	private readonly nint _library;
	private nint _display;
	private readonly nint[] _contexts = new nint[2];
	private readonly nint[] _surfaces = new nint[2];
	private readonly int _version;

	public EglTestContext(int version)
	{
		_version = version;
		_library = NativeLibrary.Load(System.OperatingSystem.IsLinux() ? "libEGL.so.1"
			: Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "av_libglesv2.dll"));
		try
		{
			if (System.OperatingSystem.IsLinux())
				_display = ((delegate* unmanaged[Stdcall]<nint, nint>)Export("EGL_GetDisplay"))(0);
			else
			{
				int[] displayAttributes = [0x3203, 0x3208, 0x3038]; // ANGLE D3D11
				fixed (int* attributes = displayAttributes)
					_display = ((delegate* unmanaged[Stdcall]<int, nint, int*, nint>)Export("EGL_GetPlatformDisplayEXT"))(0x3202, 0, attributes);
			}
			Assert.NotEqual(0, _display);
			int major, minor;
			Assert.NotEqual(0, ((delegate* unmanaged[Stdcall]<nint, int*, int*, int>)Export("EGL_Initialize"))(_display, &major, &minor));
			Assert.NotEqual(0, ((delegate* unmanaged[Stdcall]<int, int>)Export("EGL_BindAPI"))(0x30A0));
			int[] configAttributes = [0x3033, 1, 0x3040, version == 2 ? 4 : 0x40, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3038];
			nint config = 0;
			int count;
			fixed (int* attributes = configAttributes)
				Assert.NotEqual(0, ((delegate* unmanaged[Stdcall]<nint, int*, nint*, int, int*, int>)Export("EGL_ChooseConfig"))(_display, attributes, &config, 1, &count));
			Assert.Equal(1, count);
			int[] contextAttributes = [0x3098, version, 0x3038];
			int[] surfaceAttributes = [0x3057, 1, 0x3056, 1, 0x3038];
			for (int i = 0; i < 2; i++)
			{
				fixed (int* attributes = contextAttributes)
					_contexts[i] = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int*, nint>)Export("EGL_CreateContext"))(_display, config, 0, attributes);
				Assert.NotEqual(0, _contexts[i]);
				fixed (int* attributes = surfaceAttributes)
					_surfaces[i] = ((delegate* unmanaged[Stdcall]<nint, nint, int*, nint>)Export("EGL_CreatePbufferSurface"))(_display, config, attributes);
				Assert.NotEqual(0, _surfaces[i]);
			}
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	private nint Export(string name) => NativeLibrary.GetExport(_library,
		System.OperatingSystem.IsLinux() ? "egl" + name[4..] : name);
	public void MakeCurrent(int index) => Assert.NotEqual(0,
		((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)Export("EGL_MakeCurrent"))(_display, _surfaces[index], _surfaces[index], _contexts[index]));
	public int GetError() => ((delegate* unmanaged[Stdcall]<int>)Resolve("glGetError"))();
	public string Renderer => Marshal.PtrToStringAnsi(((delegate* unmanaged[Stdcall]<int, nint>)Resolve("glGetString"))(0x1F01)) ?? "";
	public AvaloniaGlApi CreateApi() => new(new GlInterface(new GlVersion(GlProfileType.OpenGLES, _version, 0), Resolve));
	private nint Resolve(string name)
	{
		if (!System.OperatingSystem.IsLinux())
			return NativeLibrary.TryGetExport(_library, name, out nint address) ? address : 0;
		nint symbol = Marshal.StringToCoTaskMemUTF8(name);
		try { return ((delegate* unmanaged[Stdcall]<nint, nint>)Export("EGL_GetProcAddress"))(symbol); }
		finally { Marshal.FreeCoTaskMem(symbol); }
	}

	public void Dispose()
	{
		if (_display != 0)
		{
			((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)Export("EGL_MakeCurrent"))(_display, 0, 0, 0);
			for (int i = 0; i < 2; i++)
			{
				if (_contexts[i] != 0) ((delegate* unmanaged[Stdcall]<nint, nint, int>)Export("EGL_DestroyContext"))(_display, _contexts[i]);
				if (_surfaces[i] != 0) ((delegate* unmanaged[Stdcall]<nint, nint, int>)Export("EGL_DestroySurface"))(_display, _surfaces[i]);
			}
			((delegate* unmanaged[Stdcall]<nint, int>)Export("EGL_Terminate"))(_display);
			_display = 0;
		}
		NativeLibrary.Free(_library);
	}
}
