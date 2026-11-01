using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nori.Core.Live2D;

namespace Nori.Desktop.Models;

/// <summary>
/// 内置形象的显示名与立绘。
///
/// 模型窗口与首次运行向导共用这一份：此前向导直接把 <c>arg-nori</c> 这种内部 ID
/// 摆在卡片上，用户看不出选的是谁。
/// </summary>
internal static class BuiltInModels
{
	/// <summary>顺序与 <see cref="SupportedModelIds.All"/> 一致。</summary>
	internal static readonly (string Id, string Name, string Image)[] All =
	[
		(SupportedModelIds.ArgNori, "ARG Nori", "ARGNori.webp"),
		(SupportedModelIds.Nori, "Nori", "Nori.webp"),
	];

	/// <summary>显示名；不认识的 ID 原样返回。</summary>
	internal static string Name(string id) => All.FirstOrDefault(model => model.Id == id).Name ?? id;

	/// <summary>
	/// 打开随程序打包的立绘。调用方负责释放。
	///
	/// 取不到返回 null —— 少一张图不该让调用方的界面出不来。
	/// </summary>
	internal static Bitmap? OpenThumbnail(string id)
	{
		string? image = All.FirstOrDefault(model => model.Id == id).Image;
		if (image is null) return null;
		try
		{
			using Stream stream = AssetLoader.Open(new Uri("avares://Nori.Desktop/Assets/Models/" + image));
			return new Bitmap(stream);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
		{
			return null;
		}
	}
}
