using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Nori.Core.Memory;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public async Task 原生迁移按实际UTF8流限制大小并拒绝非法编码和JSON()
	{
		int limit = new MemoryTransferLimits().MaxBytes;
		byte[] exact = Encoding.UTF8.GetBytes("\"" + new string('a', limit - 2) + "\"");
		using var valid = new MemoryStream(exact);
		var result = await MemoryWindow.ReadTransferContentAsync(valid, CancellationToken.None);
		Assert.Equal(limit, result.Size);
		using var oversized = new MemoryStream([.. exact, (byte)' ']);
		MemoryTransferException sizeError = await Assert.ThrowsAsync<MemoryTransferException>(() => MemoryWindow.ReadTransferContentAsync(oversized, CancellationToken.None));
		Assert.Equal(MemoryTransferErrorCategory.PayloadTooLarge, sizeError.Category);
		using var invalidUtf8 = new MemoryStream([0x22, 0xff, 0x22]);
		MemoryTransferException encodingError = await Assert.ThrowsAsync<MemoryTransferException>(() => MemoryWindow.ReadTransferContentAsync(invalidUtf8, CancellationToken.None));
		Assert.Equal(MemoryTransferErrorCategory.InvalidJson, encodingError.Category);
		using var invalidJson = new MemoryStream(Encoding.UTF8.GetBytes("{broken"));
		await Assert.ThrowsAsync<MemoryTransferException>(() => MemoryWindow.ReadTransferContentAsync(invalidJson, CancellationToken.None));
	}

	[Theory]
	[InlineData("skip", 1, 0.3)]
	[InlineData("overwrite", 1, 0.9)]
	[InlineData("create_copy", 2, 0.3)]
	public Task 原生迁移预览呈现完整元数据且三种策略按核心契约提交(string strategy, int expectedCount, double originalImportance) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryItem original = await fixture._runtime.Memory.AddAsync("迁移合成记忆", importance: 0.3, kind: MemoryKind.Preference, canonicalSummary: "迁移合成记忆");
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show(); window.Navigate("transfer");
			using var stream = new MemoryStream(Encoding.UTF8.GetBytes(TransferUiPayload()));
			await window.LoadTransferStreamAsync(stream, "memory.json");
			await window.PreviewImportAsync();
			Assert.True(window.ImportPreview.GetProperty("valid").GetBoolean());
			Assert.Equal(1, window.ImportPreview.GetProperty("conflictCount").GetInt32());
			Assert.Single(fixture._services.Memory.GetAll());
			window.UpdateLayout();
			string text = TransferUiText(window);
			Assert.Contains("迁移合成记忆", text);
			Assert.Contains("标签", text);
			Assert.Contains("合成标签", text);
			Assert.Contains("置信度", text);
			Assert.Contains("本地已有相同记忆", text);
			Assert.Equal("skip", (window.PageContent.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "MemoryImportStrategy").SelectedItem as ComboBoxItem)?.Tag);
			await window.CommitConfirmedImportAsync(strategy);
			Assert.Equal(expectedCount, fixture._services.Memory.GetAll().Count);
			Assert.Equal(originalImportance, fixture._runtime.Memory.Get(original.Id)!.Importance, 5);
			Assert.Empty(window.ImportDraftContent);
			Assert.Equal(JsonValueKind.Undefined, window.ImportPreview.ValueKind);
			await window.ExportMemoriesAsync();
			window.UpdateLayout();
			Assert.Contains("已脱敏包含字段", TransferUiText(window));
			Assert.Contains("总计导出条数", TransferUiText(window));
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task 原生迁移提交失败保留草稿且取消清除预览不写库() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show(); window.Navigate("transfer");
			using var stream = new MemoryStream(Encoding.UTF8.GetBytes(TransferUiPayload()));
			await window.LoadTransferStreamAsync(stream, "memory.json");
			await window.PreviewImportAsync();
			string token = window.ImportPreview.GetProperty("previewToken").GetString()!;
			window.Hide();
			await window.RunTransferForTestingAsync(() => window.CommitConfirmedImportAsync("skip"), "toast.importFailed");
			Assert.Contains("导入记忆失败", window.TransferError);
			Assert.NotEmpty(window.ImportDraftContent);
			Assert.Empty(fixture._services.Memory.GetAll());
			window.Show();
			await window.PreviewImportAsync();
			Assert.NotEqual(token, window.ImportPreview.GetProperty("previewToken").GetString());
			window.ResetImport();
			Assert.Empty(window.ImportDraftContent);
			Assert.Equal(JsonValueKind.Undefined, window.ImportPreview.ValueKind);
			Assert.Empty(fixture._services.Memory.GetAll());
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task 原生迁移新文件校验失败会清除旧令牌并显示明确错误() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show(); window.Navigate("transfer");
			using var stream = new MemoryStream(Encoding.UTF8.GetBytes(TransferUiPayload()));
			await window.LoadTransferStreamAsync(stream, "memory.json");
			await window.PreviewImportAsync();
			using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
			await window.RunTransferForTestingAsync(() => window.LoadTransferStreamAsync(invalid, "memory.txt"), "transfer.fileReadFailed");
			Assert.Contains("仅支持 .json 文件", window.TransferError);
			Assert.Empty(window.ImportDraftContent);
			Assert.Equal(JsonValueKind.Undefined, window.ImportPreview.ValueKind);
			Assert.Empty(fixture._services.Memory.GetAll());
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	private static string TransferUiPayload() => JsonSerializer.Serialize(new
	{
		version = "nori-memory-v1", format = "nori-memory-v1",
		memories = new[] { new { content = "迁移合成记忆", canonical_summary = "迁移合成记忆", persona_summary = "Nori迁移视角", kind = "preference", importance = 0.9, confidence = 0.85, tags = "合成标签" } },
	});

	private static string TransferUiText(MemoryWindow window) => string.Join("\n", window.PageContent.GetVisualDescendants().OfType<TextBlock>().Select(control => control.Text));
}
