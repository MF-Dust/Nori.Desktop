using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
namespace Nori.PluginRuntime;

/// <summary>每个插件独立的可回收依赖加载上下文。</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{

	private static readonly HashSet<string> SharedAvaloniaAssemblies = new(StringComparer.Ordinal)
	{
		"Avalonia", "Avalonia.Base", "Avalonia.Controls", "Avalonia.Markup", "Avalonia.Markup.Xaml",
		"Avalonia.Themes.Fluent", "Avalonia.Controls.DataGrid", "Avalonia.Controls.ColorPicker", "Avalonia.Controls.ItemsRepeater",
	};

	private readonly AssemblyDependencyResolver _resolver;
	private readonly string _pluginRoot;

	public PluginLoadContext(string mainAssemblyPath)
		: base($"Nori.Plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
	{
		string fullPath = Path.GetFullPath(mainAssemblyPath);
		_resolver = new AssemblyDependencyResolver(fullPath);
		_pluginRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath) ?? fullPath, ".."));
	}

	protected override Assembly? Load(AssemblyName assemblyName)
	{
		if (assemblyName.Name is { } name && string.Equals(name, PluginAssemblyPolicy.CurrentAssemblyName, StringComparison.Ordinal))
			return typeof(INoriPlugin).Assembly;

		if (assemblyName.Name is { } avaloniaName && SharedAvaloniaAssemblies.Contains(avaloniaName))
			return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
		if (assemblyName.Name is { } forbidden && IsForbidden(forbidden))
			throw new PluginException(PluginErrorCodes.ForbiddenReference, $"插件引用了禁止的宿主程序集: {forbidden}");

		string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
		if (resolved is null || !IsWithin(_pluginRoot, resolved)) return null;
		return LoadFromAssemblyPath(resolved);
	}

	protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
	{
		string? resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
		return resolved is not null && IsWithin(_pluginRoot, resolved)
			? LoadUnmanagedDllFromPath(resolved)
			: IntPtr.Zero;
	}

	/// <summary>预扫描插件目录，拒绝宿主内部引用、重复 contract DLL 与插件树内链接。</summary>
	public static void EnsureReferencesAllowed(string pluginDirectory)
	{
		if (!Directory.Exists(pluginDirectory)) throw new PluginException(PluginErrorCodes.InvalidPackage, "插件目录不存在");
		IReadOnlyList<string> dlls = PluginPathSafety.EnumerateDllFilesWithoutReparsePoints(
			pluginDirectory,
			PluginErrorCodes.PackagePathDenied,
			"插件目录包含符号链接");
		foreach (string path in dlls)
		{
			if (Path.GetFileNameWithoutExtension(path).StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
				throw new PluginException(PluginErrorCodes.ForbiddenReference, "插件包不得携带 Avalonia 框架程序集");
			if (PluginAssemblyPolicy.IsContractAssemblyFile(path))
				throw new PluginException(PluginErrorCodes.ContractAssemblyDenied, "插件包不得携带宿主 contract 程序集");
			EnsureAssemblyReferencesAllowed(path);
		}
	}

	private static void EnsureAssemblyReferencesAllowed(string assemblyPath)
	{
		try
		{
			using FileStream stream = File.OpenRead(assemblyPath);
			using PEReader reader = new(stream);
			// runtimes/win-* 中的原生 DLL 是合法插件依赖。只有托管程序集才有
			// AssemblyReference 元数据可供宿主做禁止引用扫描。
			if (!reader.HasMetadata) return;
			MetadataReader metadata = reader.GetMetadataReader();
			if (metadata.IsAssembly && metadata.GetString(metadata.GetAssemblyDefinition().Name).StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
				throw new PluginException(PluginErrorCodes.ForbiddenReference, "插件包不得携带 Avalonia 框架程序集");
			foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
			{
				string name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
				if (IsForbidden(name)) throw new PluginException(PluginErrorCodes.ForbiddenReference, $"插件引用了禁止的宿主程序集: {name}");
			}
		}
		catch (PluginException)
		{
			throw;
		}
		catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			throw new PluginException(PluginErrorCodes.InvalidPackage, "插件程序集格式无效", exception);
		}
	}

	private static bool IsForbidden(string name) =>
		name.Equals("Nori.Core", StringComparison.OrdinalIgnoreCase) ||
		name.Equals("Nori.Desktop", StringComparison.OrdinalIgnoreCase) ||
		PluginAssemblyPolicy.IsLegacyContractAssemblyName(name) ||
		(name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) && !SharedAvaloniaAssemblies.Contains(name)) ||
		name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase);

	private static bool IsWithin(string root, string path)
	{
		string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		string fullPath = Path.GetFullPath(path);
		StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison) || fullPath.Equals(fullRoot, comparison);
	}
}
