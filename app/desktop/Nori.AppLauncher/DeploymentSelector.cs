using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nori.AppLauncher;

/// <summary>槽内 deployment.json 的最小可信元数据。</summary>
public sealed record DeploymentManifest(
	int SchemaVersion,
	string ProductVersion,
	string NumericVersion,
	int Revision,
	string Rid,
	string Entrypoint);

/// <summary>经过目录、manifest、入口与 RID 校验的部署槽。</summary>
public sealed record DeploymentSelection(string PackageRoot, string DeploymentRoot, string Entrypoint, DeploymentManifest Manifest);

/// <summary>安全选择当前发布槽，不执行更新、不删除槽、不持有单实例锁。</summary>
public static class DeploymentSelector
{
	private static readonly Regex SlotPattern = new("^app-(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)-(?<revision>[0-9]+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

	public static DeploymentSelection Select(string packageRoot, string expectedRid)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedRid);
		string root = Path.GetFullPath(packageRoot);
		if (!Directory.Exists(root) || IsReparse(root)) throw new InvalidOperationException($"发布包根目录不存在或无效: {root}");
		List<DeploymentSelection> candidates = [];
		string? currentName = ReadCurrent(root);
		foreach (string directory in Directory.EnumerateDirectories(root))
		{
			DirectoryInfo info = new(directory);
			if (info.Name.EndsWith(".partial", StringComparison.Ordinal) || info.Name.EndsWith(".destroy", StringComparison.Ordinal)) continue;
			Match match = SlotPattern.Match(info.Name);
			if (!match.Success || IsReparse(directory)) continue;
			try
			{
				DeploymentManifest manifest = ReadManifest(directory);
				if (!manifest.NumericVersion.Equals(match.Groups["version"].Value, StringComparison.Ordinal)
					|| manifest.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) != match.Groups["revision"].Value
					|| !manifest.Rid.Equals(expectedRid, StringComparison.Ordinal)) continue;
				string entrypoint = ResolveEntrypoint(directory, manifest.Entrypoint);
				candidates.Add(new DeploymentSelection(root, directory, entrypoint, manifest));
			}
			catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
			{
				// 一个损坏或其它 RID 的槽不应遮蔽可用的旧槽。
			}
		}
		if (candidates.Count == 0) throw new InvalidOperationException($"没有找到匹配 {expectedRid} 的可用 Nori 部署槽");
		return candidates
			.OrderByDescending(item => string.Equals(Path.GetFileName(item.DeploymentRoot), currentName, StringComparison.Ordinal))
			.ThenByDescending(item => ParseVersion(item.Manifest.NumericVersion), VersionComparer.Instance)
			.ThenByDescending(item => item.Manifest.Revision)
			.ThenBy(item => Path.GetFileName(item.DeploymentRoot), StringComparer.Ordinal)
			.First();
	}

	public static DeploymentManifest ReadManifest(string deploymentRoot)
	{
		string path = Path.Combine(deploymentRoot, "deployment.json"); // NOSONAR: deploymentRoot 来自已解析的应用槽目录，后续 IsReparse 校验并限制固定文件名。
		if (!File.Exists(path) || IsReparse(path)) throw new InvalidOperationException($"部署 manifest 不存在或无效: {path}");
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
		JsonElement root = document.RootElement;
		int schema = RequiredInt(root, "schema_version");
		string product = RequiredString(root, "product_version");
		string numeric = RequiredString(root, "numeric_version");
		int revision = RequiredInt(root, "revision");
		string rid = RequiredString(root, "rid");
		string entrypoint = RequiredString(root, "entrypoint");
		if (schema != 1 || !IsSupportedNumericVersion(numeric)
			|| revision < 0 || ContainsControl(product) || ContainsControl(numeric) || ContainsControl(rid) || ContainsControl(entrypoint)
			|| Path.IsPathRooted(entrypoint) || entrypoint.Contains('\\') || entrypoint.Split('/').Any(part => part is "" or "." or ".."))
			throw new InvalidOperationException($"部署 manifest 字段无效: {path}");
		return new DeploymentManifest(schema, product, numeric, revision, rid, entrypoint);
	}

	private static string ResolveEntrypoint(string deploymentRoot, string relative)
	{
		string root = Path.GetFullPath(deploymentRoot);
		string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
		if (!IsContained(path, root) || !File.Exists(path) || IsReparse(path)) throw new InvalidOperationException($"部署入口无效: {relative}");
		for (string? current = Path.GetDirectoryName(path); current is not null && IsContained(current, root); current = Path.GetDirectoryName(current))
			if (Directory.Exists(current) && IsReparse(current)) throw new InvalidOperationException($"部署入口路径包含 reparse point: {current}");
		return path;
	}

	private static string? ReadCurrent(string root)
	{
		string path = Path.Combine(root, ".current");
		try
		{
			if (!File.Exists(path) || IsReparse(path)) return null;
			string value = File.ReadAllText(path).Trim();
			return value.Length > 0 && value.Length <= 128 && !value.Any(char.IsControl) && !value.Contains('/') && !value.Contains('\\') ? value : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return null;
		}
	}

	private static bool ContainsControl(string value) => value.Any(char.IsControl);

	private static bool IsSupportedNumericVersion(string value) =>
		Regex.IsMatch(value, "^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)
		&& value.Split('.').All(segment => ushort.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _));

	private static bool IsContained(string path, string root)
	{
		StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(fullPath, fullRoot, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
	}

	private static bool IsReparse(string path)
	{
		try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
		catch (IOException) { return true; }
		catch (UnauthorizedAccessException) { return true; }
		catch (ArgumentException) { return true; }
	}
	private static string RequiredString(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidOperationException($"manifest 缺少 {name}");
	private static int RequiredInt(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : throw new InvalidOperationException($"manifest 缺少 {name}");
	private static Version ParseVersion(string value) => Version.Parse(value);

	private sealed class VersionComparer : IComparer<Version>
	{
		public static VersionComparer Instance { get; } = new();
		public int Compare(Version? x, Version? y) => (x ?? new Version()).CompareTo(y ?? new Version());
	}
}
