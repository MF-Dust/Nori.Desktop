using Nori.Core.Cloud;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public sealed partial class BridgeCommandsTests
{
	private sealed class CloudSafeModeHandler : HttpMessageHandler
	{
		public int Requests { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
		{
			Requests++;
			throw new HttpRequestException("测试不允许联网");
		}
	}

	[Theory]
	[InlineData("cloud_backup")]
	[InlineData("cloud_restore")]
	public async Task 安全模式在桥接入口拒绝云端同步(string command)
	{
		using BridgeCommandsTests fixture = new(true);
		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			fixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), command, Args(new { })));
		Assert.Contains("安全模式", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(SignInMethod.Code, false)]
	[InlineData(SignInMethod.Code, true)]
	[InlineData(SignInMethod.Password, false)]
	public async Task 安全模式直接调用登录协调器也不联网(SignInMethod method, bool codeSent)
	{
		using BridgeCommandsTests fixture = new(true);
		CloudSafeModeHandler handler = new();
		using HttpClient http = new(handler);
		fixture._services.PublicHttp = http;
		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			fixture._services.SignIn.SubmitAsync(new SignInState
			{
				Method = method, Phase = SignInPhase.Idle, CodeSent = codeSent,
				Email = "a@b.com", Code = "123456", Password = "password",
				ResendSeconds = 0, CanSubmit = true, SubmitLabel = "登录", Error = "", Status = "",
			}));
		Assert.Contains("安全模式", error.Message, StringComparison.Ordinal);
		Assert.Null(fixture._services.SignIn.Session.Current);
		Assert.Equal(0, handler.Requests);
	}

	[Fact]
	public async Task 安全模式直接调用同步服务也不联网且退出清除本机会话()
	{
		using BridgeCommandsTests fixture = new(true);
		CloudSafeModeHandler handler = new();
		using HttpClient http = new(handler);
		fixture._services.PublicHttp = http;
		AccountSession session = fixture._services.SignIn.Session;
		session.Save(new CloudAccount {Email = "a@b.com", Token = "test-token"}, SignInMethod.Code);
		CloudSyncService sync = fixture._services.CloudSync;
		Func<Task>[] requests =
		[
			() => sync.StatusAsync(),
			() => sync.BackupAsync(),
			() => sync.RestoreAsync(),
			() => sync.ForgetAsync(),
		];
		foreach (Func<Task> request in requests)
		{
			InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(request);
			Assert.Contains("安全模式", error.Message, StringComparison.Ordinal);
			Assert.NotNull(session.Current);
		}

		await fixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "account_sign_out", Args(new { }));
		Assert.Null(session.Current);
		session.Save(new CloudAccount {Email = "a@b.com", Token = "test-token"}, SignInMethod.Code);
		await fixture._services.SignIn.SignOutAsync();
		Assert.Null(session.Current);
		Assert.Equal(0, handler.Requests);
	}
}
