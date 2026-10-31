using System.Text.Json;
using Nori.Core.Resources;

namespace Nori.Core.Live2D;

/// <summary>
/// model3.json 文件引用安全校验.
///
/// 模型定义中的所有可加载路径都必须是模型 JSON 所在目录下的普通相对文件;
/// 绝对路径、UNC、盘符、..、符号链接与 containment escape 一律拒绝.
/// </summary>
public static class Model3ReferenceValidator
{
	/// <summary>
	/// 校验一个 model3.json 及其文件引用.
	///
	/// 未声明的可选字段保持可选; 一旦声明, 引用文件必须存在且通过路径安全检查.
	/// </summary>
	public static void Validate(string modelDir, string model3Path, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string canonicalModelDir = ResourcePathSafety.FullPath(modelDir);
		string canonicalModelPath = ResourcePathSafety.FullPath(model3Path);
		if (!Directory.Exists(canonicalModelDir)) throw new ResourceException($"模型目录不存在: {modelDir}");
		ResourcePathSafety.EnsureNoReparsePointsAlongPath(canonicalModelDir, "模型目录包含符号链接或 reparse point");
		ResourcePathSafety.EnsureNoReparsePoints(canonicalModelDir, canonicalModelDir, "模型目录包含符号链接或 reparse point");
		ResourcePathSafety.EnsureContained(canonicalModelDir, canonicalModelPath, $"model3.json 超出模型目录: {model3Path}");
		ResourcePathSafety.EnsureNoReparsePoints(canonicalModelDir, canonicalModelPath, "model3.json 路径包含符号链接或 reparse point");
		if (!File.Exists(canonicalModelPath)) throw new ResourceException($"model3.json 不存在: {model3Path}");

		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(File.ReadAllText(canonicalModelPath));
		}
		catch (JsonException exception)
		{
			throw new ResourceException($"model3.json 格式无效: {Path.GetFileName(model3Path)}", exception);
		}

		using (document)
		{
			cancellationToken.ThrowIfCancellationRequested();
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				throw new ResourceException($"model3.json 根节点无效: {Path.GetFileName(model3Path)}");
			}
			if (!root.TryGetProperty("FileReferences", out JsonElement references)
				|| references.ValueKind != JsonValueKind.Object)
			{
				throw new ResourceException("model3.json 缺少有效的 FileReferences 对象");
			}

			string referenceDir = Path.GetDirectoryName(canonicalModelPath) ?? canonicalModelDir;
			ValidatePathProperty(references, "Moc", referenceDir, canonicalModelPath, cancellationToken, required: true);
			ValidateTextures(references, referenceDir, canonicalModelPath, cancellationToken);
			ValidatePathProperty(references, "Physics", referenceDir, canonicalModelPath, cancellationToken, required: false);
			ValidatePathProperty(references, "Pose", referenceDir, canonicalModelPath, cancellationToken, required: false);
			ValidatePathProperty(references, "DisplayInfo", referenceDir, canonicalModelPath, cancellationToken, required: false);
			ValidatePathProperty(references, "UserData", referenceDir, canonicalModelPath, cancellationToken, required: false);
			ValidateMotions(references, referenceDir, canonicalModelPath, cancellationToken);
			ValidateExpressions(references, referenceDir, canonicalModelPath, cancellationToken);
		}
	}

	/// <summary>
	/// 解析并校验一个相对于 model3.json 所在目录的引用, 返回已 containment 校验的绝对路径.
	/// </summary>
	public static string ResolveReferencePath(string model3Path, string reference) =>
		ResolveReferencePath(model3Path, reference, requireFile: true);

	/// <summary>
	/// 同上; <paramref name="requireFile"/> 为 false 时允许目标尚不存在 (远程下载前预检).
	/// </summary>
	internal static string ResolveReferencePath(string model3Path, string reference, bool requireFile)
	{
		string canonicalModelPath = ResourcePathSafety.FullPath(model3Path);
		string referenceDir = Path.GetDirectoryName(canonicalModelPath)
			?? throw new ResourceException($"model3.json 没有父目录: {model3Path}");
		return ResolveReferencePath(referenceDir, canonicalModelPath, reference, "模型引用", requireFile);
	}

	private static void ValidateTextures(
		JsonElement references,
		string referenceDir,
		string model3Path,
		CancellationToken cancellationToken)
	{
		if (!references.TryGetProperty("Textures", out JsonElement textures)) return;
		if (textures.ValueKind != JsonValueKind.Array)
		{
			throw new ResourceException("model3.json 的 Textures 必须是数组");
		}
		int index = 0;
		foreach (JsonElement texture in textures.EnumerateArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			string value = RequiredString(texture, $"Textures[{index}]");
			ResolveReferencePath(referenceDir, model3Path, value, $"Textures[{index}]");
			index++;
		}
	}

	private static void ValidateMotions(
		JsonElement references,
		string referenceDir,
		string model3Path,
		CancellationToken cancellationToken)
	{
		if (!references.TryGetProperty("Motions", out JsonElement motions)) return;
		if (motions.ValueKind != JsonValueKind.Object)
		{
			throw new ResourceException("model3.json 的 Motions 必须是对象");
		}

		foreach (JsonProperty group in motions.EnumerateObject())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (group.Value.ValueKind != JsonValueKind.Array)
			{
				throw new ResourceException($"model3.json 的动作组无效: {group.Name}");
			}
			int index = 0;
			foreach (JsonElement motion in group.Value.EnumerateArray())
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (motion.ValueKind != JsonValueKind.Object)
				{
					throw new ResourceException($"model3.json 的动作条目无效: {group.Name}[{index}]");
				}
				string file = RequiredPropertyString(motion, "File", $"Motions.{group.Name}[{index}]");
				ResolveReferencePath(referenceDir, model3Path, file, $"Motions.{group.Name}[{index}].File");
				if (motion.TryGetProperty("Sound", out JsonElement sound) && sound.ValueKind != JsonValueKind.Null)
				{
					// Sound/Voice 由宿主外部音频管线处理, SDK 不读取也不播放; 只拒绝危险路径.
					string soundPath = RequiredString(sound, $"Motions.{group.Name}[{index}].Sound");
					ResolveReferencePath(referenceDir, model3Path, soundPath, $"Motions.{group.Name}[{index}].Sound", requireFile: false);
				}
				index++;
			}
		}
	}

	private static void ValidateExpressions(
		JsonElement references,
		string referenceDir,
		string model3Path,
		CancellationToken cancellationToken)
	{
		if (!references.TryGetProperty("Expressions", out JsonElement expressions)) return;
		if (expressions.ValueKind != JsonValueKind.Array)
		{
			throw new ResourceException("model3.json 的 Expressions 必须是数组");
		}

		int index = 0;
		foreach (JsonElement expression in expressions.EnumerateArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (expression.ValueKind != JsonValueKind.Object)
			{
				throw new ResourceException($"model3.json 的表情条目无效: Expressions[{index}]");
			}
			RequiredPropertyString(expression, "Name", $"Expressions[{index}]");
			string file = RequiredPropertyString(expression, "File", $"Expressions[{index}]");
			ResolveReferencePath(referenceDir, model3Path, file, $"Expressions[{index}].File");
			index++;
		}
	}

	private static void ValidatePathProperty(
		JsonElement references,
		string property,
		string referenceDir,
		string model3Path,
		CancellationToken cancellationToken,
		bool required)
	{
		if (!references.TryGetProperty(property, out JsonElement value))
		{
			if (required) throw new ResourceException($"model3.json 缺少 FileReferences.{property}");
			return;
		}
		if (value.ValueKind == JsonValueKind.Null && !required) return;
		string reference = RequiredString(value, $"FileReferences.{property}");
		string resolved = ResolveReferencePath(referenceDir, model3Path, reference, $"FileReferences.{property}");
		if (property == "Moc") ValidateMoc3(resolved);
	}

	private static void ValidateMoc3(string path)
	{
		Span<byte> header = stackalloc byte[4];
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			if (stream.Read(header) != header.Length || !header.SequenceEqual("MOC3"u8))
				throw new ResourceException("Moc 文件头无效, 不是可识别的 Cubism .moc3 文件");
		}
		catch (ResourceException)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new ResourceException("无法读取 Cubism .moc3 文件", exception);
		}
	}

	private static string RequiredPropertyString(JsonElement parent, string property, string label)
	{
		if (!parent.TryGetProperty(property, out JsonElement value))
		{
			throw new ResourceException($"model3.json 缺少 {label}.{property}");
		}
		return RequiredString(value, $"{label}.{property}");
	}

	private static string RequiredString(JsonElement value, string label)
	{
		if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
		{
			throw new ResourceException($"model3.json 引用无效: {label}");
		}
		return value.GetString()!;
	}

	private static string ResolveReferencePath(
		string referenceDir,
		string model3Path,
		string reference,
		string label,
		bool requireFile = true)
	{
		string normalized = NormalizeRelativePath(reference, label);
		string candidate;
		try
		{
			candidate = Path.GetFullPath(Path.Combine(referenceDir, normalized.Replace('/', Path.DirectorySeparatorChar)));
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			throw new ResourceException($"model3.json 引用路径无效: {label}", exception);
		}

		ResourcePathSafety.EnsureContained(referenceDir, candidate, $"model3.json 引用超出模型目录: {label}");
		ResourcePathSafety.EnsureNoReparsePoints(referenceDir, candidate, $"model3.json 引用包含符号链接或 reparse point: {label}");
		if (requireFile && (!File.Exists(candidate) || Directory.Exists(candidate)))
		{
			throw new ResourceException($"model3.json 引用文件不存在: {label} -> {reference}");
		}
		return candidate;
	}

	private static string NormalizeRelativePath(string reference, string label)
	{
		if (string.IsNullOrWhiteSpace(reference)) throw new ResourceException($"model3.json 引用不能为空: {label}");
		string normalized = reference.Replace('\\', '/');
		if (normalized.StartsWith("//", StringComparison.Ordinal))
		{
			throw new ResourceException($"model3.json 引用不能是 UNC 路径: {label}");
		}
		if (normalized.StartsWith('/'))
		{
			throw new ResourceException($"model3.json 引用不能是绝对路径: {label}");
		}
		if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
		{
			throw new ResourceException($"model3.json 引用不能包含盘符: {label}");
		}
		foreach (string segment in normalized.Split('/'))
		{
			if (segment == "..") throw new ResourceException($"model3.json 引用包含路径穿越: {label}");
			if (segment.Any(char.IsControl)) throw new ResourceException($"model3.json 引用包含非法字符: {label}");
		}
		return normalized;
	}
}
