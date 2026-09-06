using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nori.Core.Live2D;

/// <summary>自定义伴侣互动的反应模式</summary>
public enum PetInteractionReactionMode
{
	Local,
	Ai,
}

/// <summary>自定义伴侣互动动作的选择模式</summary>
public enum PetInteractionActionMode
{
	None,
	Random,
	Selected,
}

/// <summary>互动区域内的归一化矩形</summary>
public sealed record PetInteractionRect
{
	public double X { get; init; }
	public double Y { get; init; }
	public double Width { get; init; }
	public double Height { get; init; }

	public double Area => Width * Height;

	public bool Contains(double x, double y) =>
		x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>互动区域绑定的动作或表情</summary>
public sealed record PetInteractionAction
{
	public PetInteractionActionMode Mode { get; init; }
	public string? Group { get; init; }
	public string? Name { get; init; }

	public static PetInteractionAction None => new() {Mode = PetInteractionActionMode.None};
	public static PetInteractionAction Random => new() {Mode = PetInteractionActionMode.Random};
}

/// <summary>用户框选的一个 Live2D 互动区域</summary>
public sealed record PetInteractionRegion
{
	public string Id { get; init; } = "";
	public string Name { get; init; } = "";
	public PetInteractionReactionMode ReactionMode { get; init; } = PetInteractionReactionMode.Local;
	public PetInteractionRect Rect { get; init; } = new();
	public PetInteractionAction Motion { get; init; } = PetInteractionAction.None;
	public PetInteractionAction Expression { get; init; } = PetInteractionAction.None;
}

/// <summary>按模型保存的自定义伴侣互动配置</summary>
public sealed record PetInteractionConfig
{
	public const int CurrentVersion = 1;
	public const int MaxRegions = 32;
	public const double MinRegionSize = 0.01;
	public const string AiEnabledKey = "l2d_ai_interaction_enabled";
	public const string StorageKeyPrefix = "l2d_interactions_";

	public static string StorageKey(string modelId) => $"{StorageKeyPrefix}{modelId}";

	public int Version { get; init; } = CurrentVersion;
	public List<PetInteractionRegion> Regions { get; init; } = [];

	public static PetInteractionConfig Empty => new();

	/// <summary>校验配置结构；桥接收到的错误配置必须以用户可读异常拒绝。</summary>
	public void Validate()
	{
		if (Version != CurrentVersion) throw new InvalidOperationException($"不支持的互动配置版本: {Version}");
		if (Regions is null) throw new InvalidOperationException("互动区域列表不能为空");
		if (Regions.Count > MaxRegions) throw new InvalidOperationException($"互动区域不能超过 {MaxRegions} 个");

		HashSet<string> ids = new(StringComparer.Ordinal);
		for (int index = 0; index < Regions.Count; index++)
		{
			PetInteractionRegion? region = Regions[index]
				?? throw new InvalidOperationException($"第 {index + 1} 个互动区域不能为空");
			string id = region.Id.Trim();
			if (id.Length == 0 || id.Length > 64) throw new InvalidOperationException($"第 {index + 1} 个互动区域 ID 无效");
			if (!ids.Add(id)) throw new InvalidOperationException($"互动区域 ID 重复: {id}");

			string name = region.Name.Trim();
			if (name.Length == 0 || name.Length > 40) throw new InvalidOperationException($"互动区域名称无效: {id}");
			ValidateRect(region.Rect, id);
			ValidateAction(region.Motion, true, id);
			ValidateAction(region.Expression, false, id);
		}
	}

	/// <summary>校验指定的 Motion/Expression 仍然存在于当前模型。</summary>
	public void ValidateBindings(IReadOnlyList<MotionGroupInfo> motions, IReadOnlyList<string> expressions)
	{
		Validate();
		foreach (PetInteractionRegion region in Regions)
		{
			if (region.Motion.Mode == PetInteractionActionMode.Selected)
			{
				MotionGroupInfo? group = motions.FirstOrDefault(item =>
					item.Group.Equals(region.Motion.Group, StringComparison.OrdinalIgnoreCase));
				if (group is null || !group.Names.Any(name => name.Equals(region.Motion.Name, StringComparison.OrdinalIgnoreCase)))
				{
					throw new InvalidOperationException($"互动区域“{region.Name}”绑定的动作不存在");
				}
			}

			if (region.Expression.Mode == PetInteractionActionMode.Selected
				&& !expressions.Any(name => name.Equals(region.Expression.Name, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException($"互动区域“{region.Name}”绑定的表情不存在");
			}
		}
	}

	/// <summary>从配置 JSON 读取并校验。</summary>
	public static PetInteractionConfig Parse(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return Empty;
		try
		{
			PetInteractionConfig config = JsonSerializer.Deserialize<PetInteractionConfig>(json, PetInteractionJson.Options)
				?? throw new InvalidOperationException("互动配置不能为空");
			config.Validate();
			return config;
		}
		catch (JsonException exception)
		{
			throw new InvalidOperationException($"互动配置不是有效 JSON: {exception.Message}", exception);
		}
	}

	/// <summary>序列化为配置存储使用的 JSON 节点。</summary>
	public JsonNode ToJsonNode()
	{
		Validate();
		return JsonNode.Parse(JsonSerializer.Serialize(this, PetInteractionJson.Options))
			?? throw new InvalidOperationException("互动配置序列化失败");
	}

	private static void ValidateRect(PetInteractionRect? rect, string id)
	{
		if (rect is null || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y)
			|| !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height))
		{
			throw new InvalidOperationException($"互动区域矩形无效: {id}");
		}
		if (rect.Width < MinRegionSize || rect.Height < MinRegionSize)
		{
			throw new InvalidOperationException($"互动区域过小: {id}");
		}
		if (rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > 1 || rect.Y + rect.Height > 1)
		{
			throw new InvalidOperationException($"互动区域必须位于模型范围内: {id}");
		}
	}

	private static void ValidateAction(PetInteractionAction? action, bool motion, string id)
	{
		if (action is null) throw new InvalidOperationException($"互动区域动作配置不能为空: {id}");
		string group = action.Group?.Trim() ?? "";
		string name = action.Name?.Trim() ?? "";
		switch (action.Mode)
		{
			case PetInteractionActionMode.None:
			case PetInteractionActionMode.Random:
				if (group.Length > 0 || name.Length > 0) throw new InvalidOperationException($"互动区域动作配置多余字段: {id}");
				break;
			case PetInteractionActionMode.Selected:
				if (name.Length == 0 || name.Length > 128) throw new InvalidOperationException($"互动区域指定名称无效: {id}");
				if (motion && group.Length == 0) throw new InvalidOperationException($"互动区域指定动作缺少动作组: {id}");
				if (group.Length > 128) throw new InvalidOperationException($"互动区域动作组名称过长: {id}");
				if (!motion && group.Length > 0) throw new InvalidOperationException($"互动区域表情不能包含动作组: {id}");
				break;
			default:
				throw new InvalidOperationException($"互动区域动作模式无效: {id}");
		}
	}
}

/// <summary>互动配置专用 JSON 约定。</summary>
public static class PetInteractionJson
{
	public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = {new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)},
	};
}
