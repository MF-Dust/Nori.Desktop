using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nori.Core.Configuration;
using Nori.Core.Network;
using Nori.Core.Security;
using YamlDotNet.Serialization;

namespace Nori.Core.Skills;

/// <summary>
/// 技能管理器服务
///
/// 支持本地技能、市场安装、URL 网络安装 (SKILL.md / JSON) 与 Prompt 动态注入。
/// 数据持久化沿用前端写入的 config 键 nori_skills (JSON 数组), 完全兼容既有数据。
/// </summary>
public sealed class SkillService(ConfigStore configStore, HttpClient httpClient)
{
	private const string ConfigKey = "nori_skills";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private enum RemoteContentFailure
	{
		Empty,
		TooLarge,
		Sensitive,
		InvalidFormat,
	}

	/// <summary>
	/// 远程技能正文上限是 12.8 万字符。两秒足够慢机器做线性扫描，同时拦住失控匹配。
	/// frontmatter 依赖懒惰量词停在第一处结束分隔符，不能改成 NonBacktracking。
	/// </summary>
	internal static readonly TimeSpan ContentMatchTimeout = TimeSpan.FromSeconds(2);

	internal const string ContentParseTimeoutMessage = "技能文件解析超时";

	private static readonly Regex SensitiveRemoteContentPattern = new(
		@"(?ix)
			(?:
				[""']?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|bearer|password|secret|client[_-]?secret|private[_-]?key|cookie|token)[""']?
				\s*[:=]\s*[""']?[^\s""',;}\]]+
			)
			|
			[?&](?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|bearer|password|secret|client[_-]?secret|private[_-]?key|cookie|token)=[^&#\s]+
			|
			-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----
			|
			\b(?:sk-[A-Za-z0-9_-]{8,}|gh[pousr]_[A-Za-z0-9_-]{16,}|xox[baprs]-[A-Za-z0-9-]{16,})\b",
		RegexOptions.IgnoreCase
			| RegexOptions.CultureInvariant
			| RegexOptions.IgnorePatternWhitespace
			| RegexOptions.Compiled,
		ContentMatchTimeout);

	private static readonly Regex SkillFrontmatterPattern = new(
		@"^---\r?\n(?<front>.*?)\r?\n---(?:\r?\n|$)(?<body>.*)$",
		RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled,
		ContentMatchTimeout);

	private static readonly Regex SkillIdPattern = new(
		@"[^a-z0-9_-]+",
		RegexOptions.CultureInvariant | RegexOptions.Compiled,
		ContentMatchTimeout);

	internal static readonly Lock ContentMatchGate = new();
	internal static Func<string, bool> MatchSensitiveContent { get; set; } = static text => SensitiveRemoteContentPattern.IsMatch(text);
	internal static Func<string, Match> MatchSkillFrontmatter { get; set; } = static text => SkillFrontmatterPattern.Match(text);
	internal static Func<string, string> MatchSkillId { get; set; } = static name => SkillIdPattern.Replace(name, "-");

	private const int MaxPendingImports = 8;
	private static readonly TimeSpan ImportPreviewLifetime = TimeSpan.FromMinutes(10);

	private readonly Lock _gate = new();
	private readonly Dictionary<string, PendingSkillImport> _pendingImports = new(StringComparer.Ordinal);
	private Dictionary<string, SkillRecord> _skills = [];
	private bool _initialized;

	private sealed record PendingSkillImport(SkillRecord Skill, DateTimeOffset ExpiresAt);

	/// <summary>加载技能列表 (首次缺失时种子内置预设)</summary>
	public void EnsureLoaded()
	{
		lock (_gate)
		{
			if (_initialized) return;
			// 内置技能由程序集定义，配置只能恢复其 Enabled 状态，不能替换名称、指令或权限等内容。
			Dictionary<string, SkillRecord> loaded = SkillPresets.All
				.Where(preset => preset.Source == "builtin")
				.ToDictionary(preset => preset.Id, preset => SkillLimits.Normalize(preset));

			try
			{
				if (configStore.Get(ConfigKey) is ConfigValue.Json { Value: JsonNode node })
				{
					List<SkillRecord>? list = node.Deserialize<List<SkillRecord>>(JsonOptions);
					foreach (SkillRecord rawSkill in list ?? [])
					{
						if (string.IsNullOrWhiteSpace(rawSkill.Id)) continue;
						if (loaded.TryGetValue(rawSkill.Id, out SkillRecord? builtin))
						{
							// 只接受旧配置对内置技能的启停修改，拒绝覆盖内置定义。
							loaded[rawSkill.Id] = builtin with
							{
								Enabled = rawSkill.Enabled,
							};
							continue;
						}
						SkillRecord normalized = SkillLimits.Normalize(rawSkill);
						loaded[normalized.Id] = normalized;
					}
				}
			}
			catch (JsonException)
			{
				// 配置损坏时保留可信的内置预设, 不抛出避免阻断启动
			}

			_skills = loaded;
			_initialized = true;
			SaveLocked();
		}
	}

	/// <summary>获取所有已安装技能列表</summary>
	public IReadOnlyList<SkillRecord> GetInstalled()
	{
		EnsureLoaded();
		lock (_gate) return _skills.Values.ToList();
	}

	/// <summary>获取所有当前激活启用的技能列表</summary>
	public IReadOnlyList<SkillRecord> GetEnabled() => GetInstalled().Where(skill => skill.Enabled).ToList();

	/// <summary>获取技能市场目录</summary>
	public static IReadOnlyList<SkillRecord> Marketplace() => SkillPresets.All;

	/// <summary>切换技能启用状态</summary>
	public bool Toggle(string id, bool enabled)
	{
		EnsureLoaded();
		lock (_gate)
		{
			if (!_skills.TryGetValue(id, out SkillRecord? skill)) return false;
			skill.Enabled = enabled;
			SaveLocked();
			return true;
		}
	}

	/// <summary>从市场安装技能</summary>
	public SkillRecord InstallFromMarketplace(string skillId)
	{
		EnsureLoaded();
		SkillRecord? target = SkillPresets.Find(skillId)
			?? throw new InvalidOperationException($"未在市场中找到技能 ID: {skillId}");

		SkillRecord installed = SkillLimits.Normalize(target with
		{
			Enabled = true,
			InstalledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
		});
		Upsert(installed);
		return installed;
	}

	/// <summary>创建或保存自定义技能</summary>
	public SkillRecord SaveCustom(SkillRecord skill)
	{
		EnsureLoaded();
		if (SkillPresets.Find(skill.Id) is not null)
		{
			throw new InvalidOperationException($"不能覆盖内置技能: {skill.Id}");
		}
		long existingAt;
		lock (_gate) existingAt = _skills.TryGetValue(skill.Id, out SkillRecord? old) ? old.InstalledAt : 0;
		SkillRecord complete = SkillLimits.Normalize(skill with
		{
			Source = "custom",
			Enabled = skill.Enabled,
			InstalledAt = existingAt != 0 ? existingAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
		});
		Upsert(complete);
		return complete;
	}

	/// <summary>卸载删除技能。内置技能由程序集提供，不能删除。</summary>
	public bool Uninstall(string id)
	{
		EnsureLoaded();
		if (SkillPresets.Find(id) is {Source: "builtin"}) throw new InvalidOperationException("内置技能不能卸载");
		lock (_gate)
		{
			if (_skills.TryGetValue(id, out SkillRecord? skill)
				&& string.Equals(skill.Source, "builtin", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("内置技能不能卸载");
			}
			bool removed = _skills.Remove(id);
			if (removed) SaveLocked();
			return removed;
		}
	}

	/// <summary>复制为新的自定义技能。始终分配新 ID，默认关闭。</summary>
	public SkillRecord Duplicate(string id)
	{
		EnsureLoaded();
		SkillRecord source;
		lock (_gate)
		{
			if (!_skills.TryGetValue(id, out source!)) throw new InvalidOperationException("技能不存在");
		}
		return SaveCustom(source with
		{
			Id = AllocateCustomId(null),
			Enabled = false,
			Source = "custom",
			Url = null,
		});
	}

	/// <summary>预览本地 SKILL.md 或 JSON。返回值不含指令正文。</summary>
	public SkillImportPreview PreviewLocal(string text)
	{
		EnsureLoaded();
		SkillRecord parsed = ParseLocalDocument(text);
		string token = Guid.NewGuid().ToString("N");
		lock (_gate)
		{
			PurgeExpiredImports();
			while (_pendingImports.Count >= MaxPendingImports)
			{
				string oldest = _pendingImports.OrderBy(pair => pair.Value.ExpiresAt).First().Key;
				_pendingImports.Remove(oldest);
			}
			_pendingImports[token] = new PendingSkillImport(parsed, DateTimeOffset.UtcNow.Add(ImportPreviewLifetime));
		}
		return new SkillImportPreview
		{
			Token = token,
			Name = parsed.Name,
			Description = parsed.Description,
			Category = parsed.Category,
			Tools = parsed.Tools ?? [],
		};
	}

	/// <summary>确认本地导入。记为自定义技能，启用状态由调用方决定。令牌只能使用一次。</summary>
	public SkillRecord CommitLocal(string token, bool enabled)
	{
		EnsureLoaded();
		SkillRecord staged;
		lock (_gate)
		{
			PurgeExpiredImports();
			if (!_pendingImports.Remove(token, out PendingSkillImport? pending) || pending is null)
				throw new InvalidOperationException("预览已失效");
			staged = pending.Skill;
		}
		return SaveCustom(staged with
		{
			Id = AllocateCustomId(staged.Id),
			Enabled = enabled,
			Source = "custom",
			Url = null,
		});
	}

	/// <summary>导出技能为 JSON 字符串</summary>
	public string Export(string id)
	{
		EnsureLoaded();
		lock (_gate)
		{
			if (!_skills.TryGetValue(id, out SkillRecord? skill)) throw new InvalidOperationException("技能不存在");
			return JsonSerializer.Serialize(skill, JsonOptions);
		}
	}

	/// <summary>导入 JSON 技能</summary>
	public SkillRecord ImportJson(string json)
	{
		EnsureLoaded();
		SkillRecord data = JsonSerializer.Deserialize<SkillRecord>(json, JsonOptions)
			?? throw new InvalidOperationException("技能 JSON 解析失败");
		if (string.IsNullOrEmpty(data.Name) || string.IsNullOrEmpty(data.Instructions))
		{
			throw new InvalidOperationException("技能 JSON 缺少必要的 name 或 instructions 字段");
		}
		string id = string.IsNullOrEmpty(data.Id) ? $"custom_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : data.Id;
		if (SkillPresets.Find(id) is not null)
		{
			throw new InvalidOperationException($"不能覆盖内置技能: {id}");
		}
		SkillRecord skill = SkillLimits.Normalize(data with
		{
			Id = id,
			Source = "custom",
			InstalledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Enabled = true,
		});
		Upsert(skill);
		return skill;
	}

	/// <summary>
	/// 从远程网络 URL 安装技能 (支持 JSON 规范与 SKILL.md Markdown 规范)
	/// </summary>
	public async Task<SkillRecord> InstallFromUrlAsync(string url, CancellationToken cancellationToken = default)
	{
		EnsureLoaded();
		Uri uri = new(url);
		Nori.Core.Network.UrlAccessPolicy.EnsurePublicHttp(uri);

		using HttpResponseMessage response = await Nori.Core.Network.UrlAccessPolicy.GetWithSafeRedirectsAsync(
			httpClient, uri, allowPrivate: false, cancellationToken: cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"下载技能失败: HTTP {(int)response.StatusCode}");
		}
		if (response.Content.Headers.ContentLength > SkillLimits.MaxRemoteDocumentCharacters)
		{
			throw CreateRemoteContentError(RemoteContentFailure.TooLarge);
		}

		string text;
		try
		{
			text = await UrlAccessPolicy.ReadCappedTextAsync(
				response.Content, SkillLimits.MaxRemoteDocumentCharacters, cancellationToken);
		}
		catch (InvalidOperationException exception) when (
			exception.Message.StartsWith("远程文件超过大小上限", StringComparison.Ordinal))
		{
			throw CreateRemoteContentError(RemoteContentFailure.TooLarge);
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			throw CreateRemoteContentError(RemoteContentFailure.Empty);
		}
		SkillRecord skill = ParseDownloadedSkill(text, url);
		string remoteId = AvailableRemoteId(skill.Id);
		SkillRecord remote = SkillLimits.Normalize(skill with
		{
			Id = remoteId,
			Enabled = false,
			Source = "url",
			Url = url,
		});
		Upsert(remote);
		return remote;
	}

	internal SkillRecord ParseDownloadedSkill(string text, string? url)
	{
		if (ContainsSensitiveContent(text))
		{
			throw CreateRemoteContentError(RemoteContentFailure.Sensitive);
		}

		SkillRecord skill;
		try
		{
			skill = text.TrimStart().StartsWith("---", StringComparison.Ordinal)
				? ParseSkillMarkdown(text, url)
				: ParseSkillJson(text, url);
		}
		catch (Exception exception) when (exception is JsonException
			|| exception is InvalidOperationException && exception is not SkillContentTimeoutException)
		{
			throw CreateRemoteContentError(RemoteContentFailure.InvalidFormat);
		}
		if (string.IsNullOrWhiteSpace(skill.Instructions))
		{
			throw CreateRemoteContentError(RemoteContentFailure.InvalidFormat);
		}
		return skill;
	}

	private static InvalidOperationException CreateRemoteContentError(RemoteContentFailure failure)
	{
		string message = failure switch
		{
			RemoteContentFailure.Empty => "远程技能文件内容为空",
			RemoteContentFailure.TooLarge => "远程文件超过大小上限",
			RemoteContentFailure.Sensitive => "远程技能文件包含敏感信息，已拒绝",
			_ => "远程技能文件格式无效",
		};
		return new InvalidOperationException(message);
	}

	private string AvailableRemoteId(string requestedId)
	{
		const int suffixLength = 16;
		string baseId = string.IsNullOrWhiteSpace(requestedId) ? "remote-skill" : requestedId.Trim();
		baseId = SkillLimits.Cap(baseId, SkillLimits.MaxIdCharacters - suffixLength);
		lock (_gate)
		{
			if (SkillPresets.Find(baseId) is null && !_skills.ContainsKey(baseId)) return baseId;
			string candidate;
			do
			{
				candidate = $"{baseId}-remote-{Guid.NewGuid().ToString("N")[..8]}";
			}
			while (SkillPresets.Find(candidate) is not null || _skills.ContainsKey(candidate));
			return candidate;
		}
	}

	private SkillRecord ParseLocalDocument(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("技能内容不能为空");
		if (text.Length > SkillLimits.MaxRemoteDocumentCharacters) throw new InvalidOperationException("技能文件超过大小上限");
		if (ContainsSensitiveContent(text)) throw new InvalidOperationException("技能文件包含敏感信息，已拒绝");
		SkillRecord parsed;
		try
		{
			parsed = text.TrimStart().StartsWith("---", StringComparison.Ordinal)
				? ParseSkillMarkdown(text, null)
				: ParseSkillJson(text, null);
		}
		catch (Exception exception) when (exception is JsonException
			|| exception is InvalidOperationException && exception is not SkillContentTimeoutException)
		{
			throw new InvalidOperationException("技能文件格式无效");
		}
		if (string.IsNullOrWhiteSpace(parsed.Name) || string.IsNullOrWhiteSpace(parsed.Instructions))
			throw new InvalidOperationException("技能缺少名称或指令正文");
		return parsed;
	}

	private string AllocateCustomId(string? requested)
	{
		string id = requested?.Trim() ?? "";
		lock (_gate)
		{
			if (id.Length > 0 && SkillPresets.Find(id) is null && !_skills.ContainsKey(id)) return id;
			string candidate;
			do
			{
				candidate = "skill_custom_" + Guid.NewGuid().ToString("N")[..8];
			}
			while (SkillPresets.Find(candidate) is not null || _skills.ContainsKey(candidate));
			return candidate;
		}
	}

	private void PurgeExpiredImports()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		foreach (string key in _pendingImports.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
			_pendingImports.Remove(key);
	}

	private SkillRecord ParseSkillJson(string text, string? url)
	{
		SkillRecord manifest = JsonSerializer.Deserialize<SkillRecord>(text, JsonOptions)
			?? throw new InvalidOperationException("解析远程技能格式失败: 不是合法的 JSON 技能清单");
		return manifest with
		{
			Id = string.IsNullOrEmpty(manifest.Id) ? $"skill_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : manifest.Id,
			Name = string.IsNullOrEmpty(manifest.Name) ? "未命名网络技能" : manifest.Name,
			Description = string.IsNullOrEmpty(manifest.Description) ? "从网络导入的技能" : manifest.Description,
			Author = string.IsNullOrEmpty(manifest.Author) ? "Online Author" : manifest.Author,
			Version = string.IsNullOrEmpty(manifest.Version) ? "1.0.0" : manifest.Version,
			Category = string.IsNullOrEmpty(manifest.Category) ? "productivity" : manifest.Category,
			Source = "url",
			Url = url,
		};
	}

	/// <summary>解析 SKILL.md (YAML Frontmatter + Markdown Instructions)</summary>
	public static SkillRecord ParseSkillMarkdown(string content, string? url)
	{
		Func<string, Match> frontmatter;
		lock (ContentMatchGate) frontmatter = MatchSkillFrontmatter;
		Match match = GuardContentMatch(() => frontmatter(content.TrimStart()));
		if (!match.Success)
		{
			throw new InvalidOperationException("SKILL.md 格式错误：缺少完整的 YAML frontmatter 头部分隔符 ---");
		}

		Dictionary<string, string> meta;
		try
		{
			Dictionary<string, object?> parsed = new DeserializerBuilder()
				.IgnoreUnmatchedProperties()
				.Build()
				.Deserialize<Dictionary<string, object?>>(match.Groups["front"].Value) ?? [];
			meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach ((string key, object? value) in parsed)
			{
				if (value is null) continue;
				if (value is IEnumerable<object> sequence)
				{
					meta[key] = string.Join(",", sequence.Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)));
				}
				else
				{
					meta[key] = value.ToString() ?? "";
				}
			}
		}
		catch (YamlDotNet.Core.YamlException)
		{
			// 保持旧版对简单 frontmatter 的容错：YAML 不完整时按首个冒号切分。
			meta = ParseLegacyFrontmatter(match.Groups["front"].Value);
		}

		string body = match.Groups["body"].Value.Trim();
		Func<string, string> identify;
		lock (ContentMatchGate) identify = MatchSkillId;
		string id = meta.TryGetValue("name", out string? name) && !string.IsNullOrEmpty(name)
			? GuardContentMatch(() => identify(name.ToLowerInvariant()))
			: $"skill_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

		meta.TryGetValue("tags", out string? tagsRaw);
		IReadOnlyList<string> tags = string.IsNullOrWhiteSpace(tagsRaw)
			? ["Skill"]
			: tagsRaw.Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToList();

		return new SkillRecord
		{
			Id = id,
			Name = meta.GetValueOrDefault("name", "未命名技能"),
			Description = meta.GetValueOrDefault("description", "从 SKILL.md 安装的技能"),
			Author = meta.GetValueOrDefault("author", "Online Creator"),
			Version = meta.GetValueOrDefault("version", "1.0.0"),
			Icon = meta.GetValueOrDefault("icon") is {Length: > 0} icon ? icon : "sparkles",
			Tags = tags,
			Category = meta.GetValueOrDefault("category") is {Length: > 0} category ? category : "productivity",
			Tools = ReadTools(meta),
			Instructions = body,
			Enabled = false,
			Source = "url",
			InstalledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Url = url,
		};
	}

	private static bool ContainsSensitiveContent(string text)
	{
		Func<string, bool> match;
		lock (ContentMatchGate) match = MatchSensitiveContent;
		return GuardContentMatch(() => match(text));
	}

	/// <summary>把正则超时转成可诊断的解析失败，避免未处理异常，也不把超时当成合法技能。</summary>
	internal static T GuardContentMatch<T>(Func<T> match)
	{
		try
		{
			return match();
		}
		catch (RegexMatchTimeoutException exception)
		{
			throw new SkillContentTimeoutException(exception);
		}
	}

	private sealed class SkillContentTimeoutException : InvalidOperationException
	{
		public SkillContentTimeoutException(Exception inner) : base(ContentParseTimeoutMessage, inner)
		{
		}
	}

	private static Dictionary<string, string> ParseLegacyFrontmatter(string frontmatter)
	{
		Dictionary<string, string> meta = new(StringComparer.OrdinalIgnoreCase);
		foreach (string line in frontmatter.Split('\n'))
		{
			int colon = line.IndexOf(':');
			if (colon <= 0) continue;
			string key = line[..colon].Trim();
			string value = line[(colon + 1)..].Trim().Trim('"', '\'');
			if (key.Length > 0) meta[key] = value;
		}
		return meta;
	}

	private static IReadOnlyList<string>? ReadTools(Dictionary<string, string> meta)
	{
		if (!meta.TryGetValue("tools", out string? raw) || string.IsNullOrWhiteSpace(raw)) return null;
		string[] tools = raw.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
		return tools.Length == 0 ? null : tools;
	}

	/// <summary>
	/// 构建注入系统提示词的技能指令集
	/// </summary>
	public string BuildSkillsPrompt(IReadOnlySet<string>? availableTools = null) =>
		ComposePrompt(availableTools, cap: true);

	/// <summary>未截断的提示词字符数，供界面判断是否超过预算。</summary>
	public int MeasurePromptCharacters(IReadOnlySet<string>? availableTools = null) =>
		ComposePrompt(availableTools, cap: false).Length;

	private string ComposePrompt(IReadOnlySet<string>? availableTools, bool cap)
	{
		IReadOnlyList<SkillRecord> active = GetEnabled().Where(skill => skill.Enabled).Take(SkillLimits.MaxSkills).ToList();
		if (active.Count == 0) return "";

		List<string> lines = ["【已激活技能与扩展指令 (Active Skills)】："];
		for (int i = 0; i < active.Count; i++)
		{
			SkillRecord skill = SkillLimits.Normalize(active[i]);
			List<string> entry = [$"\n=== 技能 {i + 1}：{skill.Name} (v{skill.Version}) ==="];
			if (!string.IsNullOrEmpty(skill.Description)) entry.Add($"简介: {skill.Description}");
			if (skill.Tools is {Count: > 0})
			{
				IReadOnlyList<string> declared = skill.Tools
					.Where(tool => !string.IsNullOrWhiteSpace(tool))
					.ToList();
				IReadOnlyList<string> available = availableTools is null
					? Array.Empty<string>()
					: declared.Where(availableTools.Contains).ToList();
				IReadOnlyList<string> missing = availableTools is null
					? declared
					: declared.Where(tool => !availableTools.Contains(tool)).ToList();
				string availableText = available.Count == 0
					? "(none)"
					: string.Join(", ", available.Select(SafeToolName));
				entry.Add($"Available tools: {availableText}");
				if (missing.Count > 0)
				{
					string missingLabel = availableTools is null
						? "Unavailable tools (未确认可用)"
						: "Unavailable tools (未注册或已禁用)";
					entry.Add($"{missingLabel}: {string.Join(", ", missing.Select(SafeToolName))}");
				}
			}
			entry.Add(skill.Instructions);
			if (cap)
			{
				string candidate = string.Join("\n", lines.Append(string.Join("\n", entry)));
				if (candidate.Length > SkillLimits.MaxPromptCharacters) break;
			}
			lines.Add(string.Join("\n", entry));
		}
		string text = string.Join("\n", lines);
		return cap ? SkillLimits.Cap(text, SkillLimits.MaxPromptCharacters) : text;
	}

	private static string SafeToolName(string name)
	{
		string singleLine = name.Replace('\r', ' ').Replace('\n', ' ');
		return SkillLimits.Cap(SensitiveDataRedactor.Redact(singleLine), SkillLimits.MaxIdCharacters);
	}

	private void Upsert(SkillRecord skill)
	{
		SkillRecord normalized = SkillLimits.Normalize(skill);
		lock (_gate)
		{
			if (_skills.TryGetValue(normalized.Id, out SkillRecord? existing)
				&& existing.Source == "builtin"
				&& normalized.Source != "builtin")
			{
				throw new InvalidOperationException($"不能覆盖内置技能: {normalized.Id}");
			}
			_skills[normalized.Id] = normalized;
			SaveLocked();
		}
	}

	/// <summary>持久化已安装技能列表 (调用方需持有 _gate)</summary>
	private void SaveLocked()
	{
		string json = JsonSerializer.Serialize(_skills.Values.ToList(), JsonOptions);
		JsonNode? node = JsonNode.Parse(json);
		if (node is not null)
		{
			configStore.Set(ConfigKey, new ConfigValue.Json(node));
		}
	}
}
