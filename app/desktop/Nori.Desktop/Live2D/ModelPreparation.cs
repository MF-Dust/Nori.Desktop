using System.Text.Json;
using Live2DCSharpSDK.Framework.Motion;
using Nori.Core.Live2D;
using Nori.Core.Resources;
using Nori.Desktop.Live2D.Behaviors;

namespace Nori.Desktop.Live2D;

/// <summary>
/// 表情引用 (model3.json 中声明的名称与文件)
/// </summary>
public sealed record ExpressionRef(string Name, string File);

/// <summary>
/// 后台准备完成的不可变模型元数据
///
/// 只包含磁盘 I/O 与 JSON 解析结果, 不引用、不创建任何 OpenGL 资源;
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

	public bool IsCurrent(long generation) => generation == Generation && Volatile.Read(ref _state) == 0;
	public void Invalidate() => Interlocked.CompareExchange(ref _state, 1, 0);
	public void Complete() => Interlocked.CompareExchange(ref _state, 2, 0);
}

/// <summary>
/// 模型元数据后台准备器
///
/// 读取模型目录、选定 model3.json、解析动作组与表情 exp3 参数。
/// 全程不触碰 GL/SDK 运行时状态, 可在任意线程运行并支持取消。
/// </summary>
public static class ModelPreparation
{
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
		string json = await File.ReadAllTextAsync(modelJsonPath, cancellationToken);

		List<MotionGroupInfo> motionGroups = [];
		List<ExpressionRef> expressionRefs = [];
		using (JsonDocument doc = JsonDocument.Parse(json))
		{
			if (doc.RootElement.TryGetProperty("FileReferences", out JsonElement fileRefs))
			{
				if (fileRefs.TryGetProperty("Motions", out JsonElement motions) && motions.ValueKind == JsonValueKind.Object)
				{
					foreach (JsonProperty groupProp in motions.EnumerateObject())
					{
						List<string> names = [];
						if (groupProp.Value.ValueKind == JsonValueKind.Array)
						{
							foreach (JsonElement item in groupProp.Value.EnumerateArray())
							{
								if (!item.TryGetProperty("File", out JsonElement fileProp)) continue;
								string file = fileProp.GetString() ?? "";
								string name = Path.GetFileNameWithoutExtension(file).Replace(".motion3", "");
								if (!string.IsNullOrEmpty(name)) names.Add(name);
							}
						}
						if (names.Count > 0)
						{
							motionGroups.Add(new MotionGroupInfo {Group = groupProp.Name, Names = names});
						}
					}
				}

				if (fileRefs.TryGetProperty("Expressions", out JsonElement expressions) && expressions.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement item in expressions.EnumerateArray())
					{
						string name = item.TryGetProperty("Name", out JsonElement nameProp) ? nameProp.GetString() ?? "" : "";
						string file = item.TryGetProperty("File", out JsonElement fileProp) ? fileProp.GetString() ?? "" : "";
						if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(file))
						{
							expressionRefs.Add(new ExpressionRef(name, file));
						}
					}
				}
			}
		}

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

		return new PreparedModel
		{
			ModelId = modelId,
			Generation = generation,
			ModelDir = modelDir,
			Model3FileName = Path.GetFileName(modelJsonPath),
			MotionGroups = motionGroups,
			ExpressionRefs = expressionRefs,
			ExpressionGroups = expressionGroups,
		};
	}

	private static ExpressionBlendMode NormaliseBlend(string? raw) => raw?.ToLowerInvariant() switch
	{
		"add" => ExpressionBlendMode.Add,
		"multiply" => ExpressionBlendMode.Multiply,
		_ => ExpressionBlendMode.Overwrite,
	};
}
