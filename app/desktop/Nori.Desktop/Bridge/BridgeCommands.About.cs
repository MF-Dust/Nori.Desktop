using Nori.Core.Community;

namespace Nori.Desktop.Bridge;

// 关于页命令的实现。分发在 BridgeCommands.cs 的 switch 里；原生调用方是设置页「关于」(AboutSettingsPage)。
public sealed partial class BridgeCommands
{
	/// <summary>读取 GitHub 贡献者头像；安全模式由目录自身只读缓存，不在入口整体拦截。</summary>
	private async Task<object?> ListContributorsAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		IReadOnlyList<GitHubContributor> contributors = await _services.Contributors.GetAsync(cancellationToken);
		return new
		{
			contributors = contributors.Select(static contributor => new
			{
				login = contributor.Login,
				avatar = Convert.ToBase64String(contributor.Avatar),
			}).ToArray(),
		};
	}
}
