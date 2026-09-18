namespace Nori.Core.Assets;

/// <summary>资源服务的附加路由。</summary>
public interface IAssetRoute
{
	/// <summary>路由根段，例如 <c>plugins</c>。</summary>
	string Segment { get; }

	/// <summary>把已百分号解码的相对路径解析为可公开文件。</summary>
	AssetRouteFile? Resolve(string relativePath);
}

/// <summary>附加资源路由返回的安全文件信息。</summary>
public sealed record AssetRouteFile(
	string FilePath,
	string ContentType,
	string CacheControl = "public, max-age=3600")
{
	/// <summary>允许隔离卡片读取公开模块资源；仅用于无凭据的 opaque origin GET/HEAD。</summary>
	public bool AllowOpaqueOrigin { get; init; }
}
