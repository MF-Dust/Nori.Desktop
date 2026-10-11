using System.Text.Json.Serialization;
using Nori.Core.Chat.Images;
using static Nori.Core.Tools.ToolProperty;
using static Nori.Core.Tools.ToolRegistration;

namespace Nori.Core.Tools;

/// <summary>
/// generateImage 的执行结果。
///
/// <see cref="Markdown"/> 不交给模型复述：引擎在本轮结束时把它追加到最终回复里，图片因此
/// 一定会出现在聊天窗口，也不会因为模型改写链接而失效。
/// </summary>
public sealed record GeneratedImageToolResult(
	[property: JsonPropertyName("image")] string Image,
	[property: JsonPropertyName("prompt")] string Prompt,
	[property: JsonPropertyName("revisedPrompt")] string? RevisedPrompt,
	[property: JsonPropertyName("note")] string Note)
{
	/// <summary>引用图片的 Markdown (不序列化给模型)。</summary>
	[JsonIgnore]
	public string Markdown { get; init; } = "";
}

/// <summary>图像生成工具。</summary>
public static class ImageTools
{
	/// <summary>工具名。</summary>
	public const string GenerateImageName = "generateImage";

	/// <summary>
	/// 注册图像生成工具，注册前先注销；生成器未配置时整组不注册。
	///
	/// 权限为 confirm：每次生成都会向图像服务计费，必须由用户确认。
	/// </summary>
	public static void RegisterAll(ToolRegistry registry, IImageGenerator? generator)
	{
		ArgumentNullException.ThrowIfNull(registry);
		registry.Unregister(GenerateImageName);
		if (generator is not {IsConfigured: true}) return;

		Register(
			registry,
			GenerateImageName,
			"根据文字描述生成一张图片，生成后会自动显示在聊天窗口里。描述尽量具体 (主体、风格、构图、色调)，"
				+ "可以用用户的语言。每次调用都会请求用户确认并产生费用，不要重复调用。",
			"confirm",
			Schema(
				Text("prompt", "图像描述"),
				Text("aspectRatio", "画面比例，如 1:1、16:9、9:16、4:3、3:4；省略则使用设置中的默认值", required: false)),
			async (args, context) =>
			{
				string prompt = ReadString(args, "prompt") ?? throw new InvalidOperationException("缺少参数: prompt");
				string? ratio = ReadString(args, "aspectRatio");
				GeneratedImage image = await generator.GenerateAsync(prompt, ratio, context.CancellationToken).ConfigureAwait(false);
				return new GeneratedImageToolResult(
					image.Name,
					prompt,
					image.RevisedPrompt,
					"图片已生成，会自动附在你这轮回复的末尾显示给用户。回复里简单说明即可，不要输出图片链接或文件名。")
				{
					Markdown = image.Markdown,
				};
			});
	}

	private static string? ReadString(System.Text.Json.Nodes.JsonNode? args, string name) =>
		args?[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
			? text.Trim()
			: null;
}
