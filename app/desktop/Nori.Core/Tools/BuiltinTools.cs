using System.Globalization;
using System.Text.Json.Nodes;
using Nori.Core.Emotion;
using Nori.Core.Memory;
using Nori.Core.Network;
using Nori.Core.Proactive;

namespace Nori.Core.Tools;

/// <summary>
/// 内置基础工具注册
///
/// 移植自前端 services/agent/tools 的 registerBuiltinTools:
/// 全部在后端执行, 依赖由 BuiltinToolDeps 注入。工具名与前端协议完全一致,
/// 这是跨语言契约, 不随 C# 命名习惯改。
/// </summary>
public static class BuiltinTools
{
	/// <summary>注册全部内置工具</summary>
	public static void RegisterAll(ToolRegistry registry, BuiltinToolDeps deps)
	{
		// 1. 获取当前时间
		Register(registry, "getTime", "获取当前系统的本地时间 (时:分:秒) 与时区信息", "safe",
			Schema([], []),
			(_, _) =>
			{
				DateTimeOffset now = DateTimeOffset.Now;
				return Task.FromResult<object?>(new
				{
					time = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
					timezone = TimeZoneInfo.Local.Id,
					timestamp = now.ToUnixTimeMilliseconds(),
				});
			});

		// 2. 获取当前日期
		Register(registry, "getDate", "获取当前系统的公历日期与星期几", "safe",
			Schema([], []),
			(_, _) =>
			{
				string[] weekDays = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];
				DateTime now = DateTime.Now;
				return Task.FromResult<object?>(new
				{
					date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					year = now.Year,
					month = now.Month,
					day = now.Day,
					dayOfWeek = weekDays[(int)now.DayOfWeek],
				});
			});

		// 3. 获取系统运行环境
		Register(registry, "getSystemInfo", "获取宿主计算机的操作系统类型、语言与运行状态", "safe",
			Schema([], []),
			async (_, _) => deps.SystemInfo.GetInfo());

		// 4. 控制 Live2D 播放指定动作
		Register(registry, "playMotion",
			"让桌宠 Nori 做出指定的 Live2D 动作 (如打招呼、开心、思考等)", "safe",
			Schema([("name", "动作名称 (motion3.json 文件名，如 smile, wave, think)")], ["name"]),
			(args, _) =>
			{
				string name = RequireString(args, "name");
				IPetActions pet = Require(deps.Pet, "桌宠尚未加载");
				bool played = pet.PlayMotionByName(name);
				return Task.FromResult<object?>(new {success = played, played = played ? name : null, available = played ? null : pet.MotionNames});
			});

		// 5. 控制 Live2D 切换表情
		Register(registry, "setExpression",
			"改变桌宠 Nori 的脸部表情", "safe",
			Schema([("name", "表情名称 (如 Smile, Shy, Angry, Surprised)")], ["name"]),
			(args, _) =>
			{
				string name = RequireString(args, "name");
				IPetActions pet = Require(deps.Pet, "桌宠尚未加载");
				bool played = pet.PlayExpression(name);
				return Task.FromResult<object?>(new {success = played, expression = played ? name : null, available = played ? null : pet.ExpressionNames});
			});

		// 6. 记住重要事实 / 偏好 (remember 与 addMemory 同体)
		RegisterRemember(registry, deps);
		RegisterForget(registry, deps);
		registry.RegisterAlias("remember", "addMemory", "添加一条长期记忆到记忆库 (remember 的别名)");

		// 7. 搜索长期记忆
		Register(registry, "searchMemory",
			"在长期记忆库中通过语义向量和关键词搜索与特定内容相关的历史记忆条目", "safe",
			Schema([("keyword", "搜索关键词或语义查询句")], ["keyword"]),
			async (args, _) =>
			{
				string keyword = RequireString(args, "keyword");
				var results = await deps.Memory.SearchHybridAsync(keyword, 10);
				return new {results};
			});

		// 8. 改变自身情绪
		Register(registry, "setEmotion",
			"主动调整 Nori 当前的心情与情绪状态", "safe",
			Schema(
			[
				("emotion", "情绪类型"),
				("intensity", "情绪强烈程度 (0.0 ~ 1.0)"),
			], ["emotion"]),
			(args, _) =>
			{
				string emotion = OptionalString(args, "emotion") ?? EmotionTypes.Neutral;
				if (!EmotionTypes.IsValid(emotion)) throw new InvalidOperationException($"未知的情绪类型: {emotion}");
				double intensity = OptionalNumber(args, "intensity") ?? 0.8;
				deps.Emotion.SetEmotion(emotion, intensity);
				return Task.FromResult<object?>(new {success = true, emotion, intensity});
			});

		// 9. 设置定时提醒
		Register(registry, "setReminder",
			"设置一个定时提醒倒计时任务，到时间后 Nori 会主动提醒主人", "safe",
			Schema(
			[
				("content", "提醒内容事项 (如: 喝水、站起来活动一下)"),
				("delayMinutes", "多少分钟后触发提醒"),
			], ["content", "delayMinutes"]),
			(args, _) =>
			{
				string content = RequireString(args, "content");
				double minutes = OptionalNumber(args, "delayMinutes")
					?? throw new InvalidOperationException("缺少参数: delayMinutes");
				ReminderItem item = deps.Proactive.AddReminder(content, minutes);
				return Task.FromResult<object?>(new {success = true, reminderId = item.Id, triggerInMinutes = minutes});
			});

		// 10. 列出所有正在生效的提醒
		Register(registry, "listReminders", "查看当前所有排队中的定时提醒事项列表", "safe",
			Schema([], []),
			(_, _) => Task.FromResult<object?>(new {reminders = deps.Proactive.ListReminders()}));

		// 11. 读取剪贴板文本
		Register(registry, "getClipboardText", "读取操作系统当前剪贴板中的纯文本内容", "confirm",
			Schema([], []),
			async (_, ctx) =>
			{
				IClipboardOps clipboard = Require(deps.Clipboard, "当前环境不支持读取剪贴板");
				string text = await clipboard.GetTextAsync(ctx.CancellationToken);
				return new {text};
			});

		// 12. 写入剪贴板文本
		Register(registry, "setClipboardText", "将指定文本写入操作系统剪贴板", "confirm",
			Schema([("text", "要写入剪贴板的文本内容")], ["text"]),
			async (args, ctx) =>
			{
				string text = RequireString(args, "text");
				IClipboardOps clipboard = Require(deps.Clipboard, "当前环境不支持写入剪贴板");
				await clipboard.SetTextAsync(text, ctx.CancellationToken);
				return new {success = true, length = text.Length};
			});

		// 13. 打开外部网页链接
		Register(registry, "openUrl", "使用默认浏览器打开指定的网络链接", "confirm",
			Schema([("url", "需要打开的完整网址 (如 https://...)")], ["url"]),
			(args, _) =>
			{
				string url = RequireString(args, "url");
				if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https"))
				{
					throw new InvalidOperationException($"不允许打开的链接: {url}");
				}
				Action<string> open = deps.OpenUrl ?? throw new InvalidOperationException("当前环境不支持打开链接");
				open(parsed.ToString());
				return Task.FromResult<object?>(new {success = true, opened = parsed.ToString()});
			});

		// 14. 获取电池电量状态
		Register(registry, "getBatteryStatus", "获取计算机当前电池电量百分比与充电状态", "safe",
			Schema([], []),
			(_, _) => Task.FromResult<object?>(
				deps.SystemInfo.GetBatteryStatus() ?? new {supported = false, message = "设备不支持或为台式机电源"}));

		// 15. AnySearch 网络搜索 (searchWeb 与 anySearch 同体)
		RegisterSearch(registry, deps);
		registry.RegisterAlias("searchWeb", "anySearch", "调用 AnySearch 专属 API 执行精准网络、技术代码与文档搜索");

		// 16. 天气查询
		Register(registry, "getWeather",
			"查询指定城市当天的实时天气、温度与天气状况", "safe",
			Schema([("city", "城市名称 (如: 北京, 上海, 广州, 东京, 纽约)")], ["city"]),
			async (args, ctx) =>
			{
				string city = OptionalString(args, "city")
					?? throw new InvalidOperationException("缺少参数: city");
				CancellationToken ct = ctx.CancellationToken;
				Uri weatherUri = new($"https://wttr.in/{Uri.EscapeDataString(city)}?format=j1");
				using HttpResponseMessage response = await UrlAccessPolicy.GetWithSafeRedirectsAsync(
					deps.Http, weatherUri, allowPrivate: false, cancellationToken: ct);
				if (!response.IsSuccessStatusCode)
				{
					throw new InvalidOperationException($"天气查询失败: HTTP {(int)response.StatusCode}");
				}
				JsonNode? data = JsonNode.Parse(await UrlAccessPolicy.ReadCappedTextAsync(
					response.Content, UrlAccessPolicy.MaxResponseBytes, ct));
				JsonNode? current = data?["current_condition"]?[0];
				if (current is null)
				{
					throw new InvalidOperationException("天气服务未返回实时数据");
				}
				return new
				{
					city,
					temp_C = current["temp_C"]?.GetValue<string>() ?? "",
					condition = current["lang_zh"]?[0]?["value"]?.GetValue<string>()
						?? current["weatherDesc"]?[0]?["value"]?.GetValue<string>()
						?? "",
					humidity = current["humidity"]?.GetValue<string>() ?? "",
					windspeedKmph = current["windspeedKmph"]?.GetValue<string>() ?? "",
				};
			});

		// 17. 数学表达式安全计算
		Register(registry, "calculate",
			"计算数学算式与数值计算 (支持加减乘除、乘方、三角函数、对数、常量与百分比)", "safe",
			Schema([("expression", "数学表达式 (如: 128 * 64, sqrt(256), sin(pi/2), 15% * 200)")], ["expression"]),
			(args, _) =>
			{
				string expression = RequireString(args, "expression");
				try
				{
					double result = MathExpression.Calculate(expression);
					return Task.FromResult<object?>(new {expression, result});
				}
				catch (Exception error)
				{
					throw new InvalidOperationException($"计算表达式 \"{expression}\" 失败: {error.Message}");
				}
			});

		// 18. 获取网页内容摘要
		Register(registry, "fetchWebPage", "抓取并提取指定公开网址的网页文本正文内容", "confirm",
			Schema([("url", "网页完整 URL 地址")], ["url"]),
			async (args, ctx) => await deps.Fetcher.FetchAsync(RequireString(args, "url"), ctx.CancellationToken));
	}

	/// <summary>构造并注册工具的小助手</summary>
	private static void Register(
		ToolRegistry registry,
		string name,
		string description,
		string permissionLevel,
		JsonObject parameters,
		Func<JsonNode?, ToolContext, Task<object?>> execute) =>
		registry.Register(new RegisteredTool
		{
			Name = name,
			Description = description,
			Parameters = parameters,
			PermissionLevel = permissionLevel,
			Execute = execute,
		});

	private static void RegisterRemember(ToolRegistry registry, BuiltinToolDeps deps)
	{
		Register(registry, "remember",
			"在对话中获知主人的个人信息、喜好、称呼、习惯或重要约定后，主动记录到长期记忆库中", "safe",
			Schema(
			[
				("content", "记忆内容事实描述 (如: 主人最喜欢的咖啡是冰美式 / 主人的生日是 8月20日)"),
				("importance", "重要程度 (0.1 ~ 1.0, 默认为 0.8)"),
				("tags", "标签分类 (可选, 如: 偏好, 姓名, 习惯, 约定)"),
				("kind", "记忆类型 (可选: identity, preference, factual, relational, episodic, planned)"),
			], ["content"]),
			async (args, _) =>
			{
				string content = RequireString(args, "content");
				double importance = OptionalNumber(args, "importance") ?? 0.8;
				string? tags = OptionalString(args, "tags");
				MemoryKind? kind = OptionalString(args, "kind") is { } kindText ? MemoryKindExtensions.Parse(kindText) : null;
				MemoryKind resolvedKind = kind ?? MemoryKind.Factual;
				Memory.MemoryItem item = await deps.Memory.RememberAsync(content, resolvedKind, importance, tags, "agent");
				return new {success = true, memory = item};
			});
	}

	private static void RegisterForget(ToolRegistry registry, BuiltinToolDeps deps)
	{
		Register(registry, "forgetMemory",
			"在用户明确要求忘记某条长期记忆后，将它归档而不是永久删除", "confirm",
			Schema([("memoryId", "要归档的记忆 id")], ["memoryId"]),
			(args, _) =>
			{
				long id = (long)(OptionalNumber(args, "memoryId") ?? throw new InvalidOperationException("缺少参数: memoryId"));
				bool archived = deps.Memory.Archive(id);
				return Task.FromResult<object?>(new {success = archived, memoryId = id});
			});
	}

	private static void RegisterSearch(ToolRegistry registry, BuiltinToolDeps deps)
	{
		Register(registry, "searchWeb",
			"使用 AnySearch 搜索引擎在互联网上搜索特定关键词、技术文档、新闻与实时信息", "safe",
			Schema(
			[
				("query", "搜索关键词或查询短句 (例如: 'Go 1.26 release notes')"),
				("tag", "搜索分类标签 (可选，例如: 'code.doc', 'web', 'general', 'news')"),
			], ["query"]),
			async (args, ctx) =>
			{
				string query = RequireString(args, "query");
				string tag = OptionalString(args, "tag") ?? "general";
				CancellationToken ct = ctx.CancellationToken;

				// 端点/凭据绑定由策略决定: 存储密钥只允许发往官方端点, 自定义端点必须显式携带 key
				AnySearchRequest resolved = AnySearchRequestPolicy.Resolve(
					OptionalString(args, "endpoint") ?? deps.Config.Get("anysearch_api_base")?.ToStorage(),
					OptionalString(args, "apiKey"),
					deps.Config.Get("anysearch_api_key")?.ToStorage());
				UrlAccessPolicy.EnsurePublicHttp(resolved.Endpoint);

				JsonObject payload = new()
				{
					["query"] = query,
					["tag"] = tag,
				};

				using HttpRequestMessage httpRequest = new(HttpMethod.Post, resolved.Endpoint)
				{
					Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
				};
				if (!string.IsNullOrEmpty(resolved.ApiKey))
				{
					httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resolved.ApiKey);
				}

				HttpResponseMessage response;
				try
				{
					response = await deps.Http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
				}
				catch (Microsoft.Security.AntiSSRF.AntiSSRFException exception)
				{
					throw UrlAccessPolicy.Translate(exception, resolved.Endpoint);
				}
				catch (HttpRequestException exception)
				{
					throw UrlAccessPolicy.Translate(exception, resolved.Endpoint);
				}
				using (response)
				{
					string body = await UrlAccessPolicy.ReadCappedTextAsync(
						response.Content, UrlAccessPolicy.MaxResponseBytes, ct);
					if (!response.IsSuccessStatusCode)
					{
						throw new HttpRequestException($"AnySearch API 返回 HTTP {(int)response.StatusCode}: {body}");
					}
					return JsonNode.Parse(body) ?? body;
				}
			});
	}

	/// <summary>构造对象参数 Schema</summary>
	private static JsonObject Schema(IReadOnlyList<(string Name, string Description)> properties, IReadOnlyList<string> required)
	{
		JsonObject props = new();
		foreach ((string name, string description) in properties)
		{
			props[name] = new JsonObject
			{
				["type"] = name == "importance" || name == "intensity" || name == "delayMinutes"
					|| name == "params" ? (name == "params" ? "object" : "number") : "string",
				["description"] = description,
			};
		}
		JsonArray requiredArray = new();
		foreach (string name in required) requiredArray.Add(name);
		return new JsonObject
		{
			["type"] = "object",
			["properties"] = props,
			["required"] = requiredArray,
		};
	}

	private static T Require<T>(T? value, string message) where T : class =>
		value ?? throw new InvalidOperationException(message);

	private static string RequireString(JsonNode? args, string name)
	{
		string? value = null;
		if (args?[name] is JsonValue text && text.TryGetValue(out string? parsed))
		{
			value = parsed;
		}
		if (string.IsNullOrEmpty(value)) throw new InvalidOperationException($"缺少参数: {name}");
		return value;
	}

	private static string? OptionalString(JsonNode? args, string name) =>
		args?[name] is JsonValue text && text.TryGetValue(out string? parsed) ? parsed : null;

	private static double? OptionalNumber(JsonNode? args, string name) =>
		args?[name] is JsonValue num && num.TryGetValue(out double parsed) ? parsed : null;
}
