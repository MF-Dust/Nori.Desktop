using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nori.Core.Cloud;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 账户窗口。
///
/// 与初始化窗口同一个分档：截图这条只在视觉步骤里跑，因为它要真实 Skia。
/// 登录流程本身的判断在 <c>Nori.Core.Tests.SignInFormTests</c>，不必渲染就能测。
/// </summary>
public partial class BridgeCommandsTests
{
	/// <summary>只在专门的视觉步骤里启用真实渲染。</summary>
	[AttributeUsage(AttributeTargets.Method)]
	private sealed class NativeAccountVisualFactAttribute : FactAttribute
	{
		public NativeAccountVisualFactAttribute()
		{
			if (Environment.GetEnvironmentVariable("NORI_CAPTURE_ACCOUNT") != "1")
				Skip = "由视觉步骤使用 NORI_CAPTURE_ACCOUNT=1 执行真实渲染。";
		}
	}

	/// <summary>界面上真正会出现的几种样子。</summary>
	private sealed record AccountScene(string Name, string Email, string Password, string Code, Action<SignInForm> Arrange);

	[NativeAccountVisualFact]
	public async Task 账户窗口能截出几帧()
	{
		string output = Path.Combine(NativeSettingsCaptureDirectory(), "..", "native-account");
		Directory.CreateDirectory(output);

		await VisualUiSession.Value.Dispatch(async () =>
		{
			foreach (AccountScene scene in AccountScenes())
			{
				AccountWindow window = new(_ => Task.FromResult(new SignInOutcome {Ok = true}), _ => { });
				try
				{
					window.Show();
					// 先填文字，**把派发队列走一遍**，再摆状态。
					// TextBox.TextChanged 是异步派发的，那一路会调 SetPassword 把错误清掉；
					// 不先排空队列的话，它会在 Arrange 之后才跑，「失败」这一帧永远截不到错误。
					window.FillForTests(scene.Email, scene.Password, scene.Code);
					await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

					scene.Arrange(window.FormForTests);
					// 成功那一档由窗口的收尾动画驱动，不是 Refresh —— 光环合上就在那里面。
					if (window.FormForTests.State.Phase == SignInPhase.Done) window.ConnectForTests();
					else window.RefreshForTests();
					await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
					// 多走几帧再截：光环在转，截第一帧会拿到一个还没动起来的环。
					for (int tick = 0; tick < 8; tick++)
					{
						AvaloniaHeadlessPlatform.ForceRenderTimerTick();
						await Task.Delay(40);
					}
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();

					using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
					frame.Save(Path.Combine(output, $"account-{scene.Name}.png"), PngBitmapEncoderOptions.Default);
				}
				finally
				{
					window.Close();
				}
			}
			return true;
		}, CancellationToken.None);
	}

	private static IEnumerable<AccountScene> AccountScenes()
	{
		const string Email = "cloudnyko@gmail.com";
		const string Password = "correct horse battery staple";

		yield return new AccountScene("empty", "", "", "", _ => { });

		yield return new AccountScene("password", Email, Password, "", form =>
		{
			// 表单默认走验证码，密码这条要显式选 —— 不选的话截出来的是另一条路。
			form.UseMethod(SignInMethod.Password);
			form.SetEmail(Email);
			form.SetPassword(Password);
		});

		yield return new AccountScene("code", Email, "", "4829", form =>
		{
			form.SetEmail(Email);
			form.UseMethod(SignInMethod.Code);
			form.BeginRequest();
			form.CodeDelivered();
			form.SetCode("4829");
		});

		yield return new AccountScene("failed", Email, Password, "", form =>
		{
			form.UseMethod(SignInMethod.Password);
			form.SetEmail(Email);
			form.SetPassword(Password);
			form.BeginRequest();
			form.Fail("邮箱或密码错误。该账户可能未设置密码。");
		});

		yield return new AccountScene("busy", Email, Password, "", form =>
		{
			form.UseMethod(SignInMethod.Password);
			form.SetEmail(Email);
			form.SetPassword(Password);
			form.BeginRequest();
		});

		// 连上了那一下：外环由虚转实、染成青绿、光晕满亮。
		yield return new AccountScene("connected", Email, Password, "", form =>
		{
			form.UseMethod(SignInMethod.Password);
			form.SetEmail(Email);
			form.SetPassword(Password);
			form.BeginRequest();
			form.Succeed();
		});
	}

	/// <summary>
	/// 云端同步窗口。
	///
	/// 两帧：未登录（三个动作都该是禁用的）与已登录且云端有存档。
	/// 这扇窗一打开就去读云端状态，所以要喂一个假的 HTTP 处理器 —— 不喂的话
	/// 截到的是一屏网络错误，看不出布局。
	/// </summary>
	[NativeAccountVisualFact]
	public async Task 云端同步窗口能截出几帧()
	{
		string output = Path.Combine(NativeSettingsCaptureDirectory(), "..", "native-account");
		Directory.CreateDirectory(output);

		/*
		 * 四种状态，对应那根线的四个样子：
		 *   未登录     两端空心、整条压暗
		 *   云端空     只有本机那一端实心
		 *   在同一版   两端实心、线为实线
		 *   版本不同   两端实心、线仍是虚线
		 * localRevision 直接写配置键：那个键的读写在 CloudSyncService 里是私有的，
		 * 而这里要的只是摆出一个状态，不是走一遍真实同步。
		 */
		(string Name, bool SignedIn, int Local, string Remote)[] scenes =
		[
			("sync-signed-out", false, 0, Present(4)),
			("sync-no-remote", true, 0, """{"ok":true,"present":false}"""),
			("sync-in-step", true, 4, Present(4)),
			("sync-behind", true, 2, Present(4)),
			// 第五帧截的是亮点跑到一半 —— 那是这扇窗唯一的动效，不看一眼等于没验。
			("sync-travel", true, 4, Present(4)),
		];

		await VisualUiSession.Value.Dispatch(async () =>
		{
			foreach ((string name, bool signedIn, int local, string remote) in scenes)
			{
				using BridgeCommandsTests fixture = new(safeMode: true);
				AccountSession session = new(fixture._config);
				if (signedIn)
				{
					session.Save(new CloudAccount
					{
						Email = "cloudnyko@gmail.com",
						Token = "visual-token",
						ExpiresAt = "2027-01-01T00:00:00.000Z",
					}, SignInMethod.Code);
				}
				if (local > 0)
				{
					fixture._config.Set("cloud_save_revision",
						new Nori.Core.Configuration.ConfigValue.Text("r" + local));
				}

				using Nori.Core.Memory.MemoryTransferService transfer =
					new(new Nori.Core.Memory.MemoryStore(fixture._services.Database));
				CloudSaveService saves = new(fixture._config, transfer,
					new Nori.Core.Proactive.ReminderStore(fixture._services.Database));
				NoriCloudClient cloud = new(new HttpClient(new VisualSyncHandler(remote)), "https://example.test");
				CloudSyncService sync = new(cloud, session, saves, fixture._config);

				Nori.Desktop.Account.CloudSyncWindow window = new(sync);
				try
				{
					window.Show();
					// 窗口在 Opened 里去读云端状态。等那一趟落地，否则截到的是「正在读取」。
					for (int tick = 0; tick < 10; tick++)
					{
						await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
						await Task.Delay(40);
					}
					await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);

					if (name == "sync-travel")
					{
						// 让它跑到大约一半再截：起点和终点都看不出方向，中途才看得出。
						window.TravelForTests(Nori.Desktop.Ui.TetherDirection.Up);
						for (int tick = 0; tick < 8; tick++)
						{
							AvaloniaHeadlessPlatform.ForceRenderTimerTick();
							await Task.Delay(35);
						}
					}

					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					await Task.Delay(60);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();

					using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
					frame.Save(Path.Combine(output, $"account-{name}.png"), PngBitmapEncoderOptions.Default);
				}
				finally
				{
					window.Close();
				}
			}
			return true;
		}, CancellationToken.None);
	}

	/// <summary>
	/// 条款确认。
	///
	/// 每一个新账户都会先看到它，是第一印象面之一。两帧：首次注册、以及只因文档改版
	/// 被拦下来重新确认 —— 两种情形下人处在完全不同的位置，文案必须分叉，这一帧就是
	/// 用来核对那句话有没有分对。
	/// </summary>
	[NativeAccountVisualFact]
	public async Task 条款确认窗能截出几帧()
	{
		string output = Path.Combine(NativeSettingsCaptureDirectory(), "..", "native-account");
		Directory.CreateDirectory(output);

		LegalDocument[] docs =
		[
			new() {Key = "tos", Title = "服务条款", Version = "2.2", Sha256 = "a"},
			new() {Key = "aup", Title = "可接受使用政策", Version = "2.2", Sha256 = "b"},
			new() {Key = "disclaimer", Title = "免责声明", Version = "2.0", Sha256 = "c"},
			new() {Key = "cloud-tos", Title = "Nori 云端服务条款", Version = "1.1", Sha256 = "d"},
			new() {Key = "cloud-privacy", Title = "Nori 云端隐私政策", Version = "1.1", Sha256 = "e"},
		];

		await VisualUiSession.Value.Dispatch(async () =>
		{
			foreach ((string name, bool renewal) in new[] {("consent-first", false), ("consent-renewal", true)})
			{
				Nori.Desktop.Account.ConsentDialog dialog = Nori.Desktop.Account.ConsentDialog.ForTests(
					new Nori.Desktop.Account.ConsentRequest
					{
						Documents = docs,
						IsRenewal = renewal,
						UrlOf = key => "https://inori.nyco.cloud/legal/" + key,
					});
				try
				{
					dialog.Show();
					await Dispatcher.UIThread.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.Background);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					await Task.Delay(60);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();

					using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(dialog.CaptureRenderedFrame());
					frame.Save(Path.Combine(output, $"account-{name}.png"), PngBitmapEncoderOptions.Default);
				}
				finally
				{
					dialog.Close();
				}
			}
			return true;
		}, CancellationToken.None);
	}

	private static string Present(int revision) =>
		$$"""{"ok":true,"present":true,"revision":{{revision}},"savedAt":"2026-09-15T07:00:00.000Z","appVersion":"1.2.3","bytes":20480}""";

	/// <summary>回一份固定的云端状态。截图要的是布局，不是网络。</summary>
	private sealed class VisualSyncHandler(string body) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
			{
				Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
			});
	}
}
