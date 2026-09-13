using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static Nori.Core.Sandbox.Windows.AppContainerNativeApi;

namespace Nori.Core.Sandbox.Windows;

/// <summary>
/// 一次 <c>CreateProcess</c> 所需的非托管内存：属性列表、能力数组、环境块。
///
/// 单独成类是因为这几块的释放顺序有要求且必须成对，散在启动流程里容易在异常路径上漏掉。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AppContainerHandles : IDisposable
{
	private IntPtr _attributeList;
	private IntPtr _capabilitiesBuffer;
	private IntPtr _capabilityArray;
	private IntPtr _capabilitySid;
	private IntPtr _containerSid;
	private IntPtr _environment;

	private AppContainerHandles()
	{
	}

	/// <summary>已初始化的进程属性列表，直接交给 <c>STARTUPINFOEX</c>。</summary>
	internal IntPtr AttributeList => _attributeList;

	/// <summary>环境块；为空表示继承父进程环境。</summary>
	internal IntPtr Environment => _environment;

	/// <summary>按容器 SID 与网络开关准备好全部非托管结构。</summary>
	internal static AppContainerHandles Create(
		string containerSid, bool allowNetwork, IReadOnlyDictionary<string, string>? environmentOverrides = null)
	{
		AppContainerHandles handles = new();
		try
		{
			handles.Build(containerSid, allowNetwork, environmentOverrides);
			return handles;
		}
		catch
		{
			handles.Dispose();
			throw;
		}
	}

	private void Build(
		string containerSid, bool allowNetwork, IReadOnlyDictionary<string, string>? environmentOverrides)
	{
		if (!ConvertStringSidToSid(containerSid, out IntPtr sid))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "解析容器 SID 失败");
		}

		// SID 必须活到 CreateProcess 之后：SECURITY_CAPABILITIES 存的是指针而非副本，
		// 在此处释放会留下悬垂指针，表现为启动时报「此操作仅在应用容器上下文中有效」。
		_containerSid = sid;

		SecurityCapabilities capabilities = new()
		{
			AppContainerSid = _containerSid,
			Capabilities = IntPtr.Zero,
			CapabilityCount = 0,
			Reserved = 0,
		};

		// 网络是 capability 而非 ACL：不授 internetClient 就没有出站连接。
		if (allowNetwork)
		{
			if (!ConvertStringSidToSid(InternetClientCapability, out _capabilitySid))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "解析网络能力 SID 失败");
			}

			_capabilityArray = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());
			Marshal.StructureToPtr(
				new SidAndAttributes { Sid = _capabilitySid, Attributes = SeGroupEnabled },
				_capabilityArray,
				false);
			capabilities.Capabilities = _capabilityArray;
			capabilities.CapabilityCount = 1;
		}

		_capabilitiesBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
		Marshal.StructureToPtr(capabilities, _capabilitiesBuffer, false);

		// 第一次调用必定失败，用途是问出需要多大的缓冲区。
		IntPtr size = IntPtr.Zero;
		InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
		_attributeList = Marshal.AllocHGlobal(size);
		if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref size))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "初始化进程属性列表失败");
		}

		if (!UpdateProcThreadAttribute(
			_attributeList, 0, ProcThreadAttributeSecurityCapabilities,
			_capabilitiesBuffer, Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "写入容器属性失败");
		}

		if (environmentOverrides is {Count: > 0}) _environment = BuildEnvironment(environmentOverrides);
	}

	/// <summary>
	/// 构造 Unicode 环境块：`名=值\0` 连续排列，末尾再加一个 `\0`。
	///
	/// 必须按名称不区分大小写排序，这是 <c>CreateProcess</c> 对环境块的格式要求。
	/// </summary>
	private static IntPtr BuildEnvironment(IReadOnlyDictionary<string, string> overrides)
	{
		Dictionary<string, string> merged = new(StringComparer.OrdinalIgnoreCase);
		foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
		{
			if (entry.Key is string key && entry.Value is string value) merged[key] = value;
		}

		foreach ((string key, string value) in overrides) merged[key] = value;

		StringBuilder block = new();
		foreach ((string key, string value) in merged.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
		{
			block.Append(key).Append('=').Append(value).Append('\0');
		}

		block.Append('\0');
		return Marshal.StringToHGlobalUni(block.ToString());
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_attributeList != IntPtr.Zero)
		{
			DeleteProcThreadAttributeList(_attributeList);
			Marshal.FreeHGlobal(_attributeList);
			_attributeList = IntPtr.Zero;
		}

		if (_capabilitiesBuffer != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(_capabilitiesBuffer);
			_capabilitiesBuffer = IntPtr.Zero;
		}

		if (_capabilityArray != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(_capabilityArray);
			_capabilityArray = IntPtr.Zero;
		}

		if (_capabilitySid != IntPtr.Zero)
		{
			LocalFree(_capabilitySid);
			_capabilitySid = IntPtr.Zero;
		}

		if (_containerSid != IntPtr.Zero)
		{
			LocalFree(_containerSid);
			_containerSid = IntPtr.Zero;
		}

		if (_environment != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(_environment);
			_environment = IntPtr.Zero;
		}
	}
}
