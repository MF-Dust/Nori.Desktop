using System.Text.Json.Nodes;
using Nori.Core.Tools;

namespace Nori.PluginRuntime.Tests;

public sealed class PluginActionContributionTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("{}")]
	[InlineData("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}")]
	public void 有效动作支持空或对象Schema且保留默认权限(string? schemaJson)
	{
		PluginContributionRegistry registry = new();
		JsonNode? schema = schemaJson is null ? null : JsonNode.Parse(schemaJson);
		IPluginActionContribution action = new ActionContribution("Read.state_v2-01", "读取状态\r\n\t支持中文与多行说明", schema);
		using IPluginRegistration registration = registry.Register(action);

		Assert.Same(action, Assert.Single(registry.GetAll<IPluginActionContribution>()));
		Assert.Same(schema, action.ParametersSchema);
		Assert.Equal(PluginActionPermission.Safe, action.Permission);
	}

	[Fact]
	public void 动作元数据接受长度上限并拒绝超限()
	{
		PluginContributionRegistry registry = new();
		using IPluginRegistration registration = registry.Register(new ActionContribution(
			new string('a', ToolLimits.MaxNameCharacters), new string('描', ToolLimits.MaxDescriptionCharacters)));
		Assert.Single(registry.Snapshot());

		AssertInvalid(new ActionContribution(id: new string('a', ToolLimits.MaxNameCharacters + 1)));
		AssertInvalid(new ActionContribution(description: new string('描', ToolLimits.MaxDescriptionCharacters + 1)));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void 重复动作ID或同一对象被拒绝且释放后可以复用ID(bool sameInstance)
	{
		PluginContributionRegistry registry = new();
		int changed = 0;
		registry.Changed = () => changed++;
		ActionContribution action = new();
		IPluginRegistration registration = registry.Register(action);

		PluginException error = Assert.Throws<PluginException>(() => registry.Register(sameInstance ? action : new ActionContribution()));
		Assert.Equal(PluginErrorCodes.DuplicateContribution, error.Code);
		Assert.Same(action, Assert.Single(registry.Snapshot()));
		Assert.Equal(1, changed);

		registration.Dispose();
		Assert.Empty(registry.Snapshot());
		ActionContribution replacement = new();
		using IPluginRegistration replacementRegistration = registry.Register(replacement);
		registration.Dispose();
		Assert.Same(replacement, Assert.Single(registry.Snapshot()));
		Assert.Equal(3, changed);
	}

	[Fact]
	public void 动作ID按序数区分大小写且不同插件注册表互不冲突()
	{
		PluginContributionRegistry registry = new();
		using IPluginRegistration upper = registry.Register(new ActionContribution("Action"));
		using IPluginRegistration lower = registry.Register(new ActionContribution("action"));
		PluginContributionRegistry otherRegistry = new();
		using IPluginRegistration other = otherRegistry.Register(new ActionContribution("Action"));

		Assert.Equal(2, registry.GetAll<IPluginActionContribution>().Count);
		Assert.Single(otherRegistry.GetAll<IPluginActionContribution>());
	}

	[Fact]
	public void 撤销清空动作并拒绝注册直到重新开始()
	{
		PluginContributionRegistry registry = new();
		int changed = 0;
		registry.Changed = () => changed++;
		ActionContribution action = new();
		using IPluginRegistration registration = registry.Register(action);
		registry.RevokeAll();

		Assert.Empty(registry.GetAll<IPluginActionContribution>());
		Assert.Equal(PluginErrorCodes.CapabilityUnavailable, Assert.Throws<PluginException>(() => registry.Register(action)).Code);
		registration.Dispose();
		registry.RevokeAll();
		Assert.Equal(2, changed);

		using PluginCapabilityRegistry capabilities = new([], [], []);
		registry.Begin(capabilities);
		using IPluginRegistration replacement = registry.Register(action);
		Assert.Same(action, Assert.Single(registry.Snapshot()));
		Assert.Equal(3, changed);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("read state")]
	[InlineData("read/state")]
	[InlineData("read\\state")]
	[InlineData("read:state")]
	[InlineData("read\n")]
	[InlineData("read\0state")]
	[InlineData("读取状态")]
	[InlineData("réad")]
	[InlineData("ｒead")]
	public void 动作ID拒绝空值及不支持的字符(string? id) =>
		AssertInvalid(new ActionContribution(id: id!));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" \r\n\t")]
	[InlineData("读取\0状态")]
	[InlineData("读取\u001b状态")]
	[InlineData("读取\u007f状态")]
	public void 动作描述拒绝空值及非排版控制字符(string? description) =>
		AssertInvalid(new ActionContribution(description: description!));

	[Theory]
	[InlineData("[]")]
	[InlineData("[{}]")]
	[InlineData("true")]
	[InlineData("false")]
	[InlineData("42")]
	[InlineData("\"schema\"")]
	public void 动作Schema拒绝非对象根节点(string schemaJson) =>
		AssertInvalid(new ActionContribution(schema: JsonNode.Parse(schemaJson)));

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void 动作注册不读取权限且允许未知权限交由宿主回退(bool throws)
	{
		PluginContributionRegistry registry = new();
		PermissionAction action = new(throws);
		using IPluginRegistration registration = registry.Register(action);
		Assert.Same(action, Assert.Single(registry.Snapshot()));
	}

	private static void AssertInvalid(IPluginActionContribution action)
	{
		PluginContributionRegistry registry = new();
		int changed = 0;
		registry.Changed = () => changed++;
		Assert.Equal(PluginErrorCodes.InvalidManifest, Assert.Throws<PluginException>(() => registry.Register(action)).Code);
		Assert.Empty(registry.Snapshot());
		Assert.Equal(0, changed);
	}

	private class ActionContribution(string id = "action", string description = "读取状态", JsonNode? schema = null) : IPluginActionContribution
	{
		public string Id => id;
		public string Description => description;
		public JsonNode? ParametersSchema => schema;
		public Task<JsonObject?> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("注册不能执行动作");
	}

	private sealed class PermissionAction(bool throws) : ActionContribution, IPluginActionContribution
	{
		public PluginActionPermission Permission => throws
			? throw new InvalidOperationException("注册不能读取权限")
			: (PluginActionPermission)42;
	}
}
