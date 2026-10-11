using System.Globalization;
using Nori.Core.Chat.Catalog;

namespace Nori.Core.Agent;

/// <summary>一次对话可用的上下文规模。</summary>
/// <param name="MaxInputTokens">交给预算器的输入上限 (含为输出保留的部分)。</param>
/// <param name="ReservedOutputTokens">为模型输出保留的 token。</param>
/// <param name="ModelWindow">模型输入窗口；0 表示未知。</param>
/// <param name="Source">数值来源：config (用户指定) / model (models.dev) / default (未知模型的保守值)。</param>
public sealed record ContextWindowPlan(int MaxInputTokens, int ReservedOutputTokens, int ModelWindow, string Source);

/// <summary>
/// 把「上下文上限」设置与模型能力换算为预算参数。
///
/// 设置取值：<c>auto</c> (默认，按模型窗口但不超过 <see cref="AutoCap"/>) / <c>full</c> (模型完整窗口) /
/// 正整数 (显式 token 数，仍不超过已知的模型窗口)。上限的存在是因为每轮都会把整段上下文发出去：
/// 百万窗口的模型在「自动」下用满窗口，长对话每句话都要付几十万 token 的钱。
/// </summary>
public static class ContextWindowPlanner
{
	/// <summary>设置键：上下文上限。</summary>
	public const string KeyContextLimit = "agent_context_tokens";

	/// <summary>设置键：为输出保留的 token。</summary>
	public const string KeyReservedOutput = "agent_reserved_output_tokens";

	/// <summary>未知模型的保守窗口。</summary>
	public const int DefaultWindow = 32_000;

	/// <summary>「自动」档位的上限。</summary>
	public const int AutoCap = 128_000;

	/// <summary>预算允许的最大窗口。</summary>
	public const int MaxWindow = 2_000_000;

	/// <summary>最小可用窗口。</summary>
	public const int MinWindow = 2_048;

	/// <summary>按设置与模型能力计算上下文规模。</summary>
	public static ContextWindowPlan Resolve(string? limitSetting, string? reservedSetting, ModelCapabilities? capabilities)
	{
		int modelWindow = capabilities?.EffectiveInputWindow is > 0 and var known ? Math.Min(known, MaxWindow) : 0;
		int window = modelWindow > 0 ? modelWindow : DefaultWindow;
		string setting = limitSetting?.Trim().ToLowerInvariant() ?? "";

		int limit;
		string source;
		if (TryParseTokens(setting, out int explicitTokens))
		{
			limit = modelWindow > 0 ? Math.Min(explicitTokens, modelWindow) : explicitTokens;
			source = "config";
		}
		else if (setting == "full")
		{
			limit = window;
			source = modelWindow > 0 ? "model" : "default";
		}
		else
		{
			limit = Math.Min(window, AutoCap);
			source = modelWindow > 0 ? "model" : "default";
		}
		limit = Math.Clamp(limit, MinWindow, MaxWindow);

		int reserved;
		if (TryParseTokens(reservedSetting?.Trim() ?? "", out int explicitReserved))
		{
			reserved = explicitReserved;
		}
		else
		{
			int outputCap = capabilities?.OutputLimit is > 0 and var output ? output : 4_096;
			reserved = Math.Min(outputCap, Math.Max(2_048, limit / 16));
			reserved = Math.Clamp(reserved, 1_024, 32_000);
		}
		// 输出预留不能吃掉大半个窗口，否则历史与系统提示词无处可放。
		reserved = Math.Clamp(reserved, 128, Math.Max(128, limit / 2));
		return new ContextWindowPlan(limit, reserved, modelWindow, source);
	}

	/// <summary>由窗口规模派生预算参数：系统提示词约占三成，其余留给历史。</summary>
	public static ContextBudgetOptions ToBudget(ContextWindowPlan plan)
	{
		int input = Math.Max(0, plan.MaxInputTokens - plan.ReservedOutputTokens);
		return new ContextBudgetOptions
		{
			MaxInputTokens = plan.MaxInputTokens,
			ReservedOutputTokens = plan.ReservedOutputTokens,
			MaxSystemTokens = Math.Clamp(input * 3 / 10, Math.Min(7_000, input), 48_000),
			MaxHistoryTokens = input,
		};
	}

	/// <summary>解析 token 数；支持 32000、32k、1m 等写法。</summary>
	public static bool TryParseTokens(string value, out int tokens)
	{
		tokens = 0;
		string text = value.Trim().ToLowerInvariant();
		if (text.Length == 0) return false;
		long multiplier = 1;
		if (text.EndsWith('k')) { multiplier = 1_000; text = text[..^1]; }
		else if (text.EndsWith('m')) { multiplier = 1_000_000; text = text[..^1]; }
		if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number <= 0) return false;
		double total = number * multiplier;
		if (total > int.MaxValue) return false;
		tokens = (int)total;
		return tokens > 0;
	}
}
