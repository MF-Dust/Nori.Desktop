using System.Net;
using System.Text;
using Avalonia.Threading;
using Nori.Core.FirstRun;
using Nori.Core.Resources;
using Nori.Desktop.FirstRun;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public sealed partial class BridgeCommandsTests
{
	private sealed class RemoteModelHandler : HttpMessageHandler
	{
		private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal)
		{
			["/Nori_web/Nori.model3.json"] = "{\"FileReferences\":{\"Moc\":\"Nori.moc3\",\"Textures\":[\"Nori.4096/texture_00.png\"]}}",
			["/Nori_web/Nori.moc3"] = "MOC3",
			["/Nori_web/Nori.4096/texture_00.png"] = "png",
		};

		public int Requests;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
		{
			Interlocked.Increment(ref Requests);
			Assert.Equal("https://static.nori.mom", request.RequestUri!.GetLeftPart(UriPartial.Authority));
			return Task.FromResult(_files.TryGetValue(request.RequestUri.AbsolutePath, out string? body)
				? new HttpResponseMessage(HttpStatusCode.OK) {Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))}
				: new HttpResponseMessage(HttpStatusCode.NotFound));
		}
	}

	[Fact]
	public async Task model_download_remote从静态站安装内置模型()
	{
		RemoteModelHandler handler = new();
		using HttpClient http = new(handler);
		_services.PublicHttp = http;

		object? result = await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "model_download_remote", Args(new {modelId = "nori"}));

		Assert.Equal("nori", result);
		Assert.True(_services.Resources.IsInstalled(ResourceType.Live2D, "nori"));
		Assert.Equal(3, handler.Requests);
	}

	[Fact]
	public async Task model_download_remote拒绝未知模型与安全模式()
	{
		RemoteModelHandler handler = new();
		using HttpClient http = new(handler);
		_services.PublicHttp = http;
		BridgeCommands commands = CreateCommands();
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "model_download_remote", Args(new {modelId = "other"})));

		using BridgeCommandsTests fixture = new(true);
		fixture._services.PublicHttp = http;
		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			fixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "model_download_remote", Args(new {modelId = "nori"})));
		Assert.Contains("安全模式", error.Message, StringComparison.Ordinal);
		Assert.Equal(0, handler.Requests);
	}

	[Fact]
	public Task 安全模式首次运行不自动在线获取形象() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		RemoteModelHandler handler = new();
		using HttpClient http = new(handler);
		fixture._services.PublicHttp = http;
		FirstRunSteps steps = new(fixture._services, _ => { }, () => { }, _ => Task.FromResult<string?>(null), CancellationToken.None);
		steps.Build(WizardStep.Model, false);
		await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
		Assert.False(steps.IsImporting);
		Assert.Equal("先导入一个形象才能继续", steps.Gate);
		Assert.Equal(0, handler.Requests);
	});
}
