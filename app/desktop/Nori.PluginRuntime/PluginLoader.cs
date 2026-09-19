using System.Reflection;
namespace Nori.PluginRuntime;

/// <summary>按 manifest 精确加载一个插件入口，不扫描程序集中的其它类型。</summary>
internal sealed class PluginLoader
{
	/// <summary>加载入口实例并返回其独立的可回收 ALC。</summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "加载失败后的 ALC 卸载只能尽力执行，必须保留原始插件错误。")]
	public INoriPlugin Load(PluginManifest manifest, string installDirectory, out PluginLoadContext loadContext)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		manifest = PluginManifestReader.Validate(manifest);
		ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
		string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
		string assemblyPath = Path.GetFullPath(Path.Combine(root, manifest.Runtime.Assembly.Replace('/', Path.DirectorySeparatorChar)));
		if (!IsWithin(root, assemblyPath) || !File.Exists(assemblyPath))
			throw new PluginException(PluginErrorCodes.EntryAssemblyMissing, "manifest 指定的入口程序集不存在");

		PluginLoadContext.EnsureReferencesAllowed(root);
		loadContext = new PluginLoadContext(assemblyPath);
		try
		{
			Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
			Type? entryType = assembly.GetType(manifest.Runtime.EntryType, throwOnError: false, ignoreCase: false);
			if (entryType is null || !typeof(INoriPlugin).IsAssignableFrom(entryType) || entryType.IsAbstract || entryType.ContainsGenericParameters)
				throw new PluginException(PluginErrorCodes.EntryTypeNotFound, "manifest entryType 不是有效的 INoriPlugin");
			if (entryType.GetConstructor(Type.EmptyTypes) is null)
				throw new PluginException(PluginErrorCodes.EntryConstructorMissing, "插件入口必须有 public parameterless constructor");
			if (Activator.CreateInstance(entryType) is not INoriPlugin instance)
				throw new PluginException(PluginErrorCodes.EntryTypeNotFound, "插件入口无法创建");
			return instance;
		}
		catch (PluginException)
		{
			try { loadContext.Unload(); } catch { }
			throw;
		}
		catch (Exception exception)
		{
			try { loadContext.Unload(); } catch { }
			throw new PluginException(PluginErrorCodes.EntryTypeNotFound, "插件入口加载失败", exception);
		}
	}

	private static bool IsWithin(string root, string path)
	{
		StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		return path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
	}
}
