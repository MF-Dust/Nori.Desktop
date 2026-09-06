namespace Nori.Core.Skills;

/// <summary>
/// 官方精选 / 预设技能市场库 (Skills Hub Catalog)
/// 移植自前端 services/skills/presets.ts。
/// </summary>
public static class SkillPresets
{
	private const long PresetInstalledAt = 1700000000000;

	public static IReadOnlyList<SkillRecord> All { get; } =
	[
		new SkillRecord
		{
			Id = "code-reviewer",
			Name = "代码审查与架构顾问",
			Description = "具备资深架构师与全栈研发能力，提供精准的代码重构建议、边界安全审查与 Bug 排查",
			Author = "Nori Core Team",
			Version = "1.2.0",
			Icon = "code",
			Tags = ["编程", "代码审查", "Debug", "架构"],
			Category = "coding",
			Instructions = """
				【技能：代码审查与架构顾问】
				1. 当对方提供代码片段或询问技术方案时，以资深工程师的严谨视角进行代码分析。
				2. 优先指出潜在的空指针、边界越界、资源未释放或竞态条件等隐患。
				3. 给出优雅简洁的重构示范，并解释修改理由。
				4. 语言精炼专业，避免冗余套话。
				""",
			Tools = ["calculate", "fetchWebPage"],
			Enabled = true,
			Source = "builtin",
			InstalledAt = PresetInstalledAt,
		},
		new SkillRecord
		{
			Id = "pomodoro-master",
			Name = "专注番茄钟与日程管家",
			Description = "智能规划工作学习节奏，主动开启 25 分钟番茄钟提醒，监督健康作息",
			Author = "Nori Core Team",
			Version = "1.1.0",
			Icon = "noriOS",
			Tags = ["效率", "番茄钟", "定时提醒", "健康"],
			Category = "productivity",
			Instructions = """
				【技能：专注番茄钟与日程管家】
				1. 当对方表示要开始工作、写代码、阅读或学习时，主动询问是否需要设置 25 分钟番茄钟 (调用 setReminder)。
				2. 番茄钟结束后，提醒对方站起来活动、喝水或眺望远方 (5 分钟休息)。
				3. 在长达数小时的高强度对话后，贴心提醒注意眼睛疲劳。
				""",
			Tools = ["setReminder", "listReminders", "getTime"],
			Enabled = true,
			Source = "builtin",
			InstalledAt = PresetInstalledAt,
		},
		new SkillRecord
		{
			Id = "language-coach",
			Name = "多语言口语私教",
			Description = "支持中英日韩等母语级口语陪练，指出用词地道性并提供发音建议",
			Author = "Community / LinguaLab",
			Version = "1.0.5",
			Icon = "sparkles",
			Tags = ["语言学习", "英语口语", "日语", "发音"],
			Category = "life",
			Instructions = """
				【技能：多语言口语私教】
				1. 当对方使用外语（如英语、日语）对话时，以对应语言自然对话交流。
				2. 在每次回答末尾，用温馨括号给出针对刚才发言的 1~2 处更地道表达或语法优化建议。
				3. 鼓励多说多练，保持积极轻松的氛围。
				""",
			Tools = ["remember"],
			Enabled = false,
			Source = "market",
			InstalledAt = PresetInstalledAt,
		},
		new SkillRecord
		{
			Id = "soul-healer",
			Name = "温情倾听与心理减压",
			Description = "具备极高的同理心与温柔语气，倾听对方的疲惫与压力，提供暖心陪伴",
			Author = "Nori Core Team",
			Version = "1.3.0",
			Icon = "sparkles",
			Tags = ["情感陪伴", "减压", "倾听", "疗愈"],
			Category = "roleplay",
			Instructions = """
				【技能：温情倾听与心理减压】
				1. 当对方表达疲倦、难过、焦虑或挫折时，优先给予真诚的情感确认与共情，不急于给出说教式建议。
				2. 使用温柔、亲切且充满支持力量的语气，并联动 Live2D 表情 (如 Shy, Smile) 和轻柔动作。
				3. 主动调用 remember 工具记录情绪偏好与关切事物。
				""",
			Tools = ["setEmotion", "setExpression", "playMotion", "remember"],
			Enabled = true,
			Source = "builtin",
			InstalledAt = PresetInstalledAt,
		},
		new SkillRecord
		{
			Id = "desktop-admin",
			Name = "极客桌面系统管家",
			Description = "实时探查电脑系统电量、网络、分辨率与剪贴板，快速执行桌面辅助操作",
			Author = "Nori System",
			Version = "1.0.0",
			Icon = "terminal",
			Tags = ["系统工具", "电量", "剪贴板", "辅助"],
			Category = "productivity",
			Instructions = """
				【技能：极客桌面系统管家】
				1. 当对方询问当前电量、系统状态或剪贴板内容时，主动调用 getSystemInfo、getBatteryStatus 或 getClipboardText 获取精准信息。
				2. 当需要复制特定内容时，主动调用 setClipboardText 并告知已完成复制。
				""",
			Tools = ["getSystemInfo", "getBatteryStatus", "getClipboardText", "setClipboardText", "openUrl"],
			Enabled = true,
			Source = "builtin",
			InstalledAt = PresetInstalledAt,
		},
		new SkillRecord
		{
			Id = "gaming-partner",
			Name = "游戏同好与攻略交流",
			Description = "熟知 Steam 热门独立游戏与主机大作，畅聊游戏剧情与配装策略",
			Author = "Gamer Club",
			Version = "1.1.2",
			Icon = "steam",
			Tags = ["游戏", "Steam", "攻略", "同好"],
			Category = "entertainment",
			Instructions = """
				【技能：游戏同好与攻略交流】
				1. 当聊到游戏话题时，用活泼热情的语气交流游戏感受、梗与通关技巧。
				2. 遇到冷门策略或 Boss 机制时，主动调用 searchWeb 检索最新攻略并归纳关键要点。
				3. 配合活泼的 Live2D 动作 (如 wave, smile) 增强游戏互动乐趣。
				""",
			Tools = ["searchWeb", "playMotion", "setExpression"],
			Enabled = false,
			Source = "market",
			InstalledAt = PresetInstalledAt,
		},
		new SkillRecord
		{
			Id = "weather-concierge",
			Name = "晨间天气与出行秘书",
			Description = "精准查询全球城市实时天气与温差变化，主动给出出行防晒与增减衣物贴士",
			Author = "Nori Life",
			Version = "1.0.2",
			Icon = "info",
			Tags = ["生活", "天气", "出行", "穿衣指南"],
			Category = "life",
			Instructions = """
				【技能：晨间天气与出行秘书】
				1. 当询问天气或提到出门计划时，主动调用 getWeather 查询目标城市的实时气温、天气状况与湿度。
				2. 结合当地具体天气，主动给出贴心的出行防风、防雨或防晒穿衣建议。
				""",
			Tools = ["getWeather", "getDate"],
			Enabled = true,
			Source = "builtin",
			InstalledAt = PresetInstalledAt,
		},
		new SkillRecord
		{
			Id = "deep-researcher",
			Name = "深度网络调研与文献摘要",
			Description = "擅长全网信息检索、抓取长篇网页正文并提炼结构化知识点",
			Author = "ResearchLab",
			Version = "1.2.0",
			Icon = "server",
			Tags = ["调研", "网页抓取", "知识整理", "文献"],
			Category = "productivity",
			Instructions = """
				【技能：深度网络调研与文献摘要】
				1. 当提出较为复杂的调研、概念解释或新闻追踪需求时，先用 searchWeb 搜索多方来源。
				2. 对关键网页调用 fetchWebPage 提取正文，并整理出清晰的要点对比与总结。
				3. 保持客观中立，注明信息来源。
				""",
			Tools = ["searchWeb", "fetchWebPage"],
			Enabled = false,
			Source = "market",
			InstalledAt = PresetInstalledAt,
		},
	];

	public static SkillRecord? Find(string id) => All.FirstOrDefault(skill => skill.Id == id);
}
