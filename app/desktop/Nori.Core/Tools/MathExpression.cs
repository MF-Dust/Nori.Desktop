using System.Globalization;

namespace Nori.Core.Tools;

/// <summary>
/// 安全的数学表达式求值器 (递归下降解析，杜绝 eval / 注入风险)
///
/// 移植自前端 services/agent/tools 的 evaluateMathExpression:
/// 支持加减乘除取模、乘方、一元正负、括号、百分号后缀、
/// 常量 pi / e 与常用数学函数。
/// </summary>
public static class MathExpression
{
	/// <summary>
	/// 计算数学表达式; 表达式非法时抛出 FormatException (消息面向用户展示)
	/// </summary>
	public static double Calculate(string expression)
	{
		Parser parser = new(expression.Trim());
		double result = parser.ParseExpression();
		parser.SkipWhitespace();
		if (!parser.IsEnd())
		{
			throw parser.Error($"无法识别的字符: {parser.Current()}");
		}
		return result;
	}

	private sealed class Parser(string source)
	{
		private int _pos;

		public bool IsEnd() => _pos >= source.Length;

		public char Current() => source[_pos];

		public FormatException Error(string message) => new($"{message} (位置 {_pos + 1})");

		public void SkipWhitespace()
		{
			while (_pos < source.Length && char.IsWhiteSpace(source[_pos])) _pos++;
		}

		/// <summary>
		/// 处理 % 后缀与二元取模的二义性:
		/// 后面跟着新的操作数时视为取模 (返回原值), 否则按百分号 val% = val * 0.01
		/// </summary>
		private double HandlePostfix(double value)
		{
			Skip(source, ref _pos);
			if (_pos < source.Length && source[_pos] == '%')
			{
				int probe = _pos + 1;
				Skip(source, ref probe);
				if (probe < source.Length && (char.IsDigit(source[probe]) || source[probe] == '('))
				{
					// 二元取模交给 ParseMultiplicative 处理
					return value;
				}
				_pos++;
				return value * 0.01;
			}
			return value;
		}

		private static void Skip(string s, ref int pos)
		{
			while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
		}

		public double ParseExpression()
		{
			SkipWhitespace();
			double left = ParseAdditive();
			return left;
		}

		private double ParseAdditive()
		{
			double left = ParseMultiplicative();
			while (true)
			{
				SkipWhitespace();
				if (!IsEnd() && (Current() is '+' or '-'))
				{
					char op = Current();
					_pos++;
					double right = ParseMultiplicative();
					if (op == '+') left += right;
					else left -= right;
				}
				else
				{
					break;
				}
			}
			return left;
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1244", Justification = "表达式除零检查必须精确判断零值，使用范围会拒绝合法非零输入。")]
		private double ParseMultiplicative()
		{
			double left = ParseExponent();
			while (true)
			{
				SkipWhitespace();
				if (!IsEnd() && Current() is '*' or '/' or '%')
				{
					char op = Current();
					_pos++;
					double right = ParseExponent();
					switch (op)
					{
						case '*':
							left *= right;
							break;
						case '/':
							if (right == 0) throw Error("除数不能为零");
							left /= right;
							break;
						default:
							if (right == 0) throw Error("取模除数不能为零");
							left %= right;
							break;
					}
				}
				else
				{
					break;
				}
			}
			return left;
		}

		private double ParseExponent()
		{
			double left = ParsePrimary();
			SkipWhitespace();
			if (!IsEnd() && (Current() == '^' || (Current() == '*' && PeekNext() == '*')))
			{
				if (Current() == '*') _pos += 2;
				else _pos++;
				double right = ParseExponent();
				left = Math.Pow(left, right);
			}
			return left;
		}

		private char PeekNext() => _pos + 1 < source.Length ? source[_pos + 1] : '\0';

		private double ParsePrimary()
		{
			SkipWhitespace();
			if (IsEnd()) throw Error("意外的表达式结尾");

			// 处理一元加减
			if (Current() == '+')
			{
				_pos++;
				return ParsePrimary();
			}
			if (Current() == '-')
			{
				_pos++;
				return -ParsePrimary();
			}

			// 括号表达式
			if (Current() == '(')
			{
				_pos++;
				double value = ParseExpression();
				SkipWhitespace();
				if (IsEnd() || Current() != ')') throw Error("缺少匹配的闭括号 ')'");
				_pos++;
				return HandlePostfix(value);
			}

			// 数字字面量
			if (char.IsDigit(Current()) || Current() == '.')
			{
				int start = _pos;
				bool hasDot = false;
				while (!IsEnd() && (char.IsDigit(Current()) || (!hasDot && Current() == '.')))
				{
					if (Current() == '.') hasDot = true;
					_pos++;
				}
				string numStr = source[start.._pos];
				if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
				{
					throw Error($"无效的数字: {numStr}");
				}
				return HandlePostfix(num);
			}

			// 标识符 (函数或常量)
			if (char.IsAsciiLetter(Current()) || Current() == '_')
			{
				int start = _pos;
				while (!IsEnd() && (char.IsAsciiLetterOrDigit(Current()) || Current() == '_')) _pos++;
				string name = source[start.._pos].ToLowerInvariant();

				// 常量
				if (name == "pi") return HandlePostfix(Math.PI);
				if (name == "e") return HandlePostfix(Math.E);

				// 函数调用
				SkipWhitespace();
				if (!IsEnd() && Current() == '(')
				{
					_pos++;
					List<double> args = [];
					SkipWhitespace();
					if (!IsEnd() && Current() != ')')
					{
						args.Add(ParseExpression());
						SkipWhitespace();
						while (!IsEnd() && Current() == ',')
						{
							_pos++;
							args.Add(ParseExpression());
							SkipWhitespace();
						}
					}
					if (IsEnd() || Current() != ')') throw Error($"函数 {name} 缺少闭括号 ')'");
					_pos++;

					return HandlePostfix(ApplyFunction(name, args));
				}

				throw Error($"未知的标识符: {name}");
			}

			throw Error($"无法识别的字符: {Current()}");
		}

		private static double ApplyFunction(string name, List<double> args)
		{
			double A0(int n)
			{
				if (args.Count != n) throw new FormatException($"{name} 需要 {n} 个参数");
				return args[0];
			}

			switch (name)
			{
				case "sqrt": return Math.Sqrt(A0(1));
				case "cbrt": return Math.Cbrt(A0(1));
				case "abs": return Math.Abs(A0(1));
				case "sin": return Math.Sin(A0(1));
				case "cos": return Math.Cos(A0(1));
				case "tan": return Math.Tan(A0(1));
				case "asin": return Math.Asin(A0(1));
				case "acos": return Math.Acos(A0(1));
				case "atan": return Math.Atan(A0(1));
				case "round": return Math.Round(A0(1));
				case "floor": return Math.Floor(A0(1));
				case "ceil": return Math.Ceiling(A0(1));
				case "log":
				case "ln": return Math.Log(A0(1));
				case "log10": return Math.Log10(A0(1));
				case "log2": return Math.Log2(A0(1));
				case "exp": return Math.Exp(A0(1));
				case "pow":
					if (args.Count != 2) throw new FormatException("pow 需要 2 个参数");
					return Math.Pow(args[0], args[1]);
				case "max":
					if (args.Count == 0) throw new FormatException("max 至少需要 1 个参数");
					return args.Max();
				case "min":
					if (args.Count == 0) throw new FormatException("min 至少需要 1 个参数");
					return args.Min();
				default:
					throw new FormatException($"不支持的数学函数: {name}");
			}
		}
	}
}
