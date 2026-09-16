namespace Nori.Core.Cloud;

/// <summary>
/// 云存档的范围 —— 这份清单就是**隐私边界本身**。
///
/// **用白名单，不用黑名单。** 黑名单意味着以后任何人加一个配置键，它会默认被传上云；
/// 加的要是一个密钥或者一条本机路径，没有人会收到通知，也不会报错。白名单反过来：
/// 新键默认留在本机，要同步必须有人来这里加一行 —— 那一行就是审阅的抓手。
///
/// （这条方向和 llm-api 那个代理里的判断是反的。那边是转发别人的请求，认不得的字段
/// 该放行，漏掉一个就是功能坏掉；这边是把用户的数据往外发，认不得的字段该扣住，
/// 漏掉一个就是隐私事故。两处的默认值取决于「出错时谁受损」。）
///
/// 用户选的范围是「偏好 + 记忆，不含对话原文」，所以下面三类各有各的理由：
/// </summary>
public static class CloudSaveScope
{
	/// <summary>
	/// 要同步的配置键。
	///
	/// 收录标准是「换一台机器之后，不带它就得重新配一遍，而带它不会造成危害」。
	/// </summary>
	public static IReadOnlySet<string> ConfigKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
	{
		// 界面与表现：纯偏好，跨机器就该一样。
		"language",
		"selected_model",
		"nori_skills",
		"audio_volume",
		"tts_auto_play",
		"ui_sidebar_collapsed",

		// 形象的显示参数。换机器之后不带它，她的大小位置全得重调。
		"l2d_scale", "l2d_opacity", "l2d_render_scale", "l2d_shadow",
		"l2d_quality_mode", "l2d_click_through", "l2d_ai_interaction_enabled",

		// 主动交互的节奏。
		"proactive_daily_greeting", "proactive_idle_enabled", "proactive_idle_minutes",

		// 记忆引擎的调参。它们决定她怎么记事，属于「她是什么样」的一部分。
		"memory_enabled", "memory_recall_top_k", "memory_min_similarity",
		"memory_decay_enabled", "memory_archive_enabled", "memory_archive_threshold",
		"memory_reflection_enabled", "memory_reflection_rounds", "memory_reflection_min_chars",

		// 语音的音色与语速。**不含 base_url 与任何密钥** —— 见下面那份排除理由。
		"tts_provider", "tts_voice", "tts_speed", "tts_model",
	};

	/// <summary>
	/// 明确**不**同步的那些，以及为什么。
	///
	/// 这份清单不参与运行逻辑（白名单已经决定了一切），它存在是为了让下一个来加键的人
	/// 先读到这些理由 —— 以及让测试能断言它们确实不在白名单里。
	/// </summary>
	public static IReadOnlyDictionary<string, string> ExcludedWithReason { get; } =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["llm_api_key"] = "密钥。而且它是用本机 DPAPI 绑定的密钥加密的，传过去也解不开。",
			["tts_api_key"] = "密钥，同样是本机加密的。",
			["stt_api_key"] = "密钥，同样是本机加密的。",
			["llm_api_base"] = "可能是内网或私有代理地址，属于这台机器的环境而不是用户的偏好。",
			["tts_base_url"] = "语音合成的地址常指向本机或内网服务。",
			["stt_base_url"] = "语音识别的地址同理。",
			["workspace_root"] = "本机路径。同步过去在另一台机器上根本不存在。",
			["workspace_tasks"] = "里面是具体的 shell 命令，既依赖本机环境，也属于敏感面。",
			["permission_gear"] = "授权档位是**这台机器上**的安全决定，不该跟着账户跑。",
			["permission_bypass_until"] = "完全放行的到期时刻，本来就只有四小时，跨机器毫无意义。",
			["automation_enabled"] = "自动化开关同属安全面，一台机器上放开不等于另一台也放开。",
			["automation_allow_pointer"] = "接管鼠标是逐台机器授予的。",
			["automation_allow_keyboard"] = "接管键盘是逐台机器授予的。",
			["screen_reading_enabled"] = "读屏是每台机器单独授予的权限。",
			["telemetry_consent"] = "同意本身不能被同步 —— 在这台机器上同意过，不代表在新机器上也同意。",
			["toast_approvals"] = "它会往机器上写开始菜单快捷方式与注册表项，属于本机安装状态。",
			["audio_backend"] = "为某台机器的声卡问题选的退路，跟着走反而会把问题带过去。",
			["pet_window_x"] = "屏幕分辨率不同，位置搬过去可能落在屏幕外。",
			["pet_window_y"] = "同一个道理，纵坐标也可能落在屏幕外。",
			["installed_at"] = "这台机器什么时候装的，是本机事实，不是用户的偏好。",
			["app_version"] = "这台机器上跑的是哪个版本，同步过去会盖掉新机器的真实版本。",
			["first_run_completed"] = "每台机器各自走一次首次运行。",
			["config_schema_version"] = "本机数据库结构版本。",

			// 登录态。白名单本来就挡住了它们，写在这里是为了让下一个想同步账户的人
			// 先读到理由 —— 以及让上面那条「排除理由与白名单不矛盾」把它们钉住。
			[AccountSession.TokenKey] = "会话令牌。同步它等于把登录态搬到另一台机器；而且它用本机密钥加密，传过去也解不开。",
			[AccountSession.EmailKey] = "登录的是哪个账户属于这台机器的状态。何况存档本身就是从那个账户取回来的。",
			[AccountSession.NameKey] = "跟着上面那条走。",
			[AccountSession.ExpiresKey] = "这台机器上这一个会话的到期时刻，对另一台没有意义。",
			[AccountSession.MethodKey] = "本机上次用的登录方式。它刻意只记在本机 —— 见 SignInForm 关于账号枚举的说明。",
			[NoriCloudClient.BaseUrlKey] = "指向某个具体部署，跟着账户跑会让另一台机器连到错误的服务端。",
			[CloudSyncService.RevisionKey] = "本机最后一次见到的云端版本号。同步它等于让另一台机器以为自己已经见过某个版本，冲突检测就此失效。",
		};

	/// <summary>
	/// 要同步的数据表。
	///
	/// <c>chat_messages</c> **不在其中** —— 用户选的就是「不含对话原文」：它是整个库里
	/// 最敏感、体量也最大的东西，而换机器并不需要它（她记住的事在 memories 里）。
	/// <c>knowledge_chunks</c> 也不同步：那是本地文档算出来的向量，源文件不在新机器上，
	/// 搬过去只是一堆指向不存在文件的索引。
	/// </summary>
	public static IReadOnlySet<string> Tables { get; } = new HashSet<string>(StringComparer.Ordinal)
	{
		"memories",
		"reminders",
	};

	/// <summary>某个配置键在不在同步范围内。</summary>
	public static bool IncludesConfig(string key) => ConfigKeys.Contains(key);

	/// <summary>
	/// 形象相关的配置是按模型分键存的（<c>l2d_scale_nori</c>）。
	///
	/// 白名单只列了基名，这里认出带后缀的那些 —— 否则用户调过的每一个形象的大小
	/// 都同步不上去，而那正是这一项存在的理由。
	/// </summary>
	public static bool IncludesConfigWithModelSuffix(string key)
	{
		if (ConfigKeys.Contains(key)) return true;
		foreach (string prefix in ConfigKeys)
		{
			if (!prefix.StartsWith("l2d_", StringComparison.Ordinal)) continue;
			if (key.Length > prefix.Length + 1
				&& key.StartsWith(prefix, StringComparison.Ordinal)
				&& key[prefix.Length] == '_') return true;
		}
		return false;
	}
}
