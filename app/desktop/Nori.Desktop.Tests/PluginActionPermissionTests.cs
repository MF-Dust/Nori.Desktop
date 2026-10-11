using System.Text.Json.Nodes;
using Nori.Desktop.Runtime;
using Nori.PluginRuntime;

namespace Nori.Desktop.Tests;

/// <summary>插件动作权限 (API 2.3) 到工具注册表权限级别的映射。</summary>
public sealed class PluginActionPermissionTests
{
	[Theory]
	[InlineData(PluginActionPermission.Safe, "safe")]
	[InlineData(PluginActionPermission.Confirm, "confirm")]
	[InlineData(PluginActionPermission.Dangerous, "dangerous")]
	[InlineData((PluginActionPermission)42, "dangerous")]
	public void 声明的权限映射到工具权限级别(PluginActionPermission permission, string expected)
	{
		Assert.Equal(expected, AppRuntime.ToPluginPermissionLevel(new DeclaredAction(permission)));
	}

	[Fact]
	public void 旧插件未实现权限时默认safe且读取失败按危险处理()
	{
		Assert.Equal("safe", AppRuntime.ToPluginPermissionLevel(new LegacyAction()));
		Assert.Equal("dangerous", AppRuntime.ToPluginPermissionLevel(new ThrowingAction()));
	}

	private class LegacyAction : IPluginActionContribution
	{
		public string Id => "legacy";
		public string Description => "旧动作";
		public JsonNode? ParametersSchema => null;
		public Task<JsonObject?> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(null);
	}

	private sealed class DeclaredAction(PluginActionPermission permission) : LegacyAction, IPluginActionContribution
	{
		public PluginActionPermission Permission => permission;
	}

	private sealed class ThrowingAction : LegacyAction, IPluginActionContribution
	{
		public PluginActionPermission Permission => throw new InvalidOperationException("插件故障");
	}
}
