using System;
using System.Globalization;
using System.Text;

using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.SLua
{
    /// <summary>
    /// Pure SLua standard library (string.* / math.* core) for Tier-2. These are scene-independent
    /// value functions, so they need no ISystemAPI/shim and their results serialize like any operand.
    /// Dispatched by the `luacall` opcode (operands: function id, arg count). The front-end resolves
    /// e.g. string.format -> Func.StrFormat and math.floor -> Func.MathFloor.
    ///
    /// DEFERRED (flagged): Lua pattern matching (string.match/gmatch/gsub) — its own engine, not here.
    /// Calls to unsupported string.*/math.* are rejected by the front-end with a clear error.
    /// </summary>
    public static class LuaLib
    {
        public enum Func
        {
            // string.*
            StrFormat = 0, StrSub, StrLen, StrUpper, StrLower, StrRep, StrByte, StrChar,
            // math.* (functions)
            MathFloor, MathCeil, MathAbs, MathMin, MathMax, MathSqrt, MathRandom, MathRandomSeed,
            // math.* (value, exposed as a 0-arg call)
            MathHuge
        }

        // Not part of RuntimeState: math.random's sequence is not reproduced across serialization
        // (Lua's RNG state isn't serialized in our model). Acceptable for Tier-2; noted.
        private static Random _rng = new Random(12345);

        public static object Call(int funcId, object[] args)
        {
            switch ((Func)funcId)
            {
                case Func.StrFormat:      return Format(args);
                case Func.StrSub:         return Sub(args);
                case Func.StrLen:         return (float)Str(At(args, 0)).Length;
                case Func.StrUpper:       return Str(At(args, 0)).ToUpperInvariant();
                case Func.StrLower:       return Str(At(args, 0)).ToLowerInvariant();
                case Func.StrRep:         return Rep(args);
                case Func.StrByte:        return ByteFn(args);
                case Func.StrChar:        return CharFn(args);

                case Func.MathFloor:      return (float)Math.Floor(Num(At(args, 0)));
                case Func.MathCeil:       return (float)Math.Ceiling(Num(At(args, 0)));
                case Func.MathAbs:        return (float)Math.Abs(Num(At(args, 0)));
                case Func.MathMin:        return MinMax(args, true);
                case Func.MathMax:        return MinMax(args, false);
                case Func.MathSqrt:       return (float)Math.Sqrt(Num(At(args, 0)));
                case Func.MathRandom:     return RandomFn(args);
                case Func.MathRandomSeed: _rng = new Random((int)Num(At(args, 0))); return LuaNil.Instance;
                case Func.MathHuge:       return float.PositiveInfinity;

                default: throw new CheckException("unknown lua stdlib function id " + funcId);
            }
        }

        // ---- argument / coercion helpers ----
        private static object At(object[] a, int i) { return (i < a.Length) ? a[i] : null; }

        private static double Num(object o)
        {
            if (o is float f) return f;
            if (o is int i) return i;
            if (o is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            throw new CheckException("number expected");
        }

        private static int IntArg(object o, int dflt)
        {
            if (o == null || o is LuaNil) return dflt;
            return (int)Num(o);
        }

        // Lua tostring (kept local to avoid coupling to the interpreter).
        private static string Str(object o)
        {
            if (o == null || o is LuaNil) return "nil";
            if (o is bool b) return b ? "true" : "false";
            if (o is string s) return s;
            if (o is float || o is int) return NumToStr(o);
            if (o is LSLTable) return "table";
            return o.ToString();
        }

        private static string NumToStr(object o)
        {
            double d = (o is int i) ? i : (float)o;
            if (d == Math.Floor(d) && !double.IsInfinity(d)) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("0.0###############", CultureInfo.InvariantCulture);
        }

        // ---- string.* ----
        private static string Sub(object[] a)
        {
            string s = Str(At(a, 0));
            int len = s.Length;
            int i = IntArg(At(a, 1), 1);
            int j = IntArg(At(a, 2), -1);
            // Lua 1-based, negative = from end
            if (i < 0) i = Math.Max(len + i + 1, 1); else if (i == 0) i = 1;
            if (j < 0) j = len + j + 1; else if (j > len) j = len;
            if (i > j) return "";
            return s.Substring(i - 1, j - i + 1);
        }

        private static string Rep(object[] a)
        {
            string s = Str(At(a, 0));
            int n = IntArg(At(a, 1), 0);
            if (n <= 0) return "";
            var sb = new StringBuilder(s.Length * n);
            for (int k = 0; k < n; k++) sb.Append(s);
            return sb.ToString();
        }

        private static object ByteFn(object[] a)
        {
            string s = Str(At(a, 0));
            int i = IntArg(At(a, 1), 1);
            if (i < 0) i = s.Length + i + 1;
            if (i < 1 || i > s.Length) return LuaNil.Instance;
            return (float)(int)s[i - 1];
        }

        private static string CharFn(object[] a)
        {
            var sb = new StringBuilder(a.Length);
            for (int k = 0; k < a.Length; k++) sb.Append((char)(int)Num(a[k]));
            return sb.ToString();
        }

        // ---- math.* ----
        private static object MinMax(object[] a, bool min)
        {
            if (a.Length == 0) throw new CheckException("bad argument to '" + (min ? "min" : "max") + "'");
            double best = Num(a[0]);
            for (int k = 1; k < a.Length; k++)
            {
                double v = Num(a[k]);
                if (min ? v < best : v > best) best = v;
            }
            return (float)best;
        }

        private static object RandomFn(object[] a)
        {
            if (a.Length == 0) return (float)_rng.NextDouble();              // [0,1)
            int m = (int)Num(a[0]);
            if (a.Length == 1) return (float)_rng.Next(1, m + 1);           // [1,m]
            int n = (int)Num(a[1]);
            return (float)_rng.Next(m, n + 1);                             // [m,n]
        }

        // ---- string.format: printf-style, common directives ----
        private static string Format(object[] a)
        {
            string fmt = Str(At(a, 0));
            var sb = new StringBuilder();
            int ai = 1;
            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                if (c != '%') { sb.Append(c); continue; }
                i++;
                if (i >= fmt.Length) break;
                if (fmt[i] == '%') { sb.Append('%'); continue; }

                string flags = "";
                while (i < fmt.Length && "-+ 0#".IndexOf(fmt[i]) >= 0) { flags += fmt[i]; i++; }
                string width = "";
                while (i < fmt.Length && char.IsDigit(fmt[i])) { width += fmt[i]; i++; }
                string prec = null;
                if (i < fmt.Length && fmt[i] == '.') { i++; prec = ""; while (i < fmt.Length && char.IsDigit(fmt[i])) { prec += fmt[i]; i++; } }
                char conv = (i < fmt.Length) ? fmt[i] : 's';
                object val = (ai < a.Length) ? a[ai++] : null;
                sb.Append(FormatOne(conv, flags, width, prec, val));
            }
            return sb.ToString();
        }

        private static string FormatOne(char conv, string flags, string width, string prec, object val)
        {
            string body;
            switch (conv)
            {
                case 'd': case 'i': case 'u':
                    body = ((long)Num(val)).ToString(CultureInfo.InvariantCulture);
                    break;
                case 'f': case 'F':
                {
                    int p = (prec == null) ? 6 : (prec == "" ? 0 : int.Parse(prec));
                    body = Num(val).ToString("F" + p, CultureInfo.InvariantCulture);
                    break;
                }
                case 'e': case 'E':
                {
                    int p = (prec == null) ? 6 : (prec == "" ? 0 : int.Parse(prec));
                    body = Num(val).ToString((conv == 'e' ? "e" : "E") + p, CultureInfo.InvariantCulture);
                    break;
                }
                case 'g': case 'G':
                    body = Num(val).ToString(CultureInfo.InvariantCulture);
                    break;
                case 'x': body = ((long)Num(val)).ToString("x", CultureInfo.InvariantCulture); break;
                case 'X': body = ((long)Num(val)).ToString("X", CultureInfo.InvariantCulture); break;
                case 'c': body = ((char)(int)Num(val)).ToString(); break;
                case 's':
                default:
                    body = Str(val);
                    if (prec != null && prec != "" && body.Length > int.Parse(prec)) body = body.Substring(0, int.Parse(prec));
                    break;
            }

            if (width.Length > 0)
            {
                int w = int.Parse(width);
                if (body.Length < w)
                {
                    bool left = flags.IndexOf('-') >= 0;
                    bool zero = flags.IndexOf('0') >= 0 && !left && conv != 's';
                    char pad = zero ? '0' : ' ';
                    body = left ? body.PadRight(w, ' ') : body.PadLeft(w, pad);
                }
            }
            return body;
        }
    }
}
