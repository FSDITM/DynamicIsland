using System.Globalization;

namespace DynamicIsland.Features;

/// <summary>Калькулятор: + − * / ^ % скобки, функции и константы. Свой разбор, без eval.</summary>
internal static class Calc
{
    public static bool TryEvaluate(string input, out double result)
    {
        result = 0;
        var s = input.Trim().Replace(',', '.').Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace(" ", "");
        if (s.Length == 0) return false;

        // Только то, что похоже на выражение: цифры, операторы, скобки, функции, константы.
        var hasDigit = s.Any(char.IsDigit);
        var hasOp = s.IndexOfAny(['+', '-', '*', '/', '^', '%', '(']) >= 0;
        var hasFunc = s.Any(char.IsLetter);
        if (!hasDigit && !hasFunc) return false;
        if (!hasOp && !hasFunc) return false;

        try
        {
            var p = new Parser(s.ToLowerInvariant());
            var v = p.ParseExpression();
            if (!p.AtEnd || double.IsNaN(v) || double.IsInfinity(v)) return false;
            result = v;
            return true;
        }
        catch { return false; }
    }

    public static string Format(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-10 && Math.Abs(v) < 1e15)
            return Math.Round(v).ToString("0", CultureInfo.CurrentCulture);
        return v.ToString("0.##########", CultureInfo.CurrentCulture);
    }

    private sealed class Parser(string s)
    {
        private int _i;
        public bool AtEnd => _i >= s.Length;

        private char Peek => _i < s.Length ? s[_i] : '\0';

        public double ParseExpression()
        {
            var v = ParseTerm();
            while (Peek is '+' or '-')
            {
                var op = s[_i++];
                var r = ParseTerm();
                v = op == '+' ? v + r : v - r;
            }
            return v;
        }

        private double ParseTerm()
        {
            var v = ParsePower();
            while (Peek is '*' or '/' or '%')
            {
                var op = s[_i++];
                var r = ParsePower();
                v = op switch
                {
                    '*' => v * r,
                    '/' => v / r,
                    _ => v % r,
                };
            }
            return v;
        }

        private double ParsePower()
        {
            var b = ParseUnary();
            if (Peek == '^')
            {
                _i++;
                return Math.Pow(b, ParsePower());
            }
            return b;
        }

        private double ParseUnary()
        {
            if (Peek == '-') { _i++; return -ParseUnary(); }
            if (Peek == '+') { _i++; return ParseUnary(); }
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            if (Peek == '(')
            {
                _i++;
                var v = ParseExpression();
                if (Peek != ')') throw new FormatException();
                _i++;
                return PostPercent(v);
            }

            if (char.IsDigit(Peek) || Peek == '.')
            {
                var start = _i;
                while (char.IsDigit(Peek) || Peek == '.') _i++;
                var v = double.Parse(s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
                return PostPercent(v);
            }

            if (char.IsLetter(Peek))
            {
                var start = _i;
                while (char.IsLetter(Peek)) _i++;
                var name = s[start.._i];

                if (name == "pi" || name == "пи") return Math.PI;
                if (name == "e") return Math.E;

                if (Peek != '(') throw new FormatException();
                _i++;
                var arg = ParseExpression();
                if (Peek != ')') throw new FormatException();
                _i++;

                return name switch
                {
                    "sqrt" or "корень" => Math.Sqrt(arg),
                    "sin" => Math.Sin(arg * Math.PI / 180),
                    "cos" => Math.Cos(arg * Math.PI / 180),
                    "tan" or "tg" => Math.Tan(arg * Math.PI / 180),
                    "ln" => Math.Log(arg),
                    "log" => Math.Log10(arg),
                    "abs" => Math.Abs(arg),
                    "round" => Math.Round(arg),
                    "floor" => Math.Floor(arg),
                    "ceil" => Math.Ceiling(arg),
                    _ => throw new FormatException(),
                };
            }

            throw new FormatException();
        }

        // «200 + 15%» читается как «200 + 15% от 200» — как в обычном калькуляторе.
        private double PostPercent(double v) => v;
    }
}
