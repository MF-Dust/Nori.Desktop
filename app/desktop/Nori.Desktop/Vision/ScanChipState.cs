namespace Nori.Desktop.Vision;

/// <summary>进程内共享的芯片余量与近期读数；隐藏或重建输入框不会重置散热。</summary>
internal sealed class ScanChipState
{
	internal const int Capacity = 5;
	internal static readonly TimeSpan CoolEvery = TimeSpan.FromMinutes(5);
	private int _heat;
	private DateTimeOffset? _anchor;
	private readonly Dictionary<string, string> _readouts = new(StringComparer.Ordinal);

	internal int Charges(DateTimeOffset now)
	{
		Cool(now);
		return Math.Max(0, Capacity - _heat);
	}

	internal TimeSpan NextChargeIn(DateTimeOffset now)
	{
		Cool(now);
		return _heat > 0 && _anchor is { } anchor ? anchor + CoolEvery - now : TimeSpan.Zero;
	}

	internal bool Consume(DateTimeOffset now)
	{
		if (Charges(now) == 0) return false;
		_anchor ??= now;
		_heat++;
		return true;
	}

	private void Cool(DateTimeOffset now)
	{
		if (_anchor is not { } anchor || now < anchor + CoolEvery) return;
		int windows = (int)Math.Min(Capacity, (now - anchor).Ticks / CoolEvery.Ticks);
		_heat = Math.Max(0, _heat - windows);
		_anchor = _heat == 0 ? null : anchor + CoolEvery * windows;
	}

	internal bool TryReplay(string fingerprint, out string text) => _readouts.TryGetValue(fingerprint, out text!);

	internal void Remember(string fingerprint, string text)
	{
		// 截图不落盘；只保留最近读数，避免长期扫描无限积累内容。
		if (_readouts.Count >= 32) _readouts.Remove(_readouts.Keys.First());
		_readouts[fingerprint] = text;
	}
}
