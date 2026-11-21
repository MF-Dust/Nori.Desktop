using System.Runtime.InteropServices;
using System.Text.Json;
using System.Numerics;
using Nori.Desktop.Live2D;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

/// <summary>仅在显式提供外部 Live2D 资源时执行真实模型测试。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class Live2DAssetsFactAttribute : FactAttribute
{
	public Live2DAssetsFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("NORI_TEST_LIVE2D_ASSETS") != "1")
			Skip = "设置 NORI_TEST_LIVE2D_ASSETS=1 执行真实 Live2D 资源测试；可用 NORI_LIVE2D_FIXTURES 指向 live2d 资源根。";
	}
}

/// <summary>后台模型资源与无文件 GL 提交入口。</summary>
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
	public async Task 纹理后台解码输出预乘Alpha以避免Mipmap边缘黑边()
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
		// Headless drawing 只提供尺寸，不保证像素内容；开启 NORI_TEST_NATIVE_GL=1（真实 Skia）时严格校验预乘像素值。
		if (Environment.GetEnvironmentVariable("NORI_TEST_NATIVE_GL") == "1")
		{
			byte r = pixels.Data[0], g = pixels.Data[1], b = pixels.Data[2], a = pixels.Data[3];
			Assert.Equal(128, a);
			Assert.InRange(r, (byte)127, (byte)129);
			Assert.InRange(g, (byte)63, (byte)65);
			Assert.InRange(b, (byte)31, (byte)33);
		}
	}

	[Live2DAssetsFact]
	public async Task 真实Nori模型可完整预载()
	{
		string modelDir = Path.GetDirectoryName(FindFixture("nori", "Nori.model3.json"))!;
		PreparedModel prepared = await PrepareWithHeadlessAsync("nori", modelDir, generation: 10);

		Assert.True(prepared.Assets.MocByteLength > 1_000_000);
		Assert.True(prepared.Assets.HasPhysics);
		Assert.False(prepared.Assets.HasPose);
		Assert.Equal(18, prepared.Assets.MotionCount);
		PreparedTexture texture = Assert.Single(prepared.Assets.Textures);
		Assert.True(texture.Width > 0);
		Assert.True(texture.Height > 0);
		Assert.Equal(checked(texture.Width * texture.Height * 4), texture.PixelByteLength);
	}

	[Live2DAssetsFact]
	public void 宿主提交预载模型不读取文件且两份实例独占资源()
	{
		PreparedModelData data = MemoryModel();
		var gl = new RecordingGlApi { CreateResources = true };
		using var first = new NativeModelHost(gl, data);
		using var second = new NativeModelHost(gl, data);
		Assert.NotSame(first.Model, second.Model);
		int secondTexture = second.Renderer.GetBindedTextureId(0);
		Assert.NotEqual(first.Renderer.GetBindedTextureId(0), secondTexture);
		Assert.NotNull(first.Animation.StartMotion("Idle", 0, MotionPriority.Force));
		first.Dispose();
		Assert.True(first.Model.IsDisposed);
		Assert.False(second.Model.IsDisposed);
		Assert.DoesNotContain(secondTexture, gl.DeletedTextures);
		second.Update(0.016f);
		second.Draw(Matrix4x4.Identity);
		Assert.True(gl.DrawCalls > 0);
		second.Dispose();
		Assert.Equal(gl.CreatedTextures.Order(), gl.DeletedTextures.Order());
	}

	private static PreparedModelData MemoryModel() => new(
		"内存模型",
		ModelDefinition.Parse("""
			{"FileReferences":{"Moc":"不存在.moc3","Textures":["不存在.png"],"Motions":{"Idle":[{"File":"不存在.motion3.json"}]}}}
			"""u8.ToArray()),
		File.ReadAllBytes(FindFixture("arg-nori", "ARGNori.moc3")),
		PhysicsDefinition.Parse(System.Text.Encoding.UTF8.GetBytes(PhysicsJson)),
		PoseDefinition.Parse("{\"Groups\":[]}"u8.ToArray()),
		new Dictionary<string, MotionClip> { ["Idle_0"] = MotionClip.Parse(System.Text.Encoding.UTF8.GetBytes(MotionJson)) },
		[new PreparedTexture(0, "不存在.png", new(1, 1, [255, 255, 255, 255]))]);

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

	[Live2DAssetsFact]
	public void 宿主构造失败回滚全部已创建GL资源()
	{
		PreparedModelData data = MemoryModel();
		foreach (int failureStage in new[] { 0, 1, 2, 3 })
		{
			var gl = new RecordingGlApi
			{
				CreateResources = failureStage != 0,
				FailTextureUploadAt = failureStage == 1 ? 0 : failureStage == 3 ? 1 : -1,
				FailShader = failureStage == 2 ? 0 : -1,
			};
			Assert.ThrowsAny<Exception>(() => new NativeModelHost(gl, data));
			Assert.Equal(gl.CreatedTextures.Order(), gl.DeletedTextures.Order());
			Assert.Equal(gl.CreatedBuffers.Order(), gl.DeletedBuffers.Order());
			Assert.Equal(gl.CreatedVertexArrays.Order(), gl.DeletedVertexArrays.Order());
			Assert.Equal(gl.CreatedFramebuffers.Order(), gl.DeletedFramebuffers.Order());
			Assert.Equal(gl.CreatedPrograms.Order(), gl.DeletedPrograms.Order());
			Assert.Equal(gl.CreatedShaders.Order(), gl.DeletedShaders.Order());
		}
	}

	[Fact]
	public void 原生库为固定版本PurismCore() => Assert.Equal(0x01010000u, NativeRuntime.ImplementationVersion);

	[Live2DAssetsFact]
	public unsafe void 两份真实模型更新保留遮罩标记与参数形变()
	{
		{
				foreach ((string id, string name) in new[] { ("arg-nori", "ARGNori"), ("nori", "Nori") })
				{
					using var model = new NativeModel(File.ReadAllBytes(FindFixture(id, $"{name}.moc3")));
					Assert.True(model.ParameterCount > 0);
					Assert.Contains("ParamAngleX", model.ParameterIds);
					Assert.True(model.IsUsingMasking());
					int count = model.DrawableCount;
					Assert.True(count > 0);
					float[]? previousVertices = null;
					foreach (float angle in new[] { -20f, 20f, -20f })
					{
						model.SetParameterValue(model.GetParameterIndex("ParamAngleX"), angle);
						model.Update();
						var vertices = new List<float>();
						var orders = new HashSet<int>();
						bool visible = false, changedMask = false;
						for (int drawable = 0; drawable < count; drawable++)
						{
							int order = model.GetDrawableRenderOrders()[drawable];
							Assert.InRange(order, 0, count - 1);
							Assert.True(orders.Add(order));
							visible |= model.GetDrawableDynamicFlagIsVisible(drawable);
							int vertexCount = model.GetDrawableVertexCount(drawable);
							var positions = new float[vertexCount * 2];
							Marshal.Copy((nint)model.GetDrawableVertexPositions(drawable), positions, 0, positions.Length);
							Assert.All(positions, value => Assert.True(float.IsFinite(value)));
							vertices.AddRange(positions);
							for (int mask = 0; mask < model.GetDrawableMaskCounts()[drawable]; mask++)
							{
								int maskDrawable = model.GetDrawableMasks()[drawable][mask];
								Assert.InRange(maskDrawable, 0, count - 1);
								changedMask |= model.GetDrawableDynamicFlagVertexPositionsDidChange(maskDrawable);
							}
						}
						Assert.True(visible);
						Assert.True(changedMask, "更新后遮罩顶点变化标记必须保留，不能被 reset 清除。");
						Assert.NotEmpty(vertices);
						if (previousVertices is not null) Assert.False(previousVertices.SequenceEqual(vertices));
						previousVertices = vertices.ToArray();
					}
				}
		}
	}

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

	internal static string FindFixture(string modelId, string fileName)
	{
		string? configuredRoot = Environment.GetEnvironmentVariable("NORI_LIVE2D_FIXTURES");
		if (!string.IsNullOrWhiteSpace(configuredRoot))
		{
			string root = Path.GetFullPath(configuredRoot);
			if (!Directory.Exists(root))
				throw new DirectoryNotFoundException($"NORI_LIVE2D_FIXTURES 目录不存在: {root}");
			string configuredPath = Path.Combine(root, modelId, fileName);
			if (!File.Exists(configuredPath))
				throw new FileNotFoundException($"NORI_LIVE2D_FIXTURES 缺少测试资源: {modelId}/{fileName}", configuredPath);
			return configuredPath;
		}

		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			string path = Path.Combine(
				directory.FullName,
				"data",
				"resources",
				"installed",
				"live2d",
				modelId,
				fileName);
			if (File.Exists(path)) return path;
			directory = directory.Parent;
		}
		throw new FileNotFoundException($"找不到只读 Live2D 测试资源: {modelId}/{fileName}");
	}

}
