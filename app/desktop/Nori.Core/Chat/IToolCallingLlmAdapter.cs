using System.Text.Json.Nodes;
using Nori.Core.Tools;

namespace Nori.Core.Chat;

/// <summary>可使用 provider 原生函数调用的 LLM 适配器。</summary>
public interface IToolCallingLlmAdapter
{
	Task<string> StreamWithToolsAsync(
		string baseUrl,
		string apiKey,
		string model,
		string systemPrompt,
		IReadOnlyList<ChatMessageInput> messages,
		IReadOnlyList<RegisteredTool> tools,
		Func<string, JsonNode?, Task<ToolResult>> executeTool,
		Action<string> onChunk,
		Action<LlmUsageInfo>? onUsage = null,
		CancellationToken cancellationToken = default);
}

/// <summary>可以按次调用替换生成参数 (思考档位、模型能力) 的适配器。</summary>
public interface IConfigurableLlmAdapter
{
	/// <summary>返回使用指定生成参数的新适配器；原适配器不变。</summary>
	ILlmAdapter WithGeneration(LlmGenerationOptions options);
}
