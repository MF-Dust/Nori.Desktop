using System.Text.Json;
using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Core.Resources;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	// ---- 快照与秘密 ----

	[Fact]
	public async Task model_interactions按模型保存并返回()
	{
		string modelDir = _services.Resources.ResourceDir(ResourceType.Live2D, "nori");
		Directory.CreateDirectory(modelDir);
		File.WriteAllText(Path.Combine(modelDir, "nori.model3.json"), """
			{
				"FileReferences": {
					"Moc": "nori.moc3",
					"Textures": [],
					"Expressions": [{"Name": "01_Smile", "File": "01_Smile.exp3.json"}],
					"Motions": {"Reactions": [{"File": "motions/01_Nod.motion3.json"}]}
				}
			}
			""");
		File.WriteAllText(Path.Combine(modelDir, "nori.moc3"), "MOC3");
		File.WriteAllText(Path.Combine(modelDir, "01_Smile.exp3.json"), "{}");
		Directory.CreateDirectory(Path.Combine(modelDir, "motions"));
		File.WriteAllText(Path.Combine(modelDir, "motions", "01_Nod.motion3.json"), "{}");
		PetInteractionConfig config = new()
		{
			Regions =
			[
				new PetInteractionRegion
				{
					Id = "head",
					Name = "头部",
					Rect = new PetInteractionRect {X = 0.2, Y = 0.1, Width = 0.3, Height = 0.2},
					Motion = new PetInteractionAction
					{
						Mode = PetInteractionActionMode.Selected,
						Group = "Reactions",
						Name = "01_Nod",
					},
					Expression = new PetInteractionAction
					{
						Mode = PetInteractionActionMode.Selected,
						Name = "01_Smile",
					},
				},
			],
		};
		JsonElement interactionJson = JsonSerializer.Deserialize<JsonElement>(config.ToJsonNode().ToJsonString());
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource source = new(WindowLabels.Main);

		await commands.InvokeAsync(source, "model_set_interactions", Args(new {modelId = "nori", interactions = interactionJson}));
		object? meta = await commands.InvokeAsync(source, "model_get_meta", Args(new {modelId = "nori"}));
		string json = JsonSerializer.Serialize(meta, BridgeJson.Options);

		Assert.Contains("\"head\"", json, StringComparison.Ordinal);
		Assert.Contains("\"01_Nod\"", json, StringComparison.Ordinal);
		Assert.True(_config.Exists(PetInteractionConfig.StorageKey("nori")));
	}

	[Fact]
	public async Task ai_interaction开关按领域命令持久化()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource source = new(WindowLabels.Main);

		await commands.InvokeAsync(source, "model_set_behavior", Args(new {aiInteraction = true}));

		Assert.True(_config.GetBoolOr(PetInteractionConfig.AiEnabledKey, false));
		object snapshot = _runtime.BuildSnapshot();
		Assert.Contains("\"aiInteraction\":true", JsonSerializer.Serialize(snapshot, BridgeJson.Options), StringComparison.Ordinal);
	}

	[Fact]
	public void 快照不回传密钥()
	{
		_config.Set("llm_api_key", new ConfigValue.Text("sk-super-secret"));

		string json = JsonSerializer.Serialize(_runtime.BuildSnapshot());

		Assert.Contains("\"hasApiKey\":true", json, StringComparison.Ordinal);
		Assert.Contains("\"aiInteraction\":false", json, StringComparison.Ordinal);
		Assert.DoesNotContain("sk-super-secret", json, StringComparison.Ordinal);
	}
}
