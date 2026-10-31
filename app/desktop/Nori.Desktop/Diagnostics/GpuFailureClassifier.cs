using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Avalonia.Platform;

namespace Nori.Desktop.Diagnostics;

/// <summary>可恢复的 GPU 渲染失败；Kind 直接用作遥测 exception_kind。</summary>
internal readonly record struct GpuFailure(string Kind, int? HResult = null, int? GlError = null)
{
	/// <summary>图形设备或上下文已丢失：驱动重置、睡眠唤醒、远程桌面、切换显卡等。</summary>
	public const string DeviceLost = "gpu_device_lost";

	/// <summary>GPU 互操作调用失败但未确认设备丢失，例如显存不足时创建交换链纹理失败。</summary>
	public const string InteropFailure = "gpu_interop_failure";

	/// <summary>只含类别与错误码的安全遥测标签。</summary>
	public IReadOnlyDictionary<string, string> Tags()
	{
		Dictionary<string, string> tags = new() { ["exception_kind"] = Kind };
		if (HResult is { } hresult) tags["hresult"] = $"0x{hresult:X8}";
		if (GlError is { } glError) tags["gl_error"] = $"0x{glError:X4}";
		return tags;
	}
}

/// <summary>
/// 识别 Avalonia OpenGL 控件合成路径上的 GPU 失败（NORI-31 / NORI-32）。
///
/// <c>OpenGlControlBase</c> 在合成提交时向 ANGLE/DXGI 借交换链纹理，设备丢失会以
/// <c>COMException("CreateTexture2D failed")</c> 冲进 UI 线程，或以
/// <see cref="PlatformGraphicsContextLostException"/> 出现在未观察任务里。
/// 分类只看异常类型、HRESULT、GL 错误码与抛出栈帧所在的命名空间，不读异常正文。
/// </summary>
internal static class GpuFailureClassifier
{
	private static readonly string[] GpuInteropNamespaces =
	[
		"Avalonia.OpenGL",
		"Avalonia.Win32.OpenGl",
		"Avalonia.Win32.DirectX",
	];

	/// <summary>DXGI_ERROR_DEVICE_REMOVED / HUNG / RESET / DRIVER_INTERNAL_ERROR。</summary>
	private static readonly HashSet<int> DeviceLostHResults =
	[
		unchecked((int) 0x887A0005),
		unchecked((int) 0x887A0006),
		unchecked((int) 0x887A0007),
		unchecked((int) 0x887A0020),
	];

	/// <summary>EGL_CONTEXT_LOST。</summary>
	private const int EglContextLost = 0x300E;

	public static GpuFailure? Classify(Exception? exception) => Classify(exception, GpuInteropNamespaces);

	internal static GpuFailure? Classify(Exception? exception, IReadOnlyCollection<string> interopNamespaces)
	{
		switch (exception)
		{
			case null:
				return null;
			case AggregateException aggregate:
				return ClassifyAll(aggregate.InnerExceptions, interopNamespaces);
			case TargetInvocationException { InnerException: { } inner }:
				return Classify(inner, interopNamespaces);
		}

		for (Exception? current = exception; current is not null; current = current.InnerException)
		{
			if (current is PlatformGraphicsContextLostException) return new GpuFailure(GpuFailure.DeviceLost);
		}

		if (exception is COMException com)
		{
			if (DeviceLostHResults.Contains(com.HResult)) return new GpuFailure(GpuFailure.DeviceLost, com.HResult);
			return ThrownFromGpuInterop(com, interopNamespaces) ? new GpuFailure(GpuFailure.InteropFailure, com.HResult) : null;
		}

		if (exception is OpenGlException gl && ThrownFromGpuInterop(gl, interopNamespaces))
		{
			string kind = gl.ErrorCode == EglContextLost ? GpuFailure.DeviceLost : GpuFailure.InteropFailure;
			return new GpuFailure(kind, GlError: gl.ErrorCode);
		}
		return null;
	}

	/// <summary>聚合异常只有在每个内层都是 GPU 失败时才算；任一设备丢失即按设备丢失处理。</summary>
	private static GpuFailure? ClassifyAll(IReadOnlyCollection<Exception> exceptions, IReadOnlyCollection<string> interopNamespaces)
	{
		GpuFailure? result = null;
		foreach (Exception inner in exceptions)
		{
			if (Classify(inner, interopNamespaces) is not { } failure) return null;
			if (result is null || (failure.Kind == GpuFailure.DeviceLost && result.Value.Kind != GpuFailure.DeviceLost))
				result = failure;
		}
		return result;
	}

	private static bool ThrownFromGpuInterop(Exception exception, IReadOnlyCollection<string> interopNamespaces)
	{
		foreach (StackFrame frame in new StackTrace(exception, false).GetFrames())
		{
			string? frameNamespace = frame.GetMethod()?.DeclaringType?.Namespace;
			if (frameNamespace is null) continue;
			foreach (string prefix in interopNamespaces)
			{
				if (frameNamespace == prefix || frameNamespace.StartsWith(prefix + ".", StringComparison.Ordinal)) return true;
			}
		}
		return false;
	}
}
