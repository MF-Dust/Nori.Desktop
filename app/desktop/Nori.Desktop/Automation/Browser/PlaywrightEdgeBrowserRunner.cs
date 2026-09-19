using Microsoft.Playwright;
using Nori.Core.Automation;

namespace Nori.Desktop.Automation.Browser;

/// <summary>使用已安装 Microsoft Edge 的隔离浏览器运行器。</summary>
public sealed class PlaywrightEdgeBrowserRunner : IAsyncDisposable
{
	private readonly object _gate = new();
	private IPlaywright? _playwright;
	private IBrowserContext? _context;
	private string? _profileDirectory;
	private BrowserSafetySignals? _safetySignals;
	private int _disposed;

	/// <summary>当前是否已有独立 Edge 会话。</summary>
	public bool IsStarted
	{
		get
		{
			lock (_gate) return _context is not null;
		}
	}

	/// <summary>启动可见的隔离 Edge 会话；不会下载浏览器或复用默认 profile。</summary>
	public async Task<PlaywrightEdgeBrowserSession> StartAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Edge 浏览器自动化首发仅支持 Windows");
		lock (_gate)
		{
			if (_context is not null) throw new InvalidOperationException("浏览器自动化会话已经启动");
		}

		string profileDirectory = Path.Combine(Path.GetTempPath(), $"nori-edge-{Guid.NewGuid():N}");
		Directory.CreateDirectory(profileDirectory);
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			IPlaywright playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
			IBrowserContext context = await playwright.Chromium.LaunchPersistentContextAsync(
				profileDirectory,
				new BrowserTypeLaunchPersistentContextOptions
				{
					Channel = "msedge",
					Headless = false,
					AcceptDownloads = false,
					HandleSIGINT = false,
					HandleSIGTERM = false,
					HandleSIGHUP = false,
				}).WaitAsync(cancellationToken).ConfigureAwait(false);
			context.SetDefaultTimeout(BrowserAutomationPolicy.DefaultTimeoutMilliseconds);
			BrowserSafetySignals safetySignals = new();
			lock (_gate)
			{
				_playwright = playwright;
				_context = context;
				_profileDirectory = profileDirectory;
				_safetySignals = safetySignals;
			}
			context.Page += OnPage;

			IPage page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
			AttachPageHandlers(page, safetySignals);
			return new PlaywrightEdgeBrowserSession(this, context, page, safetySignals);
		}
		catch
		{
			await DisposeAsync().ConfigureAwait(false);
			TryDeleteDirectory(profileDirectory);
			throw;
		}
	}

	internal async ValueTask CloseSessionAsync(IBrowserContext context)
	{
		try
		{
			context.Page -= OnPage;
			await context.CloseAsync().ConfigureAwait(false);
		}
		catch (PlaywrightException)
		{
			// 浏览器已经崩溃或被用户关闭时仍继续清理临时 profile。
		}
		finally
		{
			IPlaywright? playwright;
			string? profile;
			lock (_gate)
			{
				if (ReferenceEquals(_context, context)) _context = null;
				playwright = _playwright;
				_playwright = null;
				profile = _profileDirectory;
				_profileDirectory = null;
				_safetySignals = null;
			}
			playwright?.Dispose();
			if (profile is not null) TryDeleteDirectory(profile);
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		IBrowserContext? context;
		IPlaywright? playwright;
		string? profile;
		lock (_gate)
		{
			context = _context;
			_context = null;
			playwright = _playwright;
			_playwright = null;
			profile = _profileDirectory;
			_profileDirectory = null;
			_safetySignals = null;
		}

		if (context is not null)
		{
			try
			{
				context.Page -= OnPage;
				await context.CloseAsync().ConfigureAwait(false);
			}
			catch (PlaywrightException) { }
		}
		playwright?.Dispose();
		if (profile is not null) TryDeleteDirectory(profile);
	}

	private void OnPage(object? sender, IPage page)
	{
		BrowserSafetySignals? safetySignals;
		lock (_gate) safetySignals = _safetySignals;
		if (safetySignals is not null) AttachPageHandlers(page, safetySignals);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1854", Justification = "浏览器事件回调参数按 Playwright 事件契约保留，后台任务结果受控丢弃。")]
	private static void AttachPageHandlers(IPage page, BrowserSafetySignals safetySignals)
	{
		page.Dialog += (sender, dialog) =>
		{
			safetySignals.Report(BrowserAutomationPolicy.PauseReason.PermissionDialog);
			_ = DismissDialogAsync(dialog);
		};
		page.FileChooser += (sender, chooser) =>
		{
			safetySignals.Report(BrowserAutomationPolicy.PauseReason.FileChooser);
			_ = CancelFileChooserAsync(chooser);
		};
		page.Download += (sender, download) =>
		{
			safetySignals.Report(BrowserAutomationPolicy.PauseReason.Download);
			_ = CancelDownloadAsync(download);
		};
	}

	private static async Task DismissDialogAsync(IDialog dialog)
	{
		try { await dialog.DismissAsync().ConfigureAwait(false); }
		catch (PlaywrightException) { }
	}

	private static async Task CancelFileChooserAsync(IFileChooser chooser)
	{
		try { await chooser.SetFilesAsync(Array.Empty<string>()).ConfigureAwait(false); }
		catch (PlaywrightException) { }
	}

	private static async Task CancelDownloadAsync(IDownload download)
	{
		try { await download.CancelAsync().ConfigureAwait(false); }
		catch (PlaywrightException) { }
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}

/// <summary>单个隔离 Edge 会话的受限 DOM 操作面。</summary>
public sealed class PlaywrightEdgeBrowserSession : IAsyncDisposable
{
	private readonly PlaywrightEdgeBrowserRunner _owner;
	private readonly IBrowserContext _context;
	private readonly IPage _page;
	private readonly BrowserSafetySignals _safetySignals;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private int _disposed;

	internal PlaywrightEdgeBrowserSession(
		PlaywrightEdgeBrowserRunner owner,
		IBrowserContext context,
		IPage page,
		BrowserSafetySignals safetySignals)
	{
		_owner = owner;
		_context = context;
		_page = page;
		_safetySignals = safetySignals;
	}

	/// <summary>执行已由 Core 解析、且由浏览器策略复核过的受限动作计划。</summary>
	public async Task<BrowserAutomationExecutionResult> ExecuteAsync(
		BrowserAutomationTaskPlan plan,
		BrowserAutomationExecutionContext executionContext,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(executionContext);
		BrowserAutomationPolicy.ValidatePlan(plan);
		string? visibleText = null;
		int completed = 0;

		foreach (BrowserAutomationAction action in plan.Actions)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await executionContext.EnsureExecutionAllowedAsyncCore(cancellationToken).ConfigureAwait(false);
			int step = completed + 1;
			executionContext.Report(new BrowserAutomationProgress(step, action.Kind, BrowserAutomationProgressState.Running));
			switch (action)
			{
				case BrowserNavigateAction navigate:
					await NavigateAsync(navigate.Url, cancellationToken).ConfigureAwait(false);
					break;
				case BrowserClickAction click:
					await ClickAsync(click.Selector, cancellationToken).ConfigureAwait(false);
					break;
				case BrowserFillAction fill:
					await RequestFillApprovalAsync(executionContext, step, cancellationToken).ConfigureAwait(false);
					await FillAsync(fill.Selector, fill.Text, cancellationToken).ConfigureAwait(false);
					break;
				case BrowserScrollAction scroll:
					await ScrollAsync(scroll.Pixels, cancellationToken).ConfigureAwait(false);
					break;
				case BrowserWaitAction wait:
					await WaitAsync(wait.Milliseconds, cancellationToken).ConfigureAwait(false);
					break;
				case BrowserReadVisibleTextAction:
					visibleText = await ReadVisibleTextAsync(cancellationToken).ConfigureAwait(false);
					break;
				default:
					throw new AutomationTaskExecutionException("invalid_action");
			}
			completed = step;
			executionContext.Report(new BrowserAutomationProgress(step, action.Kind, BrowserAutomationProgressState.ActionSucceeded));
		}
		return BrowserAutomationExecutionResult.Completed(completed, visibleText);
	}

	/// <summary>导航到用户确认过的 HTTP/HTTPS 地址，并在导航后复核安全页面。</summary>
	public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
	{
		Uri target = BrowserAutomationPolicy.ValidateNavigation(url);
		await WithPageLockAsync(async page =>
		{
			await page.GotoAsync(target.ToString(), new PageGotoOptions
			{
				WaitUntil = WaitUntilState.DOMContentLoaded,
				Timeout = BrowserAutomationPolicy.DefaultTimeoutMilliseconds,
			}).WaitAsync(cancellationToken).ConfigureAwait(false);
			await BrowserAutomationPolicy.EnsureSafePageAsync(page, _safetySignals, cancellationToken).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>读取页面可见文本摘要；返回值仅保留在调用方内存并按 UTF-8 字节截断。</summary>
	public async Task<string> ReadVisibleTextAsync(CancellationToken cancellationToken = default) =>
		await WithPageLockAsync(async page =>
		{
			await BrowserAutomationPolicy.EnsureSafePageAsync(page, _safetySignals, cancellationToken).ConfigureAwait(false);
			string text = await page.Locator("body").InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
			return BrowserAutomationTaskLimits.TruncateVisibleText(text);
		}, cancellationToken).ConfigureAwait(false);

	/// <summary>点击唯一可见的 CSS 元素，并在前后复核安全页面。</summary>
	public async Task ClickAsync(string selector, CancellationToken cancellationToken = default)
	{
		string normalized = BrowserAutomationPolicy.ValidateSelector(selector);
		await WithPageLockAsync(async page =>
		{
			await BrowserAutomationPolicy.EnsureSafePageAsync(page, _safetySignals, cancellationToken).ConfigureAwait(false);
			ILocator locator = page.Locator(normalized);
			if (await locator.CountAsync().WaitAsync(cancellationToken).ConfigureAwait(false) != 1
				|| !await locator.IsVisibleAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
				throw new AutomationTaskExecutionException("execution_failed");
			await locator.ClickAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
			await BrowserAutomationPolicy.EnsureSafePageAsync(page, _safetySignals, cancellationToken).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>向唯一可见元素填入已由宿主审批的文本，并在前后复核安全页面。</summary>
	public async Task FillAsync(string selector, string text, CancellationToken cancellationToken = default)
	{
		string normalizedSelector = BrowserAutomationPolicy.ValidateSelector(selector);
		string normalizedText = BrowserAutomationPolicy.ValidateInput(text);
		await WithPageLockAsync(async page =>
		{
			await BrowserAutomationPolicy.EnsureSafePageAsync(page, _safetySignals, cancellationToken).ConfigureAwait(false);
			ILocator locator = page.Locator(normalizedSelector);
			if (await locator.CountAsync().WaitAsync(cancellationToken).ConfigureAwait(false) != 1
				|| !await locator.IsVisibleAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
				throw new AutomationTaskExecutionException("execution_failed");
			await locator.FillAsync(normalizedText).WaitAsync(cancellationToken).ConfigureAwait(false);
			await BrowserAutomationPolicy.EnsureSafePageAsync(page, _safetySignals, cancellationToken).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>兼容旧直接会话调用；confirmed 不构成结构化任务的授权来源。</summary>
	public Task FillAsync(string selector, string text, bool confirmed, CancellationToken cancellationToken = default)
	{
		BrowserAutomationPolicy.ValidateInput(text, confirmed);
		return FillAsync(selector, text, cancellationToken);
	}

	/// <summary>在当前页面滚动有限距离。</summary>
	public async Task ScrollAsync(int pixels, CancellationToken cancellationToken = default)
	{
		int amount = BrowserAutomationPolicy.ValidateScroll(pixels);
		await WithPageLockAsync(async page =>
		{
			await BrowserAutomationPolicy.EnsureSafePageAsync(page, _safetySignals, cancellationToken).ConfigureAwait(false);
			await page.Mouse.WheelAsync(0, amount).WaitAsync(cancellationToken).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>等待页面稳定，不执行脚本。</summary>
	public async Task WaitAsync(int milliseconds, CancellationToken cancellationToken = default) =>
		await Task.Delay(BrowserAutomationPolicy.ValidateWait(milliseconds), cancellationToken).ConfigureAwait(false);

	/// <summary>获取受大小限制的内存截图，不写入文件；结构化浏览器任务不调用此入口。</summary>
	public async Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken = default) =>
		await WithPageLockAsync(async page =>
		{
			byte[] data = await page.ScreenshotAsync(new PageScreenshotOptions
			{
				Type = ScreenshotType.Png,
				FullPage = false,
			}).WaitAsync(cancellationToken).ConfigureAwait(false);
			return BrowserAutomationPolicy.ValidateScreenshot(data);
		}, cancellationToken).ConfigureAwait(false);

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_gate.Dispose();
		await _owner.CloseSessionAsync(_context).ConfigureAwait(false);
	}

	private async Task RequestFillApprovalAsync(
		BrowserAutomationExecutionContext executionContext,
		int step,
		CancellationToken cancellationToken)
	{
		AutomationApprovalCallback? callback = executionContext.ApprovalCallback;
		if (callback is null) throw new AutomationTaskExecutionException("approval_denied");
		AutomationApprovalRequest request = new(Guid.NewGuid(), executionContext.TaskId, [AutomationActionKind.TypeText], DateTimeOffset.UtcNow);
		executionContext.Report(new BrowserAutomationProgress(
			step,
			BrowserAutomationActionKind.Fill,
			BrowserAutomationProgressState.AwaitingApproval,
			request.RequestId));
		AutomationApprovalDecision decision;
		try
		{
			decision = await callback(request, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) { throw; }
		catch
		{
			throw new AutomationTaskExecutionException("approval_failed");
		}

		await executionContext.EnsureExecutionAllowedAsyncCore(cancellationToken).ConfigureAwait(false);
		if (decision.RequestId != request.RequestId || decision.Outcome != AutomationApprovalOutcome.Approved)
			throw new AutomationTaskExecutionException("approval_denied");
		executionContext.Report(new BrowserAutomationProgress(step, BrowserAutomationActionKind.Fill, BrowserAutomationProgressState.Running));
	}

	private async Task<T> WithPageLockAsync<T>(Func<IPage, Task<T>> operation, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try { return await operation(_page).ConfigureAwait(false); }
		finally { _gate.Release(); }
	}

	private async Task WithPageLockAsync(Func<IPage, Task> operation, CancellationToken cancellationToken)
	{
		await WithPageLockAsync(async page =>
		{
			await operation(page).ConfigureAwait(false);
			return true;
		}, cancellationToken).ConfigureAwait(false);
	}
}
