namespace Nori.Core.Skills;

/// <summary>技能清单与注入提示词的边界限制。</summary>
public static class SkillLimits
{
	public const int MaxIdCharacters = 128;
	public const int MaxNameCharacters = 256;
	public const int MaxDescriptionCharacters = 2_000;
	public const int MaxAuthorCharacters = 256;
	public const int MaxVersionCharacters = 64;
	public const int MaxIconCharacters = 64;
	public const int MaxTagCharacters = 64;
	public const int MaxTags = 32;
	public const int MaxTools = 32;
	public const int MaxInstructionsCharacters = 16_000;
	public const int MaxUrlCharacters = 2_048;
	public const int MaxPromptCharacters = 32_000;
	public const int MaxRemoteDocumentCharacters = 128_000;
	public const int MaxSkills = 32;

	public static string Cap(string? value, int limit) =>
		string.IsNullOrEmpty(value) ? "" : value.Length <= limit ? value : value[..limit];

	public static SkillRecord Normalize(SkillRecord skill)
	{
		string id = Cap(skill.Id, MaxIdCharacters);
		if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("技能 ID 不能为空");
		return skill with
		{
			Id = id,
			Name = Cap(skill.Name, MaxNameCharacters),
			Description = Cap(skill.Description, MaxDescriptionCharacters),
			Author = Cap(skill.Author, MaxAuthorCharacters),
			Version = Cap(skill.Version, MaxVersionCharacters),
			Icon = Cap(skill.Icon, MaxIconCharacters),
			Tags = (skill.Tags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag))
				.Take(MaxTags).Select(tag => Cap(tag.Trim(), MaxTagCharacters)).ToList(),
			Instructions = Cap(skill.Instructions, MaxInstructionsCharacters),
			Tools = skill.Tools?
				.Where(tool => !string.IsNullOrWhiteSpace(tool))
				.Take(MaxTools)
				.Select(tool => Cap(tool.Trim(), MaxIdCharacters)).ToList(),
			Url = skill.Url is null ? null : Cap(skill.Url, MaxUrlCharacters),
			Enabled = skill.Enabled,
		};
	}
}
