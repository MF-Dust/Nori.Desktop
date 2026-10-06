using Avalonia.Logging;
using Nori.Core.Logging;

namespace Nori.Desktop.Diagnostics;

/// <summary>
/// Avalonia 框架诊断接入统一日志，模板参数和控件内容不进入记录。
/// 框架 Information 是每次布局/渲染的过程记录，降为 debug 避免刷屏。
/// </summary>
internal sealed class AvaloniaLogSink(FileLogger logger) : ILogSink
{
	public bool IsEnabled(LogEventLevel level, string area) => logger.IsEnabled(Level(level));

	public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
		Write(level, area, source, null);

	public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
		Write(level, area, source, propertyValues.OfType<Exception>().FirstOrDefault());

	private void Write(LogEventLevel level, string area, object? source, Exception? exception)
	{
		string category = area is "Binding" or "Layout" or "Property" or "Control" or "Visual" or "Animation" or "Styling" or "Rendering" or "Platform" ? area : "Framework";
		string message = source is null ? $"Avalonia {category} 诊断事件" : $"Avalonia {category} 诊断事件：{source.GetType().Name}";
		logger.Write(LogSource.Backend, Level(level), message, "Avalonia." + category, "avalonia.diagnostic", exception);
	}

	private static string Level(LogEventLevel level) => level switch
	{
		LogEventLevel.Verbose => "trace", LogEventLevel.Debug or LogEventLevel.Information => "debug",
		LogEventLevel.Warning => "warn", LogEventLevel.Error => "error", LogEventLevel.Fatal => "fatal", _ => "info",
	};
}
