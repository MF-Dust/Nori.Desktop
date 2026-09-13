using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Model;
using Live2DCSharpSDK.Framework.Motion;
using Live2DCSharpSDK.Framework.Physics;
using Live2DCSharpSDK.Framework.Rendering;
using Nori.Desktop.Live2D;

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
		LAppTextureAsset texture = Assert.Single(prepared.Assets.Textures);
		Assert.Equal(1, texture.Width);
		Assert.Equal(1, texture.Height);
		Assert.Equal(4, texture.PixelByteLength);

		Directory.Delete(_modelDir, recursive: true);
		Assert.Equal("sample.model3.json", prepared.Assets.ModelName);
		Assert.Equal(1, prepared.Assets.TextureCount);
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
		LAppTextureAsset texture = Assert.Single(prepared.Assets.Textures);
		Assert.True(texture.Width > 0);
		Assert.True(texture.Height > 0);
		Assert.Equal(checked(texture.Width * texture.Height * 4), texture.PixelByteLength);
	}

	[Live2DAssetsFact]
	public void 预加载SDK入口不读取文件或调用纹理解码器()
	{
		byte[] moc = File.ReadAllBytes(FindFixture("arg-nori", "ARGNori.moc3"));
		CubismMotionObj motion = JsonSerializer.Deserialize(
			MotionJson,
			CubismMotionObjContext.Default.CubismMotionObj)!;
		CubismPhysicsObj physics = JsonSerializer.Deserialize(
			PhysicsJson,
			CubismPhysicsObjContext.Default.CubismPhysicsObj)!;
		JsonObject pose = JsonNode.Parse("{\"Groups\":[]}")!.AsObject();
		var setting = new ModelSettingObj
		{
			FileReferences = new ModelSettingObj.FileReference
			{
				Moc = "不存在.moc3",
				Textures = ["不存在.png"],
				Physics = "不存在.physics3.json",
				Pose = "不存在.pose3.json",
				Motions = new Dictionary<string, List<ModelSettingObj.FileReference.Motion>>
				{
					["Idle"] = [new ModelSettingObj.FileReference.Motion {File = "不存在.motion3.json"}],
				},
			},
			Groups = [],
			HitAreas = [],
			Layout = [],
		};
		var assets = new LAppModelAssets(
			"内存模型",
			setting,
			moc,
			physics,
			pose,
			new Dictionary<string, CubismMotionObj> { ["Idle_0"] = motion },
			[new LAppTextureAsset(0, "不存在.png", new TexturePixels(1, 1, [255, 255, 255, 255]))]);

		CubismFramework.RunSynchronized(() =>
		{
			var allocator = new LAppAllocator();
			var option = new CubismOption {LogFunction = _ => { }, LoggingLevel = LogLevel.Off};
			Assert.True(CubismFramework.StartUp(allocator, option));
			var app = new RejectingDecodeDelegate();
			try
			{
				LAppModel model = app.Live2dManager.LoadModel(assets);
				Assert.False(app.DecodeCalled);
				Assert.Single(app.CreatedTextures);
				Assert.NotNull(model.StartMotion("Idle", 0, MotionPriority.PriorityForce));
				app.Live2dManager.RemoveModel(model);
				Assert.True(app.CreatedTextures[0].Disposed);
			}
			finally
			{
				app.Dispose();
				CubismFramework.CleanUp();
			}
		});
	}

	[Fact]
	public async Task 解码串行且排队取消不调用解码器()
	{
		using CancellationTokenSource firstCancellation = new();
		using CancellationTokenSource queuedCancellation = new();
		using ManualResetEventSlim release = new();
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<TexturePixels> first = Task.Run(() => ModelPreparation.DecodeTextureAsync([], firstCancellation.Token, _ =>
		{
			entered.SetResult();
			Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
			return new TexturePixels(1, 1, [0, 0, 0, 0]);
		}));
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			bool queuedDecodeCalled = false;
			Task<TexturePixels> queued = ModelPreparation.DecodeTextureAsync([], queuedCancellation.Token, _ =>
			{
				queuedDecodeCalled = true;
				return new TexturePixels(1, 1, [0, 0, 0, 0]);
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
			TexturePixels pixels = await ModelPreparation.DecodeTextureAsync([], CancellationToken.None,
				_ => new TexturePixels(1, 1, [0, 0, 0, 0]));
			Assert.Equal(4, pixels.Data.Length);
		}
		finally { release.Set(); }
	}

	[Live2DAssetsFact]
	public void 构造失败释放原生模型渲染器及已上传纹理()
	{
		byte[] moc = File.ReadAllBytes(FindFixture("arg-nori", "ARGNori.moc3"));
		CubismFramework.RunSynchronized(() =>
		{
			var allocator = new TrackingAllocator();
			Assert.True(CubismFramework.StartUp(allocator, new CubismOption {LogFunction = _ => { }, LoggingLevel = LogLevel.Off}));
			try
			{
				foreach (int failureStage in new[] { 0, 1, 2, 3 })
				{
					using var app = new RejectingDecodeDelegate
					{
						FailRenderer = failureStage == 1,
						FailTextureIndex = failureStage >= 2 ? 1 : -1,
						FailRendererDispose = failureStage == 3,
					};
					var setting = new ModelSettingObj
					{
						FileReferences = new ModelSettingObj.FileReference
						{
							Moc = "内存.moc3",
							Textures = ["一.png", "二.png"],
							Motions = failureStage == 0
								? new() { ["Idle"] = [new() {File = "损坏.motion3.json"}] }
								: [],
						},
						Groups = [], HitAreas = [], Layout = [],
					};
					var assets = new LAppModelAssets("失败回滚", setting, moc, null, null,
						new Dictionary<string, CubismMotionObj> { ["Idle_0"] = new() },
						[new(0, "一.png", new(1, 1, [0, 0, 0, 0])), new(1, "二.png", new(1, 1, [0, 0, 0, 0]))]);
					Exception error = Assert.ThrowsAny<Exception>(() => app.Live2dManager.LoadModel(assets));
					if (failureStage >= 2)
					{
						Assert.Equal("纹理上传失败", error.Message);
						Assert.True(Assert.Single(app.CreatedTextures).Disposed);
						Assert.True(app.CreatedRenderer!.Disposed);
					}
					Assert.True(allocator.AllocationCount > 0);
					Assert.Equal(0, allocator.Outstanding);
				}
			}
			finally { CubismFramework.CleanUp(); }
		});
	}

	private static async Task<PreparedModel> PrepareWithHeadlessAsync(
		string modelId,
		string modelDir,
		long generation)
	{
		PreparedModel? prepared = null;
		using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(BridgeCommandsTests));
		await session.Dispatch(async () =>
		{
			prepared = await Task.Run(() => ModelPreparation.PrepareAsync(
				modelId,
				modelDir,
				generation,
				CancellationToken.None));
			return true;
		}, CancellationToken.None);
		return prepared ?? throw new InvalidOperationException("模型准备未返回结果");
	}

	private static string FindFixture(string modelId, string fileName)
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

	private sealed class RejectingDecodeDelegate : LAppDelegate
	{
		public RejectingDecodeDelegate() => InitApp();

		public bool DecodeCalled { get; private set; }
		public bool FailRenderer { get; init; }
		public bool FailRendererDispose { get; init; }
		public int FailTextureIndex { get; init; } = -1;
		public FakeRenderer? CreatedRenderer { get; private set; }
		public List<FakeTexture> CreatedTextures { get; } = [];

		public override CubismRenderer CreateRenderer(CubismModel model)
		{
			if (FailRenderer) throw new InvalidOperationException("渲染器创建失败");
			return CreatedRenderer = new FakeRenderer(model) {FailDispose = FailRendererDispose};
		}

		public override TextureInfo CreateTexture(LAppModel model, int index, int width, int height, nint data)
		{
			if (index == FailTextureIndex) throw new InvalidOperationException("纹理上传失败");
			var texture = new FakeTexture {Id = CreatedTextures.Count + 1};
			CreatedTextures.Add(texture);
			return texture;
		}

		public override TexturePixels DecodeTexture(string fileName)
		{
			DecodeCalled = true;
			throw new InvalidOperationException("测试不允许同步纹理解码");
		}
	}

	private sealed class TrackingAllocator : ICubismAllocator
	{
		private readonly LAppAllocator _inner = new();
		public int Outstanding { get; private set; }
		public int AllocationCount { get; private set; }
		public nint Allocate(int size) => _inner.Allocate(size);
		public void Deallocate(nint memory) => _inner.Deallocate(memory);
		public nint AllocateAligned(int size, int alignment)
		{
			nint memory = _inner.AllocateAligned(size, alignment);
			Outstanding++;
			AllocationCount++;
			return memory;
		}
		public void DeallocateAligned(nint memory)
		{
			_inner.DeallocateAligned(memory);
			Outstanding--;
		}
	}

	private sealed class FakeTexture : TextureInfo
	{
		public bool Disposed { get; private set; }
		public override void Dispose() => Disposed = true;
	}

	private sealed class FakeRenderer(CubismModel model) : CubismRenderer(model)
	{
		public bool Disposed { get; private set; }
		public bool FailDispose { get; init; }
		public override void Dispose()
		{
			Disposed = true;
			if (FailDispose) throw new InvalidOperationException("渲染器释放失败");
		}
		protected override void DoDrawModel() { }
		protected override void SaveProfile() { }
		protected override void RestoreProfile() { }
	}
}
