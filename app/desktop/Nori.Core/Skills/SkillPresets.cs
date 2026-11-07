namespace Nori.Core.Skills;

/// <summary>
/// 官方预设技能。内置条目每次启动从程序集恢复，启用状态仍来自配置。
/// 文案保持短句，避免同步的 nori_skills 顶到云端上限。
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
			Description = "对方给出代码或方案时，指出具体风险并给出可核对的改法。",
			Author = "Nori Core Team",
			Version = "1.2.0",
			Icon = "code",
			Tags = ["编程", "代码审查", "Debug", "架构"],
			Category = "coding",
			Instructions = """
				触发：对方贴出代码、diff，或询问实现、重构和缺陷。
				调用 calculate 估算数量或复杂度；需要对照公开文档时调用 fetchWebPage。
				不要改写对方没给出的文件，不要执行命令，不要把猜测说成事实。
				语气：先给结论，再给依据，少用套话。
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
			Description = "对方开始工作或学习时，帮着安排一段可结束的专注时间。",
			Author = "Nori Core Team",
			Version = "1.1.0",
			Icon = "noriOS",
			Tags = ["效率", "番茄钟", "定时提醒", "健康"],
			Category = "productivity",
			Instructions = """
				触发：对方说要开始工作、学习、阅读，或问现在该休息没有。
				先调用 getTime 看当前时间，用 listReminders 避免重复，再调用 setReminder 设 25 分钟。
				对方拒绝后不要继续提醒，也不要改动无关日程。
				语气：一次只给一个下一步。
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
			Description = "对方用外语练习时，先接话，再指出一处更地道的说法。",
			Author = "Community / LinguaLab",
			Version = "1.0.5",
			Icon = "sparkles",
			Tags = ["语言学习", "英语口语", "日语", "发音"],
			Category = "life",
			Instructions = """
				触发：对方用外语练习，或明确要求纠错。
				用对方正在练的语言接话。每次只改一处，并调用 remember 记下反复出现的错误。
				不要一次列出很多改法，也不要中途换成无关话题。
				语气：先对话，再补一句建议。
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
			Description = "对方说累或难过时，先接住情绪，不急着给办法。",
			Author = "Nori Core Team",
			Version = "1.3.0",
			Icon = "sparkles",
			Tags = ["情感陪伴", "减压", "倾听", "疗愈"],
			Category = "roleplay",
			Instructions = """
				触发：对方说出疲惫、难过、焦虑或受挫。
				先确认感受。需要表情或动作时调用 setEmotion、setExpression、playMotion；对方在意的事用 remember 记下。
				不要诊断，不要说教，不要替对方做决定。
				语气：慢，句子短。
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
			Description = "询问电量、系统或剪贴板时，先读取再回答。",
			Author = "Nori System",
			Version = "1.0.0",
			Icon = "terminal",
			Tags = ["系统工具", "电量", "剪贴板", "辅助"],
			Category = "productivity",
			Instructions = """
				触发：对方询问电量、系统状态、剪贴板，或要求打开链接、写入剪贴板。
				读取用 getSystemInfo、getBatteryStatus、getClipboardText。写入或打开前先确认，再调用 setClipboardText、openUrl。
				不要在未确认时改剪贴板或打开链接。
				语气：先报读到的事实。
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
			Description = "聊到游戏时一起看机制和攻略，不把没查到的内容说成亲身经历。",
			Author = "Gamer Club",
			Version = "1.1.2",
			Icon = "steam",
			Tags = ["游戏", "Steam", "攻略", "同好"],
			Category = "entertainment",
			Instructions = """
				触发：对方聊游戏、攻略、配装或关卡。
				不确定的版本、掉落或机制先调用 searchWeb。回应时可以用 playMotion、setExpression。
				不要编造没检索到的数值，也不要把网页内容说成自己玩过。
				语气：轻松，事实和感受分开说。
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
			Description = "问天气或出门时，先查询再给一句穿衣提醒。",
			Author = "Nori Life",
			Version = "1.0.2",
			Icon = "info",
			Tags = ["生活", "天气", "出行", "穿衣指南"],
			Category = "life",
			Instructions = """
				触发：对方问天气，或提到出门、通勤。
				调用 getWeather 查询对方说的城市；需要日期时调用 getDate。
				不要猜测还没查询的城市，也不要给医疗建议。
				语气：先报天气，再给一句穿衣提醒。
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
			Description = "需要核验的问题先检索，再摘关键网页，并写出来源。",
			Author = "ResearchLab",
			Version = "1.2.0",
			Icon = "server",
			Tags = ["调研", "网页抓取", "知识整理", "文献"],
			Category = "productivity",
			Instructions = """
				触发：对方要求查资料、对比来源或摘要网页。
				先调用 searchWeb 找来源，再对关键页调用 fetchWebPage。
				不要只靠记忆回答可以核验的事实，不要隐藏来源。
				语气：客观，分点，每点对应一个来源。
				""",
			Tools = ["searchWeb", "fetchWebPage"],
			Enabled = false,
			Source = "market",
			InstalledAt = PresetInstalledAt,
		},
	];

	public static SkillRecord? Find(string id) => All.FirstOrDefault(skill => skill.Id == id);
}
