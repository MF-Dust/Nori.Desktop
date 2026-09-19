using System.Text.Json.Nodes;
using Nori.PluginRuntime;

namespace Nori.PluginRuntime.TestPlugin;

/// <summary>用于 Runtime 集成测试的正常插件。</summary>
public sealed class TestPlugin : INoriPlugin
{
	private IPluginRegistration? _registration;

	public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		context.Logger.Info("测试插件已激活");
		await context.Storage.SetAsync("started", JsonValue.Create(true), cancellationToken);
		_registration = context.Contributions.Register(new TestContribution());
	}

	public ValueTask DeactivateAsync(CancellationToken cancellationToken)
	{
		_registration?.Dispose();
		_registration = null;
		return ValueTask.CompletedTask;
	}
}

/// <summary>可被宿主枚举的测试贡献。</summary>
public sealed class TestContribution : IPluginContribution
{
}

/// <summary>用于验证 Activate 异常边界的测试入口。</summary>
public sealed class ThrowingActivatePlugin : INoriPlugin
{
	public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("test activate failure");

	public ValueTask DeactivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>用于验证 Deactivate 异常边界的测试入口。</summary>
public sealed class ThrowingDeactivatePlugin : INoriPlugin
{
	public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

	public ValueTask DeactivateAsync(CancellationToken cancellationToken) =>
		throw new InvalidOperationException("test deactivate failure");
}

/// <summary>用于验证 entryType 必须实现 INoriPlugin 的类型。</summary>
public sealed class NotAPlugin
{
}

/// <summary>用于验证入口必须有 public parameterless constructor。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3453", Justification = "测试契约需要验证入口类型缺少 public 无参构造函数时被拒绝。")]
public sealed class PrivateConstructorPlugin : INoriPlugin
{
	private PrivateConstructorPlugin()
	{
	}

	public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
	public ValueTask DeactivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
