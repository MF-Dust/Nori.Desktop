using Nori.Core.Assets;

namespace Nori.PluginRuntime;

/// <summary>插件公开资源的 AssetServer 附加路由。</summary>
internal sealed class PluginAssetRoute(PluginManager manager) : IAssetRoute
{
	private readonly PluginManager _manager = manager ?? throw new ArgumentNullException(nameof(manager));

	public string Segment => "plugins";

	public AssetRouteFile? Resolve(string relativePath)
	{
		string[] parts = relativePath.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 2 || !PluginManifestReader.IsValidPluginId(parts[0]) || !PluginAssetProvider.IsPublicAsset(parts[1])) return null;

		string? root = _manager.ResolveAssetRoot(parts[0]);
		string? resolved = root is null ? null : AssetPath.ResolveExact(root, parts[1]);
		return resolved is null ? null : new AssetRouteFile(resolved, AssetPath.MimeFor(parts[1]))
		{
			// 卡片模块在 opaque origin 中运行，仅公开 web 资源需要无凭据 CORS。
			AllowOpaqueOrigin = parts[1].StartsWith("web/", StringComparison.Ordinal),
		};
	}
}
