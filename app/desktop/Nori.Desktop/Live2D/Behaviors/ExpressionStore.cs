using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nori.Desktop.Live2D.Behaviors;

public enum ExpressionBlendMode
{
	Add,
	Multiply,
	Overwrite,
}

public sealed class ExpressionParameter
{
	public required string ParameterId { get; init; }
	public required ExpressionBlendMode Blend { get; init; }
	public required float Value { get; init; }
}

public sealed class ExpressionGroupDefinition
{
	public required string Name { get; init; }
	public required List<ExpressionParameter> Parameters { get; init; }
}

public sealed class ExpressionEntry
{
	private const float NeutralEpsilon = 0.0001f;

	public required string Name { get; init; }
	public required string ParameterId { get; init; }
	public required ExpressionBlendMode Blend { get; set; }
	public float CurrentValue { get; set; }
	public float DefaultValue { get; set; }
	public float ModelDefault { get; set; }
	public float TargetValue { get; set; }

	public float NeutralValue => GetNeutralValue(Blend, ModelDefault);

	public static float GetNeutralValue(ExpressionBlendMode blend, float modelDefault) => blend switch
	{
		ExpressionBlendMode.Add => 0.0f,
		ExpressionBlendMode.Multiply => 1.0f,
		_ => modelDefault,
	};

	public bool IsNeutral() => MathF.Abs(CurrentValue - NeutralValue) < NeutralEpsilon;

	public float Apply(float currentFrameValue) => Blend switch
	{
		ExpressionBlendMode.Add => currentFrameValue + CurrentValue,
		ExpressionBlendMode.Multiply => currentFrameValue * CurrentValue,
		_ => CurrentValue,
	};

	public void SetValue(ExpressionBlendMode blend, float value)
	{
		Blend = blend;
		DefaultValue = NeutralValue;
		CurrentValue = value;
	}

	public void SetNeutral(ExpressionBlendMode blend)
	{
		Blend = blend;
		DefaultValue = NeutralValue;
		CurrentValue = DefaultValue;
	}

	public void Reset()
	{
		DefaultValue = NeutralValue;
		CurrentValue = DefaultValue;
	}
}

public sealed class Exp3JsonFile
{
	[JsonPropertyName("Type")]
	public string? Type { get; set; }

	[JsonPropertyName("FadeInTime")]
	public float FadeInTime { get; set; }

	[JsonPropertyName("FadeOutTime")]
	public float FadeOutTime { get; set; }

	[JsonPropertyName("Parameters")]
	public List<Exp3JsonParameter>? Parameters { get; set; }
}

public sealed class Exp3JsonParameter
{
	[JsonPropertyName("Id")]
	public string Id { get; set; } = "";

	[JsonPropertyName("Value")]
	public float Value { get; set; }

	[JsonPropertyName("Blend")]
	public string? Blend { get; set; }
}

/// <summary>
/// 表情数据仓库
///
/// 对应前端 stores/expression-store.ts
/// </summary>
public sealed class ExpressionStore
{
	public Dictionary<string, ExpressionEntry> Expressions { get; } = new(StringComparer.OrdinalIgnoreCase);
	public Dictionary<string, ExpressionGroupDefinition> ExpressionGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
	public string ModelId { get; private set; } = "";

	public void RegisterExpressions(
		string modelId,
		IEnumerable<ExpressionGroupDefinition> groups,
		IEnumerable<ExpressionEntry> entries)
	{
		Expressions.Clear();
		ExpressionGroups.Clear();
		ModelId = modelId;

		foreach (var group in groups) ExpressionGroups[group.Name] = group;
		foreach (var entry in entries) Expressions[entry.Name] = entry;
	}

	public bool Play(string name)
	{
		if (ExpressionGroups.TryGetValue(name, out var group))
		{
			foreach (var param in group.Parameters)
			{
				if (Expressions.TryGetValue(param.ParameterId, out var entry))
				{
					entry.SetValue(param.Blend, param.Value);
				}
			}
			return true;
		}

		if (Expressions.TryGetValue(name, out var singleEntry))
		{
			singleEntry.SetValue(singleEntry.Blend, singleEntry.TargetValue);
			return true;
		}

		return false;
	}

	public void Stop() => ResetAll();

	public bool Toggle(string name)
	{
		if (ExpressionGroups.TryGetValue(name, out var group))
		{
			bool isActive = group.Parameters.Any(param =>
				Expressions.TryGetValue(param.ParameterId, out var entry)
				&& entry.Blend == param.Blend
				&& MathF.Abs(entry.CurrentValue - param.Value) < 0.001f);

			foreach (var param in group.Parameters)
			{
				if (Expressions.TryGetValue(param.ParameterId, out var entry))
				{
					if (isActive) entry.SetNeutral(param.Blend);
					else entry.SetValue(param.Blend, param.Value);
				}
			}
			return true;
		}

		if (Expressions.TryGetValue(name, out var singleEntry))
		{
			if (singleEntry.IsNeutral()) singleEntry.SetValue(singleEntry.Blend, singleEntry.TargetValue);
			else singleEntry.Reset();
			return true;
		}

		return false;
	}

	public bool Set(string name, float value)
	{
		if (ExpressionGroups.TryGetValue(name, out var group))
		{
			foreach (var param in group.Parameters)
			{
				if (Expressions.TryGetValue(param.ParameterId, out var entry))
				{
					entry.SetValue(param.Blend, value);
				}
			}
			return true;
		}

		if (Expressions.TryGetValue(name, out var singleEntry))
		{
			singleEntry.SetValue(singleEntry.Blend, value);
			return true;
		}

		return false;
	}

	public void ResetAll()
	{
		foreach (var entry in Expressions.Values)
		{
			entry.Reset();
		}
	}

	public void Dispose()
	{
		Expressions.Clear();
		ExpressionGroups.Clear();
		ModelId = "";
	}

	public IReadOnlyList<string> AllNames() => [.. Expressions.Keys];
	public IReadOnlyList<string> AllGroupNames() => [.. ExpressionGroups.Keys];
}
