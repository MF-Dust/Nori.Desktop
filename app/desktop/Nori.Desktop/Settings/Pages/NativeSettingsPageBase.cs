using Nori.Desktop.Settings;

namespace Nori.Desktop.Settings.Pages;

/// <summary>复杂原生设置页的宿主适配层，将列表型 ViewModel 接入统一设置导航。</summary>
public abstract class NativeSettingsPageBase : SettingsPageBase
{
	/// <summary>创建复杂设置页。</summary>
	protected NativeSettingsPageBase(
		SettingsService service,
		string key,
		string groupKey,
		SettingsText title,
		SettingsText description,
		SettingsPageViewModelBase viewModel,
		CancellationToken lifetimeToken)
		: base(service, key, groupKey, title, description, lifetimeToken)
	{
		ComplexViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
	}

	/// <summary>本页使用的列表型 ViewModel。</summary>
	public SettingsPageViewModelBase ComplexViewModel { get; }

	/// <summary>刷新复杂页面数据；错误保留在页面上，不阻断其它设置页。</summary>
	public async Task RefreshComplexAsync(CancellationToken cancellationToken = default)
	{
		ComplexViewModel.ClearError();
		try
		{
			await ComplexViewModel.RefreshAsync(cancellationToken).ConfigureAwait(true);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			ComplexViewModel.ReportError(exception);
		}
	}

	/// <summary>等待复杂页面尚未完成的操作。</summary>
	public Task FlushComplexAsync(CancellationToken cancellationToken = default) =>
		ComplexViewModel.FlushAsync(cancellationToken);
}

/// <summary>技能工坊页面。</summary>
public sealed class SkillsSettingsPage : NativeSettingsPageBase
{
	/// <summary>创建技能工坊页面。</summary>
	public SkillsSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "skills", "extend", new("技能工坊", "Skills Workshop"), new("管理已安装技能、市场预设与自定义技能。", "Manage installed, marketplace and custom skills."), new SkillsSettingsViewModel(service), lifetimeToken)
	{
	}
}

/// <summary>MCP 与工具页面。</summary>
public sealed class McpSettingsPage : NativeSettingsPageBase
{
	/// <summary>创建 MCP 与工具页面。</summary>
	public McpSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "mcp", "extend", new("MCP 与工具", "MCP & Tools"), new("管理 MCP 服务与内置工具，所有调用均由宿主执行。", "Manage MCP servers and built-in tools; calls run in the host."), new McpSettingsViewModel(service), lifetimeToken)
	{
	}
}

/// <summary>自动化权限与任务页面。</summary>
public sealed class AutomationSettingsPage : NativeSettingsPageBase
{
	/// <summary>创建自动化页面。</summary>
	public AutomationSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "automation", "perception", new("自动化", "Automation"), new("管理桌面、浏览器自动化权限和任务生命周期。", "Manage desktop and browser automation permissions and task lifecycles."), new AutomationSettingsViewModel(service), lifetimeToken)
	{
	}
}

/// <summary>插件管理页面。</summary>
public sealed class PluginsSettingsPage : NativeSettingsPageBase
{
	/// <summary>创建插件管理页面。</summary>
	public PluginsSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "plugins", "extend", new("插件", "Plugins"), new("安装、启停插件并管理插件数据。", "Install, control and remove plugins and their data."), new PluginsSettingsViewModel(service), lifetimeToken)
	{
	}
}

/// <summary>调试与诊断页面。</summary>
public sealed class DebugSettingsPage : NativeSettingsPageBase
{
	/// <summary>创建调试与诊断页面。</summary>
	public DebugSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "debug", "system", new("调试与诊断", "Debug & Diagnostics"), new("查看日志、导出脱敏诊断并验证异常处理入口。", "Inspect logs, export redacted diagnostics and exercise failure handling."), new DebugSettingsViewModel(service), lifetimeToken)
	{
	}
}
