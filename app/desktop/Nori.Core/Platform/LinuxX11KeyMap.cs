using System.Buffers;
using System.Text;

namespace Nori.Core.Platform;

internal static class LinuxX11KeyMap
{
	internal const uint ShiftLeft = 0xffe1;

	internal static uint ForKeyPress(string key) => key.ToUpperInvariant() switch
	{
		"ENTER" => 0xff0d,
		"TAB" => 0xff09,
		"ESCAPE" => 0xff1b,
		"BACKSPACE" => 0xff08,
		"DELETE" => 0xffff,
		"HOME" => 0xff50,
		"END" => 0xff57,
		"PAGEUP" => 0xff55,
		"PAGEDOWN" => 0xff56,
		"ARROWUP" => 0xff52,
		"ARROWDOWN" => 0xff54,
		"ARROWLEFT" => 0xff51,
		"ARROWRIGHT" => 0xff53,
		"SPACE" => ' ',
		_ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => char.ToLowerInvariant(key[0]),
		_ => 0,
	};

	internal static bool TryReadText(string text, out List<uint> symbols)
	{
		symbols = [];
		ReadOnlySpan<char> remaining = text;
		while (!remaining.IsEmpty)
		{
			if (Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed) != OperationStatus.Done || Rune.IsControl(rune))
				return false;
			uint codePoint = (uint)rune.Value;
			symbols.Add(codePoint <= 0xff ? codePoint : 0x0100_0000 | codePoint);
			remaining = remaining[consumed..];
		}
		return true;
	}

	internal readonly record struct Stroke(byte Keycode, bool Shift);

	internal static void AddKeycode(Dictionary<uint, Stroke> map, byte keycode, uint plain, uint shifted)
	{
		if (plain != 0 && (!map.TryGetValue(plain, out Stroke previous) || previous.Shift))
			map[plain] = new(keycode, false);
		if (shifted != 0) map.TryAdd(shifted, new(keycode, true));
	}
}
