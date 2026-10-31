using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Tests.GpuInteropFixture;

namespace Nori.Desktop.Tests
{
	/// <summary>NORI-31 / NORI-32：Avalonia OpenGL 合成路径上的 GPU 失败分类。</summary>
	public sealed class GpuFailureClassifierTests
	{
		private const int DeviceRemoved = unchecked((int) 0x887A0005);
		private const int OutOfMemory = unchecked((int) 0x8007000E);
		private static readonly string[] FixtureNamespaces = [typeof(FakeGpuInterop).Namespace!];

		[Fact]
		public void 上下文丢失按设备丢失处理且内层包装也能识别()
		{
			Assert.Equal(new GpuFailure(GpuFailure.DeviceLost), GpuFailureClassifier.Classify(new PlatformGraphicsContextLostException()));
			Assert.Equal(GpuFailure.DeviceLost, GpuFailureClassifier.Classify(
				new InvalidOperationException("包装", new PlatformGraphicsContextLostException()))?.Kind);
		}

		[Fact]
		public void DXGI设备移除HRESULT不论来源都按设备丢失处理()
		{
			GpuFailure? failure = GpuFailureClassifier.Classify(new COMException("CreateTexture2D failed", DeviceRemoved));

			Assert.Equal(new GpuFailure(GpuFailure.DeviceLost, DeviceRemoved), failure);
			Assert.Equal("0x887A0005", failure!.Value.Tags()["hresult"]);
		}

		[Fact]
		public void 互操作栈帧抛出的其他COM与GL失败归为互操作失败()
		{
			COMException com = Assert.Throws<COMException>(() => FakeGpuInterop.CreateTexture2D(OutOfMemory));
			OpenGlException gl = Assert.Throws<OpenGlException>(FakeGpuInterop.ConfigureFbo);

			Assert.Equal(new GpuFailure(GpuFailure.InteropFailure, OutOfMemory), GpuFailureClassifier.Classify(com, FixtureNamespaces));
			Assert.Equal(GpuFailure.InteropFailure, GpuFailureClassifier.Classify(gl, FixtureNamespaces)?.Kind);
		}

		[Fact]
		public void EGL上下文丢失错误码按设备丢失处理()
		{
			OpenGlException gl = Assert.Throws<OpenGlException>(FakeGpuInterop.MakeCurrentLost);

			GpuFailure? failure = GpuFailureClassifier.Classify(gl, FixtureNamespaces);

			Assert.Equal(GpuFailure.DeviceLost, failure?.Kind);
			Assert.Equal("0x300E", failure!.Value.Tags()["gl_error"]);
		}

		[Fact]
		public void 非GPU来源的COM与普通异常不被吞掉()
		{
			COMException com = Assert.Throws<COMException>(() => ThrowCom(OutOfMemory));

			Assert.Null(GpuFailureClassifier.Classify(com, FixtureNamespaces));
			Assert.Null(GpuFailureClassifier.Classify(new InvalidOperationException("业务错误")));
			Assert.Null(GpuFailureClassifier.Classify(new OpenGlException("未抛出的 GL 异常"), FixtureNamespaces));
		}

		[Fact]
		public void 未观察聚合异常只有全部为GPU失败时才归类()
		{
			AggregateException lost = new(new PlatformGraphicsContextLostException(), new PlatformGraphicsContextLostException());
			AggregateException mixed = new(new PlatformGraphicsContextLostException(), new InvalidOperationException("业务错误"));

			Assert.Equal(GpuFailure.DeviceLost, GpuFailureClassifier.Classify(lost)?.Kind);
			Assert.Null(GpuFailureClassifier.Classify(mixed));
			Assert.Null(GpuFailureClassifier.Classify(new AggregateException()));
		}

		[Fact]
		public void 标签只含类别与错误码()
		{
			IReadOnlyDictionary<string, string> tags = new GpuFailure(GpuFailure.InteropFailure).Tags();

			Assert.Equal(GpuFailure.InteropFailure, Assert.Single(tags).Value);
		}

		private static void ThrowCom(int hresult) => throw new COMException("Clipboard failed", hresult);
	}
}

namespace Nori.Desktop.Tests.GpuInteropFixture
{
	/// <summary>在独立命名空间里模拟 Avalonia GPU 互操作栈帧。</summary>
	internal static class FakeGpuInterop
	{
		public static void CreateTexture2D(int hresult) => throw new COMException("CreateTexture2D failed", hresult);

		public static void ConfigureFbo() => throw OpenGlException.GetFormattedException("Unable to configure OpenGL FBO", 0x0506);

		public static void MakeCurrentLost() => throw OpenGlException.GetFormattedEglException("eglMakeCurrent", 0x300E);
	}
}
