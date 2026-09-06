using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nori.Core.Update;

/// <summary>发布包 UPDATE-{rid}.json 清单元数据。</summary>
public sealed record UpdateManifest(
	int SchemaVersion,
	string ProductVersion,
	string NumericVersion,
	int Revision,
	string Rid,
	string ReleaseTag,
	string PackageName,
	string DownloadUrl,
	string Sha256,
	long SizeBytes,
	string ArchiveType,
	string Entrypoint,
	int LauncherProtocol,
	DateTimeOffset? PublishedAt,
	string? ReleaseNotes)
{
	private static readonly Regex NumericVersionRegex = new("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
	private static readonly Regex Sha256Regex = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
	public const long MaxPackageSizeBytes = 512L * 1024 * 1024; // 512 MiB

	/// <summary>从 JSON 字符串解析并严格校验更新清单。</summary>
	public static UpdateManifest FromJson(string json)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(json);
		if (Encoding.UTF8.GetByteCount(json) > 1024 * 1024)
		{
			throw new InvalidOperationException("更新清单元数据体积超过 1 MiB 上限");
		}

		using JsonDocument doc = JsonDocument.Parse(json);
		JsonElement root = doc.RootElement;

		int schema = RequiredInt(root, "schema_version");
		if (schema != 1) throw new InvalidOperationException($"不支持的更新清单协议版本: {schema}");

		string product = RequiredString(root, "product_version");
		string numeric = RequiredString(root, "numeric_version");
		if (!NumericVersionRegex.IsMatch(numeric) || !numeric.Split('.').All(part => ushort.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
		{
			throw new InvalidOperationException($"更新清单数字版本格式无效: {numeric}");
		}

		int revision = RequiredInt(root, "revision");
		if (revision < 0) throw new InvalidOperationException($"更新清单 revision 不能为负数: {revision}");

		string rid = RequiredString(root, "rid");
		string releaseTag = RequiredString(root, "release_tag");
		string packageName = RequiredString(root, "package_name");
		if (packageName.Contains('/') || packageName.Contains('\\') || packageName.Contains("..")
			|| packageName.Any(char.IsControl) || packageName.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0
			|| packageName.EndsWith('.') || packageName.EndsWith(' '))
		{
			throw new InvalidOperationException($"更新清单文件名包含非法字符: {packageName}");
		}

		string downloadUrl = RequiredString(root, "download_url");
		if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
			|| uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
		{
			throw new InvalidOperationException($"更新下载地址必须是 HTTPS URL: {downloadUrl}");
		}
		if (!IsAllowedHost(uri.Host))
		{
			throw new InvalidOperationException($"更新下载主机不在受信任白名单内: {uri.Host}");
		}

		string sha256 = RequiredString(root, "sha256").ToLowerInvariant();
		if (!Sha256Regex.IsMatch(sha256))
		{
			throw new InvalidOperationException($"更新清单 SHA-256 格式无效: {sha256}");
		}

		long sizeBytes = RequiredLong(root, "size_bytes");
		if (sizeBytes <= 0 || sizeBytes > MaxPackageSizeBytes)
		{
			throw new InvalidOperationException($"更新包大小 ({sizeBytes} 字节) 超过允许上限 ({MaxPackageSizeBytes} 字节)");
		}

		string archiveType = OptionalString(root, "archive_type") ?? (packageName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? "tar.gz" : "zip");
		if (archiveType is not ("zip" or "tar.gz") || !packageName.EndsWith("." + archiveType, StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("更新包扩展名与归档格式不一致或不受支持");
		string entrypoint = RequiredString(root, "entrypoint");
		if (Path.IsPathRooted(entrypoint) || entrypoint.Contains('\\') || entrypoint.Contains(':') || entrypoint.Any(char.IsControl)
			|| entrypoint.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
		{
			throw new InvalidOperationException($"更新清单中的入口文件名无效: {entrypoint}");
		}

		int launcherProtocol = OptionalInt(root, "launcher_protocol") ?? 1;
		if (launcherProtocol != 1)
		{
			throw new InvalidOperationException($"不支持的启动器交互协议版本: {launcherProtocol}");
		}

		DateTimeOffset? publishedAt = null;
		if (root.TryGetProperty("published_at", out JsonElement pubElement) && pubElement.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(pubElement.GetString(), out DateTimeOffset parsedDate))
		{
			publishedAt = parsedDate;
		}

		string? releaseNotes = OptionalString(root, "release_notes");

		return new UpdateManifest(
			schema,
			product,
			numeric,
			revision,
			rid,
			releaseTag,
			packageName,
			downloadUrl,
			sha256,
			sizeBytes,
			archiveType,
			entrypoint,
			launcherProtocol,
			publishedAt,
			releaseNotes);
	}

	/// <summary>验证下载主机是否在受信任的 GitHub 资产白名单中。</summary>
	public static bool IsAllowedHost(string host) =>
		string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

	/// <summary>比较目标版本是否严格高于当前版本（强制拒绝平级和降级）。</summary>
	public static bool IsStrictlyNewer(string currentNumeric, int currentRevision, string targetNumeric, int targetRevision)
	{
		if (!Version.TryParse(currentNumeric, out Version? current) || !Version.TryParse(targetNumeric, out Version? target))
		{
			return false;
		}

		int cmp = target.CompareTo(current);
		if (cmp > 0) return true;
		if (cmp == 0 && targetRevision > currentRevision) return true;
		return false;
	}

	private static string RequiredString(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
			? value.GetString()!
			: throw new InvalidOperationException($"更新清单缺少字段: {name}");

	private static int RequiredInt(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
			? result
			: throw new InvalidOperationException($"更新清单缺少整数字段: {name}");

	private static long RequiredLong(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result)
			? result
			: throw new InvalidOperationException($"更新清单缺少长整数字段: {name}");

	private static int? OptionalInt(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
			? result
			: null;

	private static string? OptionalString(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
