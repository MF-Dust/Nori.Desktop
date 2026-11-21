using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nori.Live2D;
using Nori.Core.Live2D;
using Nori.Core.Resources;
using Nori.Desktop.Live2D.Behaviors;

namespace Nori.Desktop.Live2D;

/// <summary>
/// 表情引用 (model3.json 中声明的名称与文件)
/// </summary>
public sealed record ExpressionRef(string Name, string File);

/// <summary>
/// 后台准备完成的不可变模型数据
///
/// 包含已读取并解析的模型资源与已解码纹理, 不创建任何 OpenGL 资源;
/// 携带发起时的模型世代号, 渲染线程只在世代仍匹配时消费。
/// </summary>
public sealed record PreparedModel
{
	/// <summary>模型 ID</summary>
	public required string ModelId { get; init; }

	/// <summary>发起准备时的世代号</summary>
	public required long Generation { get; init; }

	/// <summary>模型目录</summary>
	public required string ModelDir { get; init; }

	/// <summary>选定的 model3.json 文件名</summary>
	public required string Model3FileName { get; init; }

	/// <summary>解析好的动作组</summary>
	public required IReadOnlyList<MotionGroupInfo> MotionGroups { get; init; }

	/// <summary>表情声明 (名称 → exp3 文件相对路径)</summary>
	public required IReadOnlyList<ExpressionRef> ExpressionRefs { get; init; }

	/// <summary>解析好的表情组定义</summary>
	public required IReadOnlyList<ExpressionGroupDefinition> ExpressionGroups { get; init; }

	/// <summary>只等待 原生对象创建与 GL 上传的模型资源</summary>
	public required PreparedModelData Assets { get; init; }
}

/// <summary>一次模型准备的后台结果, 不在后台线程提交运行时状态。</summary>
internal sealed record ModelLoadOutcome
{
	private ModelLoadOutcome(PreparedModel? prepared, Exception? error, bool canceled)
	{
		Prepared = prepared;
		Error = error;
		IsCanceled = canceled;
	}

	public PreparedModel? Prepared { get; }
	public Exception? Error { get; }
	public bool IsCanceled { get; }

	public static ModelLoadOutcome Succeeded(PreparedModel prepared) => new(prepared, null, false);
	public static ModelLoadOutcome Failed(Exception error) => new(null, error, false);
	public static ModelLoadOutcome Canceled() => new(null, null, true);
}

/// <summary>
/// 模型加载操作的世代归属.
///
/// 状态只用于丢弃已失效的后台结果; 真正的提交仍由 PetRuntime 在同一把锁内完成。
/// </summary>
internal sealed class ModelLoadOperation
{
	private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _state;

	public ModelLoadOperation(
		long generation,
		string modelId,
		string? fallbackModelId,
		CancellationTokenSource cancellation,
		Task<ModelLoadOutcome> preparationTask)
	{
		Generation = generation;
		ModelId = modelId;
		FallbackModelId = fallbackModelId;
		Cancellation = cancellation;
		PreparationTask = preparationTask;
	}

	public long Generation { get; }
	public string ModelId { get; }
	public string? FallbackModelId { get; }
	public CancellationTokenSource Cancellation { get; }
	public Task<ModelLoadOutcome> PreparationTask { get; }
	public Task Completion => _completion.Task;

	public bool IsCurrent(long generation) => generation == Generation && Volatile.Read(ref _state) == 0;

	public void Invalidate()
	{
		if (Interlocked.CompareExchange(ref _state, 1, 0) == 0) _completion.TrySetCanceled();
	}

	public void Complete()
	{
		if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) _completion.TrySetResult();
	}

	public void Fail(Exception exception)
	{
		if (Interlocked.CompareExchange(ref _state, 3, 0) == 0) _completion.TrySetException(exception);
	}
}

/// <summary>
/// 模型资源后台准备器
///
/// 读取全部模型文件、解析 JSON 并解码纹理。
/// 全程不触碰 GL/原生 运行时状态, 可在任意线程运行并支持取消。
/// </summary>
public static class ModelPreparation
{
	// ponytail: 图片解码不可中途取消；串行限制旧世代峰值，必要时再引入可取消的解码器。
	private static readonly SemaphoreSlim TextureDecodeGate = new(1, 1);

	/// <summary>
	/// 在后台准备一个模型的全部宿主可控元数据.
	///
	/// 模型目录不存在或找不到 model3.json 时抛出 ResourceException;
	/// 取消时抛出 OperationCanceledException, 其余 IO/JSON 错误向上传播给调用方。
	/// </summary>
	public static async Task<PreparedModel> PrepareAsync(
		string modelId,
		string modelDir,
		long generation,
		CancellationToken cancellationToken)
	{
		if (!SupportedModelIds.IsSupported(modelId))
		{
			throw new ResourceException($"不支持的 Live2D 模型 ID: {modelId}");
		}
		cancellationToken.ThrowIfCancellationRequested();
		if (!Directory.Exists(modelDir))
		{
			throw new ResourceException($"模型目录不存在: {modelDir}");
		}

		string[] model3Files = Directory.GetFiles(modelDir, "*.model3.json", SearchOption.TopDirectoryOnly);
		if (model3Files.Length == 0)
		{
			throw new ResourceException($"模型目录中缺少 model3.json: {modelDir}");
		}

		cancellationToken.ThrowIfCancellationRequested();
		string modelJsonPath = model3Files.OrderBy(path => path, StringComparer.Ordinal).First();
		Model3ReferenceValidator.Validate(modelDir, modelJsonPath, cancellationToken);
		byte[] modelJson = await File.ReadAllBytesAsync(modelJsonPath, cancellationToken);

		ModelDefinition setting;
		try { setting = ModelDefinition.Parse(modelJson); }
		catch (JsonException exception)
		{
			throw new ResourceException($"model3.json 格式无效: {Path.GetFileName(modelJsonPath)}", exception);
		}
		List<MotionGroupInfo> motionGroups = setting.Motions
			.Where(group => group.Value.Count > 0)
			.Select(group => new MotionGroupInfo
			{
				Group = group.Key,
				Names = group.Value.Select(item => Path.GetFileNameWithoutExtension(item.File).Replace(".motion3", "")).ToList(),
			}).ToList();
		List<ExpressionRef> expressionRefs = setting.Expressions.Select(item => new ExpressionRef(item.Name, item.File)).ToList();

		cancellationToken.ThrowIfCancellationRequested();

		// 解析全部 exp3 表情文件 (纯数据, 不绑定模型默认值 —— 那一步留给 GL 线程的同步 apply)
		List<ExpressionGroupDefinition> expressionGroups = [];
		foreach (ExpressionRef reference in expressionRefs)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string filePath = Model3ReferenceValidator.ResolveReferencePath(modelJsonPath, reference.File);

			try
			{
				string expJson = await File.ReadAllTextAsync(filePath, cancellationToken);
				Exp3JsonFile? expFile = JsonSerializer.Deserialize<Exp3JsonFile>(expJson);
				if (expFile?.Parameters == null) continue;

				List<Behaviors.ExpressionParameter> groupParams = [];
				foreach (Exp3JsonParameter parameter in expFile.Parameters)
				{
					if (string.IsNullOrEmpty(parameter.Id)) continue;
					groupParams.Add(new Behaviors.ExpressionParameter
					{
						ParameterId = parameter.Id,
						Blend = NormaliseBlend(parameter.Blend),
						Value = parameter.Value,
					});
				}

				expressionGroups.Add(new ExpressionGroupDefinition
				{
					Name = reference.Name,
					Parameters = groupParams,
				});
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// 单个表情文件损坏不影响其余表情
			}
		}

		PreparedModelData assets = await PrepareModelDataAsync(
			modelJsonPath,
			setting,
			cancellationToken);

		cancellationToken.ThrowIfCancellationRequested();
		return new PreparedModel
		{
			ModelId = modelId,
			Generation = generation,
			ModelDir = modelDir,
			Model3FileName = Path.GetFileName(modelJsonPath),
			MotionGroups = Array.AsReadOnly(motionGroups.ToArray()),
			ExpressionRefs = Array.AsReadOnly(expressionRefs.ToArray()),
			ExpressionGroups = Array.AsReadOnly(expressionGroups.ToArray()),
			Assets = assets,
		};
	}

	private static async Task<PreparedModelData> PrepareModelDataAsync(
		string modelJsonPath,
		ModelDefinition setting,
		CancellationToken cancellationToken)
	{
		ModelDefinition references = setting;
		byte[] moc = await ReadReferenceBytesAsync(
			modelJsonPath,
			references.Moc,
			cancellationToken);

		PhysicsDefinition? physics = null;
		if (!string.IsNullOrWhiteSpace(references.Physics))
		{
			byte[] data = await ReadReferenceBytesAsync(modelJsonPath, references.Physics, cancellationToken);
			try
			{
				physics = PhysicsDefinition.Parse(data);
			}
			catch (JsonException exception)
			{
				throw new ResourceException($"Live2D 物理文件格式无效: {references.Physics}", exception);
			}
		}

		PoseDefinition? pose = null;
		if (!string.IsNullOrWhiteSpace(references.Pose))
		{
			byte[] data = await ReadReferenceBytesAsync(modelJsonPath, references.Pose, cancellationToken);
			try
			{
				pose = PoseDefinition.Parse(data);
			}
			catch (JsonException exception)
			{
				throw new ResourceException($"Live2D 姿势文件格式无效: {references.Pose}", exception);
			}
		}

		Dictionary<string, MotionClip> motions = new(StringComparer.Ordinal);
		if (references.Motions is not null)
		{
			foreach ((string group, IReadOnlyList<ModelMotion> items) in references.Motions)
			{
				for (int index = 0; index < items.Count; index++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					ModelMotion item = items[index]
						?? throw new ResourceException($"model3.json 的动作条目无效: {group}[{index}]");
					byte[] data = await ReadReferenceBytesAsync(modelJsonPath, item.File, cancellationToken);
					try
					{
						motions[$"{group}_{index}"] = MotionClip.Parse(data);
					}
					catch (JsonException exception)
					{
						throw new ResourceException($"Live2D 动作文件格式无效: {item.File}", exception);
					}
				}
			}
		}

		List<PreparedTexture> textures = [];
		if (references.Textures is not null)
		{
			for (int index = 0; index < references.Textures.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string reference = references.Textures[index];
				byte[] encoded = await ReadReferenceBytesAsync(modelJsonPath, reference, cancellationToken);
				TexturePixels pixels = await DecodeTextureAsync(encoded, cancellationToken);
				textures.Add(new PreparedTexture(index, reference, pixels));
			}
		}

		cancellationToken.ThrowIfCancellationRequested();
		return new PreparedModelData(
			Path.GetFileName(modelJsonPath),
			setting,
			moc,
			physics,
			pose,
			new System.Collections.ObjectModel.ReadOnlyDictionary<string, MotionClip>(motions),
			textures.AsReadOnly());
	}

	private static async Task<byte[]> ReadReferenceBytesAsync(
		string modelJsonPath,
		string reference,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string path = Model3ReferenceValidator.ResolveReferencePath(modelJsonPath, reference);
		return await File.ReadAllBytesAsync(path, cancellationToken);
	}

	internal static async Task<TexturePixels> DecodeTextureAsync(
		byte[] encoded,
		CancellationToken cancellationToken,
		Func<byte[], TexturePixels>? decode = null)
	{
		await TextureDecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			TexturePixels pixels = (decode ?? DecodeTexture)(encoded);
			cancellationToken.ThrowIfCancellationRequested();
			return pixels;
		}
		finally { TextureDecodeGate.Release(); }
	}

	// 纹理上传后会生成 Mipmap 并做线性过滤，必须保持预乘 Alpha，避免透明边界在过滤后再被着色器二次预乘产生灰黑边。
	internal const AlphaFormat TextureAlphaFormat = AlphaFormat.Premul;

	private static TexturePixels DecodeTexture(byte[] encoded)
	{
		using MemoryStream stream = new(encoded, writable: false);
		using Bitmap bitmap = new(stream);
		PixelSize size = bitmap.PixelSize;
		using WriteableBitmap rgba = new(size, bitmap.Dpi, PixelFormat.Rgba8888, TextureAlphaFormat);
		using ILockedFramebuffer framebuffer = rgba.Lock();
		bitmap.CopyPixels(framebuffer);

		int rowBytes = checked(size.Width * 4);
		byte[] data = new byte[checked(rowBytes * size.Height)];
		for (int row = 0; row < size.Height; row++)
		{
			Marshal.Copy(framebuffer.Address + row * framebuffer.RowBytes, data, row * rowBytes, rowBytes);
		}
		return new TexturePixels(size.Width, size.Height, data);
	}

	private static ExpressionBlendMode NormaliseBlend(string? raw) => raw?.ToLowerInvariant() switch
	{
		"add" => ExpressionBlendMode.Add,
		"multiply" => ExpressionBlendMode.Multiply,
		_ => ExpressionBlendMode.Overwrite,
	};
}
