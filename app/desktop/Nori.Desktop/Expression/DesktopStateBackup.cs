using Nori.Core.Configuration;

namespace Nori.Desktop.Expression;

/// <summary>
/// 改持久系统设置之前先把原值存下来，关掉时还回去。
///
/// 存进配置库而不是内存：进程崩了、被任务管理器结束了、或者升级重启了，桌面仍然停在她改过
/// 的样子，而原值只在内存里的话就永远找不回来了。存在库里则下次启动仍能还原。
///
/// 这是和 AppContainer 授权同一类的东西 —— **改了用户机器上的持久状态，就必须有还原路径**，
/// 那次是写了文档没接调用点，这次从一开始就把还原做进来。
/// </summary>
public sealed class DesktopStateBackup
{
	private readonly ConfigStore _config;

	/// <summary>创建备份存取器。</summary>
	public DesktopStateBackup(ConfigStore config)
	{
		ArgumentNullException.ThrowIfNull(config);
		_config = config;
	}

	/// <summary>
	/// 第一次改某项之前存下原值；已经存过就不再覆盖。
	///
	/// 不覆盖是关键：第二次调用时当前值已经是她改过的，再存一遍就把真正的原值丢了。
	/// </summary>
	public void Remember(string key, string? original)
	{
		if (original is null || _config.Get(BackupKey(key)) is not null) return;
		_config.Set(BackupKey(key), new ConfigValue.Text(original));
	}

	/// <summary>
	/// 取出原值；没存过返回 null。
	///
	/// 走 <see cref="ConfigValue.AsStringOr"/> 而不是按 <c>ConfigValue.Text</c> 模式匹配：
	/// 配置层会把内容形如数字的文本识别成 Integer、形如 JSON 容器的识别成 Json，按 Text 匹配
	/// 就读不回来。强调色的备份值恰好是纯数字串（`4292134519`），第一版正是这么错的。
	///
	/// 同一个坑在本仓库已经是第四次（luolicore_enabled 的 "0"、工具轮数、任务清单、这里）。
	/// </summary>
	public string? Original(string key) =>
		ConfigValue.AsStringOr(_config.Get(BackupKey(key)), "") is {Length: > 0} value ? value : null;

	/// <summary>还原完成后清掉备份，下次再改时重新记。</summary>
	public void Forget(string key) => _config.Delete(BackupKey(key));

	/// <summary>有没有待还原的项。用于启动时判断上次是不是没还原干净。</summary>
	public bool HasBackup(string key) => Original(key) is not null;

	private static string BackupKey(string key) => key + "_original";
}
