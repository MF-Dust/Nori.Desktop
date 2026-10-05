using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nori.Core.Agent;

/// <summary>
/// 流式 JSON / Markdown 解析器
///
/// 移植自前端 services/agent/jsonParser.ts: 处理 LLM 输出的 ```json 代码块包裹、
/// 分段 JSON 对象以及普通文本兜底。
///
/// 扫描状态 (游标、括号深度、字符串/转义态) 在多次 push 之间保持,
/// 每个 chunk 只扫描新增文本, 未闭合对象不会被反复从头扫描;
/// 单个未闭合 payload 超过上限时抛出可处理错误而不是无限占用内存。
/// </summary>
public sealed class StreamingJsonParser
{
	/// <summary>默认的未完成缓冲区上限 (字符数)</summary>
	public const int DefaultMaxPendingBuffer = 1_000_000;

	private readonly int _maxPendingBuffer;
	private string _buffer = "";
	/** 下一次扫描的起点: 已确认为垃圾前缀或已消费的部分不再重复扫描 */
	private int _scanIndex;
	private int _jsonStartIndex = -1;
	private int _braceDepth;
	private bool _inString;
	private bool _isEscaped;

	/// <summary>单个未闭合 payload 的上限, 测试可用小值覆盖</summary>
	public StreamingJsonParser(int maxPendingBuffer = DefaultMaxPendingBuffer)
	{
		if (maxPendingBuffer <= 0) throw new ArgumentOutOfRangeException(nameof(maxPendingBuffer), "maxPendingBuffer 必须为正数");
		_maxPendingBuffer = maxPendingBuffer;
	}

	/// <summary>
	/// 追加流式文本分片并尝试解析出完整的 Agent 协议对象
	/// </summary>
	public IReadOnlyList<AgentProtocolItem> Push(string chunk)
	{
		_buffer += chunk;
		List<AgentProtocolItem> results = ExtractAvailableObjects();
		// 上限针对“未完成”的输入: 未闭合对象从起点计, 普通垃圾前缀按全量计
		int pendingSize = HasOpenObject()
			? _buffer.Length - Math.Max(_jsonStartIndex, 0)
			: _buffer.Length;
		if (pendingSize > _maxPendingBuffer)
		{
			Reset();
			throw new InvalidOperationException($"流式解析缓冲区超过上限 ({_maxPendingBuffer} 字符)，已终止当前输入");
		}
		return results;
	}

	/// <summary>
	/// 流结束时刷新剩余内容
	/// </summary>
	public IReadOnlyList<AgentProtocolItem> Flush()
	{
		List<AgentProtocolItem> objects = ExtractAvailableObjects();
		string remaining = _buffer.Trim();

		if (remaining.Length > 0)
		{
			// 尝试最后一次完整解析
			AgentProtocolItem? parsed = TryParseObject(remaining);
			if (parsed is not null)
			{
				objects.Add(parsed);
			}
			else
			{
				// 若不是合法 JSON，清理掉代码块标记后作为纯文本消息兜底
				string cleanedText = System.Text.RegularExpressions.Regex.Replace(remaining, @"^```(json)?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
				cleanedText = System.Text.RegularExpressions.Regex.Replace(cleanedText, @"\s*```$", "").Trim();

				if (cleanedText.Length > 0)
				{
					objects.Add(new ProtocolMessage(cleanedText, null, null, null));
				}
			}
		}

		Reset();
		return objects;
	}

	/// <summary>
	/// 重置解析器状态
	/// </summary>
	public void Reset()
	{
		_buffer = "";
		_scanIndex = 0;
		_jsonStartIndex = -1;
		_braceDepth = 0;
		_inString = false;
		_isEscaped = false;
	}

	/// <summary>是否存在尚未闭合的对象 (用于超限判定)</summary>
	private bool HasOpenObject() => _jsonStartIndex != -1 || _braceDepth > 0;

	/// <summary>
	/// 从上次扫描位置继续提取所有闭合的 JSON 对象.
	///
	/// 只扫描新增区间; 提取完整对象后消费已确认前缀并按需复位状态,
	/// 同一调用内可以连续输出多个对象。
	/// </summary>
	private List<AgentProtocolItem> ExtractAvailableObjects()
	{
		List<AgentProtocolItem> results = [];
		int i = _scanIndex;

		while (i < _buffer.Length)
		{
			char ch = _buffer[i];

			if (_inString)
			{
				if (_isEscaped)
				{
					_isEscaped = false;
				}
				else if (ch == '\\')
				{
					_isEscaped = true;
				}
				else if (ch == '"')
				{
					_inString = false;
				}
				i++;
				continue;
			}

			if (ch == '"')
			{
				_inString = true;
				i++;
				continue;
			}

			if (ch == '{')
			{
				if (_braceDepth == 0)
				{
					_jsonStartIndex = i;
				}
				_braceDepth++;
			}
			else if (ch == '}')
			{
				_braceDepth--;
				if (_braceDepth == 0 && _jsonStartIndex != -1)
				{
					string jsonStr = _buffer.Substring(_jsonStartIndex, i + 1 - _jsonStartIndex);
					AgentProtocolItem? parsed = TryParseObject(jsonStr);
					if (parsed is not null)
					{
						results.Add(parsed);
						// 消费掉已解析部分与之前的垃圾前缀
						_buffer = _buffer[(i + 1)..];
						i = -1;
						_scanIndex = 0;
					}
					// 解析失败时保留原文交给 flush 兜底, 只复位状态继续扫描;
					// 该区间位于 scanIndex 之前, 后续 push 不会重复扫描
					_braceDepth = 0;
					_jsonStartIndex = -1;
				}
				else if (_braceDepth < 0)
				{
					// 括号失配纠正: 该字符视为普通文本
					_braceDepth = 0;
					_jsonStartIndex = -1;
				}
			}

			i++;
		}

		// 记录已扫描到的位置: 未闭合对象的中间部分在后续 push 中不会重扫
		_scanIndex = i;
		return results;
	}

	/// <summary>
	/// 尝试将字符串解析为合法 Agent 协议对象
	/// </summary>
	private AgentProtocolItem? TryParseObject(string raw)
	{
		string trimmed = raw.Trim();
		if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return null;

		try
		{
			using JsonDocument document = JsonDocument.Parse(trimmed);
			JsonElement root = document.RootElement.Clone();
			if (root.ValueKind != JsonValueKind.Object) return null;

			// 1. message 类型
			string? type = root.TryGetProperty("type", out JsonElement typeElem) && typeElem.ValueKind == JsonValueKind.String ? typeElem.GetString() : null;
			bool hasText = root.TryGetProperty("text", out JsonElement textElem) && textElem.ValueKind == JsonValueKind.String;
			if (type == "message" || hasText)
			{
				return new ProtocolMessage(
					hasText ? textElem.GetString() ?? "" : "",
					GetStringOrNull(root, "emotion"),
					GetStringOrNull(root, "expression"),
					GetStringOrNull(root, "action") ?? GetStringOrNull(root, "l2dAction"));
			}

			// 2. tool_call 类型
			if (type == "tool_call"
				&& root.TryGetProperty("name", out JsonElement nameElem)
				&& nameElem.ValueKind == JsonValueKind.String)
			{
				JsonNode? args = JsonNode.Parse("{}");
				if (root.TryGetProperty("arguments", out JsonElement argElem))
				{
					if (argElem.ValueKind == JsonValueKind.Object)
					{
						args = JsonNode.Parse(argElem.GetRawText());
					}
					else if (argElem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(argElem.GetString()))
					{
						// 部分模型会把 arguments 输出为 JSON 字符串, 尝试二次解析
						try
						{
							using JsonDocument inner = JsonDocument.Parse(argElem.GetString()!);
							if (inner.RootElement.ValueKind == JsonValueKind.Object)
							{
								args = JsonNode.Parse(inner.RootElement.GetRawText());
							}
						}
						catch
						{
							/* 忽略无效参数 JSON, 保持空对象 */
						}
					}
				}

				return new ProtocolToolCall(
					// 缺少编号时保留空值，让执行跟踪按名称与参数去重；时间戳会碰撞也会破坏重复查询复用。
					GetStringOrNull(root, "id") ?? "",
					nameElem.GetString()!,
					args);
			}

			// 3. event 类型
			if (type == "event" && GetStringOrNull(root, "name") is { Length: > 0 } eventName)
			{
				JsonNode? payload = null;
				if (root.TryGetProperty("payload", out JsonElement payloadElem) && payloadElem.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
				{
					payload = JsonNode.Parse(payloadElem.GetRawText());
				}
				return new ProtocolEvent(eventName, payload);
			}

			return null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string? GetStringOrNull(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	/// <summary>
	/// 静态辅助方法：直接解析一次完整的 LLM 输出
	/// </summary>
	public static IReadOnlyList<AgentProtocolItem> ParseComplete(string raw)
	{
		StreamingJsonParser parser = new();
		IReadOnlyList<AgentProtocolItem> fromPush = parser.Push(raw);
		IReadOnlyList<AgentProtocolItem> fromFlush = parser.Flush();
		List<AgentProtocolItem> all = [.. fromPush, .. fromFlush];
		return all;
	}
}
