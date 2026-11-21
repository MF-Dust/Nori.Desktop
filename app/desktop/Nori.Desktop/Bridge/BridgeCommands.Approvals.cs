using System.Text.Json;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Resources;

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
			or "mcp_connect_server"
			or "mcp_test_server"
			or "mcp_call_tool"
			or "mcp_import_url"
			or "cloud_backup"
			or "cloud_restore"
			or "updater_check"
			or "model_download_remote"
			or "updater_install"
			or "tts_test"
			or "stt_start"
			or "stt_stop"
			or "realtime_voice_start"
			or "open_url") return true;

		// 安全模式仍允许手动保存不自动连接的 MCP 配置，但不能借此触发自动连接。
		if (command == "mcp_save_server"
			&& args.ValueKind == JsonValueKind.Object
			&& OptionalBool(args, "enabled") == true
			&& OptionalBool(args, "autoConnect") == true) return true;
		return false;
	}

	private bool RespondApproval(IBridgeSource source, JsonElement args) =>
		Runtime.RespondApproval(source, Str(args, "requestId"), OptionalBool(args, "approved") ?? false);

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
			_services.Logger.Write(LogSource.Backend, "warn", $"检查模型资源失败 [{modelId}]", exception: exception);
			return false;
		}
	}
}
