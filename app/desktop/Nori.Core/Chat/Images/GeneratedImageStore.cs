using System.Text.RegularExpressions;

namespace Nori.Core.Chat.Images;

/// <summary>
/// 生成图片的本地存放与引用格式。
///
/// 图片保存在包内数据目录，消息正文里只写 <c>![说明](nori-image:文件名)</c> 引用 ——
/// 聊天渲染只认这个前缀，并且只从本目录按文件名读取，不加载任意路径或网络地址。
/// </summary>
public sealed partial class GeneratedImageStore
{
	/// <summary>消息正文中的图片引用前缀。</summary>
	public const string Scheme = "nori-image:";

	/// <summary>单张图片的大小上限。</summary>
	public const int MaxImageBytes = 25 * 1024 * 1024;

	private readonly string _directory;

	/// <summary>创建存储；目录在首次保存时创建。</summary>
	public GeneratedImageStore(string directory)
	{
		_directory = string.IsNullOrWhiteSpace(directory)
			? throw new ArgumentException("图片目录不能为空", nameof(directory))
			: Path.GetFullPath(directory);
	}

	/// <summary>图片目录。</summary>
	public string Directory => _directory;

	/// <summary>文件名是否为本存储生成的合法名字 (无路径、无上级目录、限定扩展名)。</summary>
	public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

	/// <summary>生成引用图片的 Markdown。</summary>
	public static string ToMarkdown(string name, string? alt)
	{
		string label = (alt ?? "生成的图片").Replace('[', '(').Replace(']', ')').Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (label.Length > 60) label = label[..60] + "…";
		return $"![{label}]({Scheme}{name})";
	}

	/// <summary>去掉正文中的图片引用 (朗读、气泡等纯文本场景)。</summary>
	public static string StripReferences(string text) =>
		string.IsNullOrEmpty(text) ? text : MarkdownPattern().Replace(text, "").Trim();

	/// <summary>从引用地址 (nori-image:xxx.png) 取出文件名；不是合法引用时返回 null。</summary>
	public static string? ParseReference(string? url)
	{
		if (url is null || !url.StartsWith(Scheme, StringComparison.Ordinal)) return null;
		string name = url[Scheme.Length..];
		return IsValidName(name) ? name : null;
	}

	/// <summary>保存图片并返回文件名。</summary>
	public async Task<string> SaveAsync(byte[] bytes, string? mimeType, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(bytes);
		if (bytes.Length == 0) throw new InvalidOperationException("图像服务返回了空图片");
		if (bytes.Length > MaxImageBytes) throw new InvalidOperationException("生成的图片超过大小上限");
		string extension = DetectExtension(bytes, mimeType);
		System.IO.Directory.CreateDirectory(_directory);
		string name = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24] + extension;
		string path = Path.Combine(_directory, name);
		await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
		return name;
	}

	/// <summary>按文件名解析到完整路径；文件不存在或名字不合法时返回 null。</summary>
	public string? Resolve(string name)
	{
		if (!IsValidName(name)) return null;
		string path = Path.GetFullPath(Path.Combine(_directory, name));
		if (!Data.AppStoragePaths.IsContained(path, _directory)) return null;
		return File.Exists(path) ? path : null;
	}

	/// <summary>读取图片字节；不存在时返回 null。</summary>
	public async Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default)
	{
		if (Resolve(name) is not { } path) return null;
		FileInfo info = new(path);
		if (info.Length > MaxImageBytes) return null;
		return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>按文件头判断格式，服务端声明的 MIME 只作参考。</summary>
	internal static string DetectExtension(byte[] bytes, string? mimeType)
	{
		if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
		if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
		if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
			&& bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') return ".webp";
		return mimeType?.ToLowerInvariant() switch
		{
			"image/jpeg" or "image/jpg" => ".jpg",
			"image/webp" => ".webp",
			"image/png" => ".png",
			_ => throw new InvalidOperationException("图像服务返回的数据不是支持的图片格式"),
		};
	}

	[GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,80}\.(png|jpg|webp)$")]
	private static partial Regex NamePattern();

	[GeneratedRegex(@"!\[[^\]\r\n]*\]\(nori-image:[A-Za-z0-9_.-]+\)")]
	private static partial Regex MarkdownPattern();
}
