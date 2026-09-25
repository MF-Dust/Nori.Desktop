namespace Nori.Desktop.Settings;

/// <summary>将宿主异常转换为原生设置页当前语言的用户文案。</summary>
internal static class SettingsErrorText
{
	private static readonly IReadOnlyDictionary<string, string> EnglishMessages = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		["应用运行时尚未就绪"] = "The application runtime is not ready.",
		["插件运行时尚未就绪"] = "The plugin runtime is not ready.",
		["应用关闭中"] = "The application is shutting down.",
		["安全模式下不能运行插件。"] = "Plugins cannot run in safe mode.",
		["向量维度必须为正整数，或留空使用默认值。"] = "Dimensions must be a positive integer, or empty for the model default.",
		["请先填写接口地址。"] = "Enter an endpoint URL first.",
		["没有找到可用模型。"] = "No available models were found.",
		["设置保存失败，请检查输入后重试。"] = "Settings could not be saved. Check the input and retry.",
	};

	/// <summary>按设置页语言读取异常文本；英文页不泄漏未翻译的中文后端消息。</summary>
	public static string Resolve(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return Resolve(Unwrap(exception).Message);
	}

	/// <summary>按设置页语言读取一段异常或状态文本。</summary>
	public static string Resolve(string? message)
	{
		message = message?.Trim() ?? string.Empty;
		if (!SettingsLocalization.IsEnglish || message.Length == 0) return message;
		if (EnglishMessages.TryGetValue(message, out string? translated)) return translated;
		if (!ContainsChinese(message)) return message;
		if (message.Contains("尚未就绪", StringComparison.Ordinal)) return "The application is not ready.";
		if (message.Contains("超时", StringComparison.Ordinal)) return "The operation timed out.";
		if (message.Contains("不能为空", StringComparison.Ordinal) || message.Contains("必须", StringComparison.Ordinal))
			return "The provided value is invalid.";
		if (message.Contains("不存在", StringComparison.Ordinal) || message.Contains("未找到", StringComparison.Ordinal))
			return "The requested item was not found.";
		return "The operation failed. Check the settings and try again.";
	}

	private static Exception Unwrap(Exception exception)
	{
		while (exception is AggregateException {InnerExceptions.Count: 1} aggregate)
			exception = aggregate.InnerExceptions[0];
		return exception;
	}

	private static bool ContainsChinese(string value)
	{
		foreach (char character in value)
		{
			if (character is >= '\u3400' and <= '\u9fff') return true;
		}
		return false;
	}
}
