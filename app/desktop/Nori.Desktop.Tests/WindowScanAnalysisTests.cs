using Avalonia.Controls;
using Nori.Core.Vision;
using Nori.Desktop.Vision;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private static CapturedScreen ScanFixture(byte value = 1) => new()
	{
		Bytes = [value], MimeType = "image/jpeg", Width = 100, Height = 80, Window = "测试窗口",
	};

	private sealed class ScanAnalyzer(Func<CancellationToken, Task<string>> complete) : IVisionAnalyzer
	{
		public bool IsConfigured => true;
		internal int Calls { get; private set; }
		internal CapturedScreen? Screen { get; private set; }
		public Task<string> AnalyzeAsync(string question, CapturedScreen screen, CancellationToken cancellationToken)
		{
			Assert.Contains("screenshot", question);
			Screen = screen; Calls++;
			return complete(cancellationToken);
		}
	}

	[Fact]
	public Task ScanAnalysisUsesSelectedImageAndReplaysIdenticalContentWithoutExtraAiCalls() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new();
		using WindowScanController scan = new(fixture._services, new Window(), () => true, _ => { }, _ => { });
		ScanAnalyzer analyzer = new(_ => Task.FromResult("Analysis of selected window"));
		CapturedScreen screen = ScanFixture();
		Assert.Equal("Analysis of selected window", await scan.AnalyzeAsync(screen, analyzer, CancellationToken.None));
		Assert.Same(screen, analyzer.Screen);
		Assert.Equal(4, fixture._services.ScanChip.Charges(DateTimeOffset.UtcNow));
		Assert.Equal("Analysis of selected window", await scan.AnalyzeAsync(screen, analyzer, CancellationToken.None));
		Assert.Equal(1, analyzer.Calls);
		Assert.Equal(4, fixture._services.ScanChip.Charges(DateTimeOffset.UtcNow));
		await scan.AnalyzeAsync(ScanFixture(2), analyzer, CancellationToken.None);
		Assert.Equal(2, analyzer.Calls);
		Assert.Equal(3, fixture._services.ScanChip.Charges(DateTimeOffset.UtcNow));
	});

	[Fact]
	public Task CancelledScanDoesNotCacheLateAiResults() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new();
		using WindowScanController scan = new(fixture._services, new Window(), () => true, _ => { }, _ => { });
		using CancellationTokenSource cancellation = new();
		TaskCompletionSource<string> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
		ScanAnalyzer delayed = new(_ => response.Task);
		Task<string> operation = scan.AnalyzeAsync(ScanFixture(), delayed, cancellation.Token);
		cancellation.Cancel();
		response.SetResult("迟到结果");
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
		ScanAnalyzer fresh = new(_ => Task.FromResult("新的分析"));
		Assert.Equal("新的分析", await scan.AnalyzeAsync(ScanFixture(), fresh, CancellationToken.None));
		Assert.Equal(1, fresh.Calls);
	});

	[Fact]
	public Task ScanAnalysisRejectsSafeModeAndEmptyResponses() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests safe = new(safeMode: true);
		using WindowScanController blocked = new(safe._services, new Window(), () => true, _ => { }, _ => { });
		ScanAnalyzer analyzer = new(_ => Task.FromResult(""));
		await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.AnalyzeAsync(ScanFixture(), analyzer, CancellationToken.None));
		Assert.Equal(0, analyzer.Calls);
		Assert.Equal(5, safe._services.ScanChip.Charges(DateTimeOffset.UtcNow));
		using BridgeCommandsTests fixture = new();
		using WindowScanController scan = new(fixture._services, new Window(), () => true, _ => { }, _ => { });
		await Assert.ThrowsAsync<InvalidOperationException>(() => scan.AnalyzeAsync(ScanFixture(), analyzer, CancellationToken.None));
		Assert.Equal(1, analyzer.Calls);
	});
}
