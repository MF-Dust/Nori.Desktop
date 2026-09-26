using System.Text.Json;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Resources;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Bridge;

public sealed partial class BridgeCommands
{
	private static bool IsNetworkCommand(string command, JsonElement args)
	{
		if (command is
			"llm_fetch_models"
			or "ai_test_connection"
			or "chat_start"
			or "memory_reembed_all"
			or "memory_recall_debug"
			or "memory_knowledge_reindex"
			or "skills_install_url"
			or "mcp_get_servers"
			or "mcp_connect_server"
			or "mcp_test_server"
			or "mcp_call_tool"
			or "mcp_import_url"
			or "updater_check"
			or "updater_install"
			or "tts_test"
			or "stt_start"
			or "stt_stop"
			or "open_url") return true;

		// 安全模式仍允许手动保存不自动连接的 MCP 配置，但不能借此触发自动连接。
		if (command == "mcp_save_server"
			&& args.ValueKind == JsonValueKind.Object
			&& OptionalBool(args, "enabled") == true
			&& OptionalBool(args, "autoConnect") == true) return true;
		return false;
	}

	/// <summary>main 或已通过领域白名单校验的原生窗口校验 (无返回值场景)</summary>
	private static void RequireMainVoid(IBridgeSource source)
	{
		if (source is not INativeSettingsSource and not INativeMemorySource and not INativeModelSource and not INativeChatSource
			&& source.Label != WindowLabels.Main)
		{
			throw new InvalidOperationException($"命令只能由 {WindowLabels.Main} 窗口调用");
		}
	}

	/// <summary>在 UI 线程读取可见性后校验来源; Avalonia 的 Window.IsVisible 只能在 UI 线程访问。</summary>
	private async Task<object?> RequireVisibleMainAsync(IBridgeSource source, Func<object?> factory)
	{
		RequireMainVoid(source);
		bool visible = await OnUi(() => (object?)source.IsVisible) is true;
		if (!visible) throw new InvalidOperationException("main 窗口不可见");
		return factory();
	}

	private async Task RequireVisibleMainVoidAsync(IBridgeSource source)
	{
		RequireMainVoid(source);
		bool visible = await OnUi(() => (object?)source.IsVisible) is true;
		if (!visible) throw new InvalidOperationException("main 窗口不可见");
	}

	private bool RespondApproval(IBridgeSource source, JsonElement args)
	{
		bool approved = OptionalBool(args, "approved") ?? false;
		if (source is INativeChatSource && approved && !source.IsVisible)
			throw new InvalidOperationException("对话窗口不可见，无法批准工具执行");
		return Runtime.RespondApproval(source, Str(args, "requestId"), approved);
	}

	// ===================================================================
	// 授权辅助
	// ===================================================================

	/// <summary>
	/// 校验来源窗口后执行工厂函数
	/// </summary>
	private static object? RequireLabel(IBridgeSource source, string allowed, Func<object?> factory)
	{
		if (!IsLabelAllowed(source, allowed))
		{
			throw new InvalidOperationException($"命令只能由 {allowed} 窗口调用");
		}
		return factory();
	}

	private static object? RequireLabel(IBridgeSource source, string allowedA, string allowedB, Func<object?> factory)
	{
		if (!IsLabelAllowed(source, allowedA) && !IsLabelAllowed(source, allowedB))
		{
			throw new InvalidOperationException($"命令只能由 {allowedA}/{allowedB} 窗口调用");
		}
		return factory();
	}

	private static object? RequireMain(IBridgeSource source, Func<object?> factory) => RequireLabel(source, WindowLabels.Main, factory);

	private static bool IsLabelAllowed(IBridgeSource source, string allowed) =>
		source is INativeSettingsSource
		|| ((source is INativeMemorySource or INativeModelSource or INativeChatSource) && allowed == WindowLabels.Main)
		|| source.Label == allowed;

	private static async Task<object?> RequireMainAsync(IBridgeSource source, Func<Task<object?>> factory)
	{
		RequireLabel(source, WindowLabels.Main, () => (object?)null);
		return await factory();
	}

	/// <summary>校验已知模型 ID 且确认本地模型资源已安装。</summary>
	private string RequireKnownInstalledModel(string value)
	{
		string modelId = SupportedModelIds.Normalize(value)
			?? throw new InvalidOperationException("只支持 arg-nori 或 nori 模型");
		if (!IsKnownInstalledModel(modelId)) throw new InvalidOperationException($"模型尚未安装: {modelId}");
		return modelId;
	}

	private bool IsKnownInstalledModel(string modelId)
	{
		try
		{
			return SupportedModelIds.Normalize(modelId) is not null
				&& _services.Resources.IsInstalled(ResourceType.Live2D, modelId);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ResourceException)
		{
			_services.Logger.Write(LogSource.Backend, "warn", $"检查模型资源失败 [{modelId}]: {exception.GetType().Name}");
			return false;
		}
	}
}
