using Nori.Core.Live2D;
using Nori.Core.Resources;
using Nori.Desktop.Live2D;
using Nori.Desktop.Live2D.Behaviors;

namespace Nori.Desktop.Tests;

/// <summary>
/// Live2D 模型元数据后台准备的纯 I/O 行为 (不涉及 OpenGL 上下文)
/// </summary>
public class PetModelPreparationTests : IDisposable
{
	private readonly string _modelDir = Path.Combine(Path.GetTempPath(), $"nori-prep-{Guid.NewGuid():N}");

	public void Dispose()
	{
		try
		{
			Directory.Delete(_modelDir, true);
		}
		catch (IOException)
		{
		}
	}

	private void WriteFile(string relativePath, string content)
	{
		string path = Path.Combine(_modelDir, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	private const string MODEL3_JSON = """
		{
		  "Version": 3,
		  "FileReferences": {
		    "Moc": "sample.moc3",
		    "Textures": [],
		    "Motions": {
		      "Idle": [{"File": "motions/idle_01.motion3.json"}],
		      "TapBody": [{"File": "motions/tap_a.motion3.json"}, {"File": "motions/tap_b.motion3.json"}]
		    },
		    "Expressions": [
		      {"Name": "Smile", "File": "expressions/smile.exp3.json"},
		      {"Name": "Sad", "File": "expressions/sad.exp3.json"}
		    ]
		  }
		}
		""";

	[Fact]
	public async Task 有效模型解析动作与表情元数据()
	{
		WriteFile("sample.model3.json", MODEL3_JSON);
		WriteFile("sample.moc3", "MOC3");
		WriteFile("motions/idle_01.motion3.json", "{}");
		WriteFile("motions/tap_a.motion3.json", "{}");
		WriteFile("motions/tap_b.motion3.json", "{}");
		WriteFile("expressions/smile.exp3.json", """
			{"Type":"Live2D Expression","Parameters":[
				{"Id":"ParamMouthForm","Value":1,"Blend":"Add"},
				{"Id":"ParamEyeLOpen","Value":0.9,"Blend":"Multiply"}
			]}
			""");
		WriteFile("expressions/sad.exp3.json", """
			{"Type":"Live2D Expression","Parameters":[{"Id":"ParamTear","Value":1}]}
			""");

		PreparedModel? prepared = await ModelPreparation.PrepareAsync("arg-nori", _modelDir, generation: 7, CancellationToken.None);

		Assert.NotNull(prepared);
		Assert.Equal("arg-nori", prepared.ModelId);
		Assert.Equal(7, prepared.Generation);
		Assert.Equal("sample.model3.json", prepared.Model3FileName);

		// 动作组: 文件名去掉 .motion3 后缀
		Assert.Equal(2, prepared.MotionGroups.Count);
		MotionGroupInfo idle = prepared.MotionGroups.First(group => group.Group == "Idle");
		Assert.Equal(["idle_01"], idle.Names);
		MotionGroupInfo tap = prepared.MotionGroups.First(group => group.Group == "TapBody");
		Assert.Equal(["tap_a", "tap_b"], tap.Names);

		// 表情: blend 归一化, 参数值保留
		Assert.Equal(2, prepared.ExpressionGroups.Count);
		ExpressionGroupDefinition smile = prepared.ExpressionGroups.First(group => group.Name == "Smile");
		Assert.Equal(ExpressionBlendMode.Add, smile.Parameters[0].Blend);
		Assert.Equal(1.0f, smile.Parameters[0].Value);
		Assert.Equal(ExpressionBlendMode.Multiply, smile.Parameters[1].Blend);
		ExpressionGroupDefinition sad = prepared.ExpressionGroups.First(group => group.Name == "Sad");
		// 未声明 blend 时默认 Overwrite
		Assert.Equal(ExpressionBlendMode.Overwrite, sad.Parameters[0].Blend);
	}

	[Fact]
	public async Task 不支持的模型ID被拒绝()
	{
		Directory.CreateDirectory(_modelDir);
		ResourceException error = await Assert.ThrowsAsync<ResourceException>(() =>
			ModelPreparation.PrepareAsync("other", _modelDir, 1, CancellationToken.None));
		Assert.Contains("不支持的 Live2D 模型 ID", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 目录为空或缺少模型定义时明确失败()
	{
		Directory.CreateDirectory(_modelDir);
		ResourceException missingDefinition = await Assert.ThrowsAsync<ResourceException>(() =>
			ModelPreparation.PrepareAsync("arg-nori", _modelDir, 1, CancellationToken.None));
		Assert.Contains("缺少 model3.json", missingDefinition.Message, StringComparison.Ordinal);

		string missing = Path.Combine(_modelDir, "does-not-exist");
		ResourceException missingDirectory = await Assert.ThrowsAsync<ResourceException>(() =>
			ModelPreparation.PrepareAsync("nori", missing, 1, CancellationToken.None));
		Assert.Contains("模型目录不存在", missingDirectory.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void 过期模型操作不能通过世代校验()
	{
		using CancellationTokenSource cancellation = new();
		ModelLoadOperation operation = new(
			generation: 4,
			modelId: "nori",
			fallbackModelId: "arg-nori",
			cancellation,
			Task.FromResult(ModelLoadOutcome.Canceled()));

		Assert.True(operation.IsCurrent(4));
		Assert.False(operation.IsCurrent(5));
		operation.Invalidate();
		Assert.False(operation.IsCurrent(4));
		operation.Complete();
		Assert.False(operation.IsCurrent(4));
	}

	[Fact]
	public async Task 取消令牌在读取间生效()
	{
		WriteFile("sample.model3.json", MODEL3_JSON);
		WriteFile("expressions/smile.exp3.json", "{\"Parameters\":[{\"Id\":\"A\",\"Value\":1}]}");
		WriteFile("expressions/sad.exp3.json", "{\"Parameters\":[{\"Id\":\"B\",\"Value\":2}]}");

		CancellationTokenSource cts = new();
		await cts.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => ModelPreparation.PrepareAsync("arg-nori", _modelDir, 1, cts.Token));
	}

	[Fact]
	public async Task 单个表情文件损坏不影响其余表情()
	{
		WriteFile("sample.model3.json", MODEL3_JSON);
		WriteFile("sample.moc3", "MOC3");
		WriteFile("motions/idle_01.motion3.json", "{}");
		WriteFile("motions/tap_a.motion3.json", "{}");
		WriteFile("motions/tap_b.motion3.json", "{}");
		WriteFile("expressions/smile.exp3.json", "{not valid json");
		WriteFile("expressions/sad.exp3.json", "{\"Parameters\":[{\"Id\":\"B\",\"Value\":2}]}");

		PreparedModel? prepared = await ModelPreparation.PrepareAsync("arg-nori", _modelDir, 3, CancellationToken.None);

		Assert.NotNull(prepared);
		ExpressionGroupDefinition? sad = prepared.ExpressionGroups.FirstOrDefault(group => group.Name == "Sad");
		Assert.NotNull(sad);
		Assert.DoesNotContain(prepared.ExpressionGroups, group => group.Name == "Smile");
	}

	[Theory]
	[InlineData("../outside.moc3")]
	[InlineData("C:\\outside.moc3")]
	public async Task 模型准备拒绝越界引用(string reference)
	{
		string escaped = reference.Replace("\\", "\\\\");
		WriteFile("sample.model3.json", $"{{\"FileReferences\":{{\"Moc\":\"{escaped}\"}}}}");

		ResourceException error = await Assert.ThrowsAsync<ResourceException>(() =>
			ModelPreparation.PrepareAsync("arg-nori", _modelDir, 1, CancellationToken.None));
		Assert.Contains("model3.json 引用", error.Message, StringComparison.Ordinal);
	}
}
