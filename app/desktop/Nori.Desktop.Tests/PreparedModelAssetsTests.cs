using Nori.Desktop.Live2D;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

/// <summary>后台模型资源准备与纹理解码。</summary>
[Collection("Native settings")]
public sealed class PreparedModelAssetsTests : IDisposable
{
	private const string MotionJson = """
		{
			"Version": 3,
			"Meta": {
				"Duration": 1,
				"Loop": false,
				"AreBeziersRestricted": true,
				"CurveCount": 0,
				"TotalSegmentCount": 0,
				"TotalPointCount": 0,
				"UserDataCount": 0
			},
			"Curves": [],
			"UserData": []
		}
		""";

	private const string PhysicsJson = """
		{
			"Version": 3,
			"Meta": {
				"EffectiveForces": {
					"Gravity": {"X": 0, "Y": -1},
					"Wind": {"X": 0, "Y": 0}
				},
				"Fps": 30,
				"PhysicsSettingCount": 0,
				"TotalInputCount": 0,
				"TotalOutputCount": 0,
				"VertexCount": 0
			},
			"PhysicsSettings": []
		}
		""";

	private static readonly byte[] TinyPng = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
	private static readonly byte[] SemiTransparentPng = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP43+DQAAAGgQJAY6O3bwAAAABJRU5ErkJggg==");
	private readonly string _modelDir = Path.Combine(Path.GetTempPath(), $"nori-assets-{Guid.NewGuid():N}");

	public void Dispose()
	{
		try { Directory.Delete(_modelDir, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	[Fact]
	public async Task 准备阶段读取解析并解码全部SDK资源()
	{
		Directory.CreateDirectory(_modelDir);
		File.WriteAllBytes(Path.Combine(_modelDir, "sample.moc3"), "MOC3"u8.ToArray());
		File.WriteAllBytes(Path.Combine(_modelDir, "texture.png"), TinyPng);
		File.WriteAllText(Path.Combine(_modelDir, "idle.motion3.json"), MotionJson);
		File.WriteAllText(Path.Combine(_modelDir, "sample.physics3.json"), PhysicsJson);
		File.WriteAllText(Path.Combine(_modelDir, "sample.pose3.json"), "{\"FadeInTime\":0.5,\"Groups\":[]}");
		File.WriteAllText(Path.Combine(_modelDir, "sample.model3.json"), """
			{
				"Version": 3,
				"FileReferences": {
					"Moc": "sample.moc3",
					"Textures": ["texture.png"],
					"Physics": "sample.physics3.json",
					"Pose": "sample.pose3.json",
					"Motions": {"Idle": [{"File": "idle.motion3.json"}]}
				},
				"Groups": [],
				"HitAreas": []
			}
			""");

		PreparedModel prepared = await PrepareWithHeadlessAsync("arg-nori", _modelDir, generation: 9);

		Assert.Equal(4, prepared.Assets.MocByteLength);
		Assert.True(prepared.Assets.HasPhysics);
		Assert.True(prepared.Assets.HasPose);
		Assert.Equal(1, prepared.Assets.MotionCount);
		PreparedTexture texture = Assert.Single(prepared.Assets.Textures);
		Assert.Equal(1, texture.Width);
		Assert.Equal(1, texture.Height);
		Assert.Equal(4, texture.PixelByteLength);

		Directory.Delete(_modelDir, recursive: true);
		Assert.Equal("sample.model3.json", prepared.Assets.ModelName);
		Assert.Equal(1, prepared.Assets.TextureCount);
	}

	[Fact]
	public async Task 纹理后台解码返回预乘格式和像素缓冲()
	{
		Assert.Equal(Avalonia.Platform.AlphaFormat.Premul, ModelPreparation.TextureAlphaFormat);
		TexturePixels? pixels = null;
		await BridgeCommandsTests.WithSettingsUiAsync(async () =>
		{
			pixels = await ModelPreparation.DecodeTextureAsync(SemiTransparentPng, CancellationToken.None);
		});
		Assert.NotNull(pixels);
		Assert.Equal(1, pixels!.Width);
		Assert.Equal(1, pixels.Height);
		Assert.Equal(4, pixels.Data.Length);
	}

	[Fact]
	public async Task 解码串行且排队取消不调用解码器()
	{
		using CancellationTokenSource firstCancellation = new();
		using CancellationTokenSource queuedCancellation = new();
		using ManualResetEventSlim release = new();
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<Nori.Desktop.Live2D.TexturePixels> first = Task.Run(() => ModelPreparation.DecodeTextureAsync([], firstCancellation.Token, _ =>
		{
			entered.SetResult();
			Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
			return new Nori.Desktop.Live2D.TexturePixels(1, 1, [0, 0, 0, 0]);
		}));
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			bool queuedDecodeCalled = false;
			Task<Nori.Desktop.Live2D.TexturePixels> queued = ModelPreparation.DecodeTextureAsync([], queuedCancellation.Token, _ =>
			{
				queuedDecodeCalled = true;
				return new Nori.Desktop.Live2D.TexturePixels(1, 1, [0, 0, 0, 0]);
			});
			Assert.False(queued.IsCompleted);
			queuedCancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
			Assert.False(queuedDecodeCalled);
			firstCancellation.Cancel();
			release.Set();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
			await Assert.ThrowsAsync<InvalidDataException>(() => ModelPreparation.DecodeTextureAsync([], CancellationToken.None,
				_ => throw new InvalidDataException("解码失败")));
			Nori.Desktop.Live2D.TexturePixels pixels = await ModelPreparation.DecodeTextureAsync([], CancellationToken.None,
				_ => new Nori.Desktop.Live2D.TexturePixels(1, 1, [0, 0, 0, 0]));
			Assert.Equal(4, pixels.Data.Length);
		}
		finally { release.Set(); }
	}

	[Fact]
	public void 原生库为固定版本PurismCore() => Assert.Equal(0x01010000u, NativeRuntime.ImplementationVersion);

	private static async Task<PreparedModel> PrepareWithHeadlessAsync(
		string modelId,
		string modelDir,
		long generation)
	{
		PreparedModel? prepared = null;
		await BridgeCommandsTests.WithSettingsUiAsync(async () =>
		{
			prepared = await Task.Run(() => ModelPreparation.PrepareAsync(
				modelId,
				modelDir,
				generation,
				CancellationToken.None));
		});
		return prepared ?? throw new InvalidOperationException("模型准备未返回结果");
	}
}
