using Nori.Core.Voice.Stt;

namespace Nori.Core.Voice.Realtime;

/// <summary>单轮临时字幕，仅供内存中的观察者读取。</summary>
public sealed record RealtimeTranscriptSnapshot(
	long Generation,
	string TurnId,
	string? ProviderItemId,
	string Text,
	bool IsFinal,
	bool Truncated,
	DateTimeOffset UpdatedAtUtc);

/// <summary>单轮事件关联；由调用方串行访问。</summary>
public interface ITranscriptionCoordinator
{
	RealtimeTranscriptSnapshot? Snapshot { get; }
	TranscriptionEventResult Handle(long generation, StreamingSttEvent sttEvent);
}

public enum TranscriptionEventResult
{
	Ignored,
	Committed,
	Partial,
	Completed,
	Failed,
}

/// <summary>一条连接只服务一轮。首个文本项暂定归属，提交确认可纠正尚未锁定的临时项。</summary>
public sealed class RealtimeTranscriptionCoordinator(long turnGeneration, string turnId) : ITranscriptionCoordinator
{
	public const int MaxTranscriptCharacters = 8192;

	private string? _itemId;
	private bool _committed;
	private bool _terminal;

	public RealtimeTranscriptSnapshot? Snapshot { get; private set; }

	public TranscriptionEventResult Handle(long generation, StreamingSttEvent sttEvent)
	{
		if (generation != _generation || _terminal) return TranscriptionEventResult.Ignored;
		string? itemId = sttEvent.ItemId;
		switch (sttEvent.Kind)
		{
			case StreamingSttEventKind.InputCommitted:
				if (_committed || string.IsNullOrEmpty(itemId)) return TranscriptionEventResult.Ignored;
				_committed = true;
				if (_itemId != itemId) Snapshot = null;
				_itemId = itemId;
				return TranscriptionEventResult.Committed;
			case StreamingSttEventKind.Delta:
			case StreamingSttEventKind.Completed:
				if (string.IsNullOrEmpty(itemId) || (_itemId is not null && _itemId != itemId))
					return TranscriptionEventResult.Ignored;
				_itemId = itemId;
				bool final = sttEvent.Kind == StreamingSttEventKind.Completed;
				string prefix = final ? "" : Snapshot?.Text ?? "";
				string incoming = sttEvent.Text ?? "";
				if (!final && (incoming.Length == 0 || Snapshot?.Truncated == true))
					return TranscriptionEventResult.Ignored;
				int available = MaxTranscriptCharacters - prefix.Length;
				bool truncated = incoming.Length > available;
				int take = Math.Min(available, incoming.Length);
				// 截断不能把 UTF-16 代理对切成半个字符（包括跨事件的代理对）。
				if (truncated && take > 0 && char.IsHighSurrogate(incoming[take - 1])) take--;
				if (truncated && take == 0 && prefix.Length > 0 && char.IsHighSurrogate(prefix[^1])) prefix = prefix[..^1];
				Snapshot = new(_generation, turnId, itemId, string.Concat(prefix, incoming.AsSpan(0, take)),
					final, truncated, DateTimeOffset.UtcNow);
				_terminal = final;
				return final ? TranscriptionEventResult.Completed : TranscriptionEventResult.Partial;
			case StreamingSttEventKind.Failed:
				if (_itemId is not null && !string.IsNullOrEmpty(itemId) && _itemId != itemId)
					return TranscriptionEventResult.Ignored;
				goto case StreamingSttEventKind.Error;
			case StreamingSttEventKind.Error:
			case StreamingSttEventKind.Closed:
				_terminal = true;
				Snapshot = null;
				return TranscriptionEventResult.Failed;
			default:
				return TranscriptionEventResult.Ignored;
		}
	}

	private readonly long _generation = turnGeneration;
}
