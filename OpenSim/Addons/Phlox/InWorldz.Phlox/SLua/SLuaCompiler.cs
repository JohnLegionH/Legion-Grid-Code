/*
 * SLua Tier-1 front-end (ADDITIVE, parallel to the LSL front-end).
 *
 * Compiles the TRIVIAL SLua subset (SL's Luau dialect) to Phlox ASSEMBLY TEXT, which the proven
 * back-half (CompilerFrontend.AssembleText -> assembler -> VM -> serialization) consumes unchanged.
 * Targets SL's source-verified surface (see SLUA_SURFACE.md):
 *   - ll calls:   ll.Name(args)  ->  Phlox "ll"+Name  ->  existing 674-fn table (TableIndex)
 *   - events:     bare global function whose name is a Phlox event (touch_start, timer, ...)
 *   - top-level:  code outside any function = the state_entry-equivalent (runs on rez)
 *   - types:      Luau `number` == double  -> Phlox Float; coerce to a function's declared
 *                 param type at the call boundary using the EXISTING cast opcodes (icast/fcast).
 *
 * Scope: locals, number arithmetic + coercion, if/elseif/else, while, comparison, event-named
 * global functions, top-level code, ll.* calls. Everything else (tables, closures, LLEvents:on
 * object model, metatables, multiple states, user functions) is Tier-2 and intentionally rejected
 * with a clear error rather than mis-compiled.
 *
 * NO VM/opcode change: this is pure front-end codegen, mirroring what the LSL GenVisitor emits.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.SLua
{
    // ======================================================================================
    // Public entry
    // ======================================================================================
    public static class SLuaCompiler
    {
        /// <summary>
        /// Heuristic script-kind detection. LSL never begins with a Lua line comment ("--"), so a
        /// leading "--" (and especially the explicit "--!slua"/"--!lua" marker) routes to SLua.
        /// </summary>
        public static bool IsLuaScript(string src)
        {
            if (string.IsNullOrEmpty(src)) return false;
            string s = src.TrimStart();
            return s.StartsWith("--!slua", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("--!lua", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("--");
        }

        /// <summary>
        /// Compile SLua source to Phlox assembly text. Returns null on error (reported via listener).
        /// </summary>
        public static string CompileToAssembly(string src, ILSLListener listener)
        {
            try
            {
                var tokens = new SLuaLexer(src).Tokenize();
                var chunk = new SLuaParser(tokens).ParseChunk();
                return new SLuaCodeGen().Generate(chunk);
            }
            catch (SLuaException e)
            {
                if (listener != null) listener.Error(string.Format("SLua: {0} (line {1})", e.Message, e.Line));
                return null;
            }
            catch (Exception e)
            {
                if (listener != null) listener.Error("SLua: internal compiler error: " + e.Message);
                return null;
            }
        }
    }

    public sealed class SLuaException : Exception
    {
        public int Line;
        public SLuaException(string msg, int line) : base(msg) { Line = line; }
    }

    // ======================================================================================
    // Lexer
    // ======================================================================================
    internal enum TT { Name, Number, String, Keyword, Op, EOF }

    internal struct Tok
    {
        public TT Type;
        public string Text;
        public double Num;
        public int Line;
        public Tok(TT t, string s, int line, double n = 0) { Type = t; Text = s; Line = line; Num = n; }
        public override string ToString() { return Type + ":" + Text; }
    }

    internal sealed class SLuaLexer
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "local","function","end","if","then","elseif","else","while","do","return",
            "true","false","nil","and","or","not","for","in"
        };

        private readonly string _s;
        private int _i;
        private int _line = 1;

        public SLuaLexer(string s) { _s = s ?? string.Empty; }

        private char Cur => _i < _s.Length ? _s[_i] : '\0';
        private char Peek(int k = 1) => _i + k < _s.Length ? _s[_i + k] : '\0';

        public List<Tok> Tokenize()
        {
            var toks = new List<Tok>();
            while (true)
            {
                SkipTrivia();
                if (_i >= _s.Length) { toks.Add(new Tok(TT.EOF, "<eof>", _line)); break; }

                char c = Cur;
                if (char.IsLetter(c) || c == '_') { toks.Add(LexName()); continue; }
                if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek()))) { toks.Add(LexNumber()); continue; }
                if (c == '"' || c == '\'') { toks.Add(LexString(c)); continue; }
                toks.Add(LexOperator());
            }
            return toks;
        }

        private void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                char c = Cur;
                if (c == '\n') { _line++; _i++; }
                else if (c == ' ' || c == '\t' || c == '\r') { _i++; }
                else if (c == '-' && Peek() == '-')
                {
                    _i += 2;
                    if (Cur == '[' && Peek() == '[') { _i += 2; SkipBlockComment(); }
                    else { while (_i < _s.Length && Cur != '\n') _i++; }
                }
                else break;
            }
        }

        private void SkipBlockComment()
        {
            while (_i < _s.Length)
            {
                if (Cur == ']' && Peek() == ']') { _i += 2; return; }
                if (Cur == '\n') _line++;
                _i++;
            }
        }

        private Tok LexName()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(Cur) || Cur == '_')) _i++;
            string text = _s.Substring(start, _i - start);
            return new Tok(Keywords.Contains(text) ? TT.Keyword : TT.Name, text, _line);
        }

        private Tok LexNumber()
        {
            int start = _i;
            // hex
            if (Cur == '0' && (Peek() == 'x' || Peek() == 'X'))
            {
                _i += 2;
                while (_i < _s.Length && Uri.IsHexDigit(Cur)) _i++;
                string hx = _s.Substring(start, _i - start);
                long hv = Convert.ToInt64(hx.Substring(2), 16);
                return new Tok(TT.Number, hx, _line, hv);
            }
            while (_i < _s.Length && char.IsDigit(Cur)) _i++;
            if (Cur == '.') { _i++; while (_i < _s.Length && char.IsDigit(Cur)) _i++; }
            if (Cur == 'e' || Cur == 'E')
            {
                _i++;
                if (Cur == '+' || Cur == '-') _i++;
                while (_i < _s.Length && char.IsDigit(Cur)) _i++;
            }
            string text = _s.Substring(start, _i - start);
            double d = double.Parse(text, CultureInfo.InvariantCulture);
            return new Tok(TT.Number, text, _line, d);
        }

        private Tok LexString(char quote)
        {
            int line = _line;
            _i++; // opening quote
            var sb = new StringBuilder();
            while (_i < _s.Length && Cur != quote)
            {
                char c = Cur;
                if (c == '\n') throw new SLuaException("unterminated string", line);
                if (c == '\\')
                {
                    _i++;
                    char e = Cur;
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\'': sb.Append('\''); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append(e); break;
                    }
                    _i++;
                }
                else { sb.Append(c); _i++; }
            }
            if (_i >= _s.Length) throw new SLuaException("unterminated string", line);
            _i++; // closing quote
            return new Tok(TT.String, sb.ToString(), line);
        }

        private Tok LexOperator()
        {
            int line = _line;
            char c = Cur;
            char n = Peek();
            // two-char operators
            if ((c == '=' && n == '=') || (c == '~' && n == '=') ||
                (c == '<' && n == '=') || (c == '>' && n == '=') || (c == '.' && n == '.'))
            {
                _i += 2;
                return new Tok(TT.Op, new string(new[] { c, n }), line);
            }
            _i++;
            return new Tok(TT.Op, c.ToString(), line);
        }
    }

    // ======================================================================================
    // AST
    // ======================================================================================
    internal abstract class Node { public int Line; }

    internal abstract class Expr : Node { }
    internal sealed class NumberLit : Expr { public double Value; }
    internal sealed class StringLit : Expr { public string Value; }
    internal sealed class BoolLit : Expr { public bool Value; }
    internal sealed class NameRef : Expr { public string Name; }
    internal sealed class Binary : Expr { public string Op; public Expr L, R; }
    internal sealed class Unary : Expr { public string Op; public Expr E; }
    internal sealed class LlCall : Expr { public string Member; public List<Expr> Args; }
    // Tier-2 table expressions
    internal sealed class NilLit : Expr { }
    internal sealed class TableField { public Expr Key; public Expr Value; } // Key==null => array element
    internal sealed class TableLit : Expr { public List<TableField> Fields; }
    internal sealed class Index : Expr { public Expr Target; public Expr Key; }   // t[k] / t.x
    internal sealed class Len : Expr { public Expr E; }                            // #t
    internal sealed class Builtin : Expr { public string Name; public Expr Arg; }  // type/tostring/tonumber

    internal abstract class Stmt : Node { }
    internal sealed class LocalDecl : Stmt { public string Name; public Expr Init; }
    internal sealed class Assign : Stmt { public string Name; public Expr Value; }
    internal sealed class ExprStmt : Stmt { public LlCall Call; }
    internal sealed class IfStmt : Stmt { public Expr Cond; public List<Stmt> Then; public List<Stmt> Else; }
    internal sealed class WhileStmt : Stmt { public Expr Cond; public List<Stmt> Body; }
    internal sealed class ReturnStmt : Stmt { public Expr Value; }
    internal sealed class FuncDecl : Stmt { public string Name; public List<string> Params; public List<Stmt> Body; }
    // Tier-2 table statements
    internal sealed class IndexAssign : Stmt { public Expr Target; public Expr Key; public Expr Value; } // t[k]=v / t.x=v
    internal sealed class ForIn : Stmt { public List<string> Vars; public Expr TableExpr; public List<Stmt> Body; } // for k,v in pairs(t)
    internal sealed class TableInsert : Stmt { public Expr Table; public Expr Value; } // table.insert(t, v)

    // ======================================================================================
    // Parser (recursive descent)
    // ======================================================================================
    internal sealed class SLuaParser
    {
        private readonly List<Tok> _t;
        private int _p;

        public SLuaParser(List<Tok> toks) { _t = toks; }

        private Tok Cur => _t[_p];
        private Tok Next => _t[Math.Min(_p + 1, _t.Count - 1)];
        private bool IsOp(string s) => Cur.Type == TT.Op && Cur.Text == s;
        private bool IsKw(string s) => Cur.Type == TT.Keyword && Cur.Text == s;

        private Tok Eat() { return _t[_p++]; }
        private void ExpectOp(string s) { if (!IsOp(s)) Err("expected '" + s + "'"); _p++; }
        private void ExpectKw(string s) { if (!IsKw(s)) Err("expected '" + s + "'"); _p++; }
        private void Err(string m) { throw new SLuaException(m + " near '" + Cur.Text + "'", Cur.Line); }

        public List<Stmt> ParseChunk()
        {
            var stmts = new List<Stmt>();
            while (Cur.Type != TT.EOF) stmts.Add(ParseStat());
            return stmts;
        }

        // Parse a block until a terminator keyword (end/else/elseif) or EOF.
        private List<Stmt> ParseBlock()
        {
            var stmts = new List<Stmt>();
            while (Cur.Type != TT.EOF && !IsKw("end") && !IsKw("else") && !IsKw("elseif"))
                stmts.Add(ParseStat());
            return stmts;
        }

        private Stmt ParseStat()
        {
            int line = Cur.Line;
            if (IsOp(";")) { Eat(); return ParseStatNonEmpty(); } // skip stray ';'
            return ParseStatNonEmpty();
        }

        private Stmt ParseStatNonEmpty()
        {
            int line = Cur.Line;
            if (IsKw("local")) return ParseLocal();
            if (IsKw("function")) return ParseFunction();
            if (IsKw("if")) return ParseIf();
            if (IsKw("while")) return ParseWhile();
            if (IsKw("for")) return ParseForIn();
            if (IsKw("return")) return ParseReturn();

            // Name-led statements.
            if (Cur.Type == TT.Name)
            {
                // ll.X(...) call statement
                if (Cur.Text == "ll" && Next.Type == TT.Op && Next.Text == ".")
                {
                    var call = ParseLlCall();
                    return new ExprStmt { Call = call, Line = line };
                }
                // table.insert(t, v) library call statement
                if (Cur.Text == "table" && Next.Type == TT.Op && Next.Text == ".")
                {
                    return ParseTableLibCall();
                }

                // A prefix expression (Name + postfix .x / [k]) used as an assignment target.
                Expr target = ParsePrefixExpr();
                if (IsOp("="))
                {
                    Eat();
                    var val = ParseExpr();
                    if (target is NameRef nr)
                        return new Assign { Name = nr.Name, Value = val, Line = line };
                    if (target is Index ix)
                        return new IndexAssign { Target = ix.Target, Key = ix.Key, Value = val, Line = line };
                    throw new SLuaException("invalid assignment target", line);
                }
                if (target is NameRef && IsOp("("))
                    throw new SLuaException("user function calls are not supported in the Tier-2 subset", line);
                throw new SLuaException("expected '=' (assignment) or a supported call", line);
            }
            Err("unexpected statement");
            return null;
        }

        // for k [, v] in pairs(t) do ... end   (generic-for; pairs only in Tier-2)
        private Stmt ParseForIn()
        {
            int line = Cur.Line; ExpectKw("for");
            var vars = new List<string>();
            if (Cur.Type != TT.Name) Err("expected loop variable name");
            vars.Add(Eat().Text);
            while (IsOp(",")) { Eat(); if (Cur.Type != TT.Name) Err("expected loop variable name"); vars.Add(Eat().Text); }
            ExpectKw("in");
            // Tier-2: the iterator must be pairs(expr) or ipairs(expr) (both iterate insertion order)
            if (!(Cur.Type == TT.Name && (Cur.Text == "pairs" || Cur.Text == "ipairs")))
                throw new SLuaException("for-in requires pairs(...) or ipairs(...) in the Tier-2 subset", Cur.Line);
            Eat(); // 'pairs' / 'ipairs'
            ExpectOp("(");
            var iter = ParseExpr();
            ExpectOp(")");
            ExpectKw("do");
            var body = ParseBlock();
            ExpectKw("end");
            return new ForIn { Vars = vars, TableExpr = iter, Body = body, Line = line };
        }

        // table.insert(t, v)  (Tier-2: t must be a simple name)
        private Stmt ParseTableLibCall()
        {
            int line = Cur.Line;
            Eat(); // 'table'
            ExpectOp(".");
            if (Cur.Type != TT.Name) Err("expected table library function name");
            string fn = Eat().Text;
            if (fn != "insert")
                throw new SLuaException("table." + fn + " is not supported in the Tier-2 subset (only table.insert)", line);
            ExpectOp("(");
            var t = ParseExpr();
            ExpectOp(",");
            var v = ParseExpr();
            ExpectOp(")");
            return new TableInsert { Table = t, Value = v, Line = line };
        }

        private Stmt ParseLocal()
        {
            int line = Cur.Line; ExpectKw("local");
            if (Cur.Type != TT.Name) Err("expected name after 'local'");
            string name = Eat().Text;
            // optional Luau type annotation:  local x: number
            if (IsOp(":")) { Eat(); SkipTypeAnnotation(); }
            ExpectOp("=");
            var init = ParseExpr();
            return new LocalDecl { Name = name, Init = init, Line = line };
        }

        private Stmt ParseFunction()
        {
            int line = Cur.Line; ExpectKw("function");
            if (Cur.Type != TT.Name) Err("expected function name");
            string name = Eat().Text;
            ExpectOp("(");
            var pars = new List<string>();
            if (!IsOp(")"))
            {
                while (true)
                {
                    if (Cur.Type != TT.Name) Err("expected parameter name");
                    pars.Add(Eat().Text);
                    if (IsOp(":")) { Eat(); SkipTypeAnnotation(); }
                    if (IsOp(",")) { Eat(); continue; }
                    break;
                }
            }
            ExpectOp(")");
            if (IsOp(":")) { Eat(); SkipTypeAnnotation(); } // optional return type
            var body = ParseBlock();
            ExpectKw("end");
            return new FuncDecl { Name = name, Params = pars, Body = body, Line = line };
        }

        // Consume a (simple) Luau type annotation token-wise: a Name optionally followed by
        // {..}, generics, or table types are NOT in Tier-1; we accept a bare type name only.
        private void SkipTypeAnnotation()
        {
            if (Cur.Type == TT.Name || Cur.Type == TT.Keyword) { Eat(); return; }
            throw new SLuaException("unsupported type annotation in Tier-1 subset", Cur.Line);
        }

        private Stmt ParseIf()
        {
            int line = Cur.Line; ExpectKw("if");
            var cond = ParseExpr();
            ExpectKw("then");
            var then = ParseBlock();
            List<Stmt> els = null;
            if (IsKw("elseif"))
            {
                // desugar elseif into a nested if in the else branch
                els = new List<Stmt> { ParseElseIf() };
                return new IfStmt { Cond = cond, Then = then, Else = els, Line = line };
            }
            if (IsKw("else")) { Eat(); els = ParseBlock(); }
            ExpectKw("end");
            return new IfStmt { Cond = cond, Then = then, Else = els, Line = line };
        }

        private Stmt ParseElseIf()
        {
            int line = Cur.Line; ExpectKw("elseif");
            var cond = ParseExpr();
            ExpectKw("then");
            var then = ParseBlock();
            List<Stmt> els = null;
            if (IsKw("elseif")) { els = new List<Stmt> { ParseElseIf() }; return new IfStmt { Cond = cond, Then = then, Else = els, Line = line }; }
            if (IsKw("else")) { Eat(); els = ParseBlock(); }
            ExpectKw("end");
            return new IfStmt { Cond = cond, Then = then, Else = els, Line = line };
        }

        private Stmt ParseWhile()
        {
            int line = Cur.Line; ExpectKw("while");
            var cond = ParseExpr();
            ExpectKw("do");
            var body = ParseBlock();
            ExpectKw("end");
            return new WhileStmt { Cond = cond, Body = body, Line = line };
        }

        private Stmt ParseReturn()
        {
            int line = Cur.Line; ExpectKw("return");
            Expr val = null;
            if (Cur.Type != TT.EOF && !IsKw("end") && !IsKw("else") && !IsKw("elseif") && !IsOp(";"))
                val = ParseExpr();
            return new ReturnStmt { Value = val, Line = line };
        }

        // ---- expressions (Lua precedence: or < and < comparison < .. < add < mul < unary) ----
        private Expr ParseExpr() { return ParseOr(); }

        private Expr ParseOr()
        {
            var l = ParseAnd();
            while (IsKw("or")) { Eat(); var r = ParseAnd(); l = new Binary { Op = "or", L = l, R = r, Line = l.Line }; }
            return l;
        }

        private Expr ParseAnd()
        {
            var l = ParseComparison();
            while (IsKw("and")) { Eat(); var r = ParseComparison(); l = new Binary { Op = "and", L = l, R = r, Line = l.Line }; }
            return l;
        }

        private Expr ParseComparison()
        {
            var l = ParseConcat();
            while (IsOp("<") || IsOp(">") || IsOp("<=") || IsOp(">=") || IsOp("==") || IsOp("~="))
            {
                string op = Eat().Text;
                var r = ParseConcat();
                l = new Binary { Op = op, L = l, R = r, Line = l.Line };
            }
            return l;
        }

        private Expr ParseConcat()
        {
            var l = ParseAdd();
            if (IsOp(".."))   // right-associative
            {
                Eat();
                var r = ParseConcat();
                return new Binary { Op = "..", L = l, R = r, Line = l.Line };
            }
            return l;
        }

        private Expr ParseAdd()
        {
            var l = ParseMul();
            while (IsOp("+") || IsOp("-"))
            {
                string op = Eat().Text;
                var r = ParseMul();
                l = new Binary { Op = op, L = l, R = r, Line = l.Line };
            }
            return l;
        }

        private Expr ParseMul()
        {
            var l = ParseUnary();
            while (IsOp("*") || IsOp("/") || IsOp("%"))
            {
                string op = Eat().Text;
                var r = ParseUnary();
                l = new Binary { Op = op, L = l, R = r, Line = l.Line };
            }
            return l;
        }

        private Expr ParseUnary()
        {
            if (IsOp("-"))
            {
                int line = Cur.Line; Eat();
                var e = ParseUnary();
                return new Unary { Op = "-", E = e, Line = line };
            }
            if (IsOp("#"))
            {
                int line = Cur.Line; Eat();
                var e = ParseUnary();
                return new Len { E = e, Line = line };
            }
            if (IsKw("not"))
            {
                int line = Cur.Line; Eat();
                var e = ParseUnary();
                return new Unary { Op = "not", E = e, Line = line };
            }
            return ParsePrimary();
        }

        private Expr ParsePrimary()
        {
            var t = Cur;
            if (t.Type == TT.Number) { Eat(); return new NumberLit { Value = t.Num, Line = t.Line }; }
            if (t.Type == TT.String) { Eat(); return new StringLit { Value = t.Text, Line = t.Line }; }
            if (IsKw("true")) { Eat(); return new BoolLit { Value = true, Line = t.Line }; }
            if (IsKw("false")) { Eat(); return new BoolLit { Value = false, Line = t.Line }; }
            if (IsKw("nil")) { Eat(); return new NilLit { Line = t.Line }; }
            if (IsOp("{")) return ParseTableLit();
            if (IsOp("("))
            {
                Eat();
                var e = ParseExpr();
                ExpectOp(")");
                return ParsePostfix(e);
            }
            if (t.Type == TT.Name)
            {
                if (t.Text == "ll" && Next.Type == TT.Op && Next.Text == ".")
                    return ParseLlCall();
                if ((t.Text == "type" || t.Text == "tostring" || t.Text == "tonumber")
                    && Next.Type == TT.Op && Next.Text == "(")
                {
                    Eat();                  // builtin name
                    ExpectOp("(");
                    var arg = ParseExpr();
                    ExpectOp(")");
                    return new Builtin { Name = t.Text, Arg = arg, Line = t.Line };
                }
                return ParsePrefixExpr();
            }
            Err("expected expression");
            return null;
        }

        // A prefix expression: a Name followed by a postfix chain of .field / [key] indexing.
        private Expr ParsePrefixExpr()
        {
            var t = Cur;
            if (t.Type != TT.Name) { Err("expected name"); return null; }
            Eat();
            Expr e = new NameRef { Name = t.Text, Line = t.Line };
            return ParsePostfix(e);
        }

        private Expr ParsePostfix(Expr e)
        {
            while (true)
            {
                if (IsOp("."))
                {
                    int line = Cur.Line; Eat();
                    if (Cur.Type != TT.Name && Cur.Type != TT.Keyword) Err("expected field name after '.'");
                    string field = Eat().Text;
                    e = new Index { Target = e, Key = new StringLit { Value = field, Line = line }, Line = line };
                }
                else if (IsOp("["))
                {
                    int line = Cur.Line; Eat();
                    var key = ParseExpr();
                    ExpectOp("]");
                    e = new Index { Target = e, Key = key, Line = line };
                }
                else break;
            }
            return e;
        }

        // { } | { e1, e2, ... } | { x = v, ... } | { [k] = v, ... } | mixed
        private Expr ParseTableLit()
        {
            int line = Cur.Line; ExpectOp("{");
            var fields = new List<TableField>();
            while (!IsOp("}"))
            {
                if (IsOp("["))
                {
                    Eat();
                    var k = ParseExpr();
                    ExpectOp("]");
                    ExpectOp("=");
                    var v = ParseExpr();
                    fields.Add(new TableField { Key = k, Value = v });
                }
                else if (Cur.Type == TT.Name && Next.Type == TT.Op && Next.Text == "=")
                {
                    string name = Eat().Text; // field name
                    Eat();                    // '='
                    var v = ParseExpr();
                    fields.Add(new TableField { Key = new StringLit { Value = name, Line = line }, Value = v });
                }
                else
                {
                    var v = ParseExpr();
                    fields.Add(new TableField { Key = null, Value = v }); // array element
                }

                if (IsOp(",") || IsOp(";")) { Eat(); continue; }
                break;
            }
            ExpectOp("}");
            return new TableLit { Fields = fields, Line = line };
        }

        private LlCall ParseLlCall()
        {
            int line = Cur.Line;
            // 'll'
            if (!(Cur.Type == TT.Name && Cur.Text == "ll")) Err("expected 'll'");
            Eat();
            ExpectOp(".");
            if (Cur.Type != TT.Name) Err("expected ll member name");
            string member = Eat().Text;
            ExpectOp("(");
            var args = new List<Expr>();
            if (!IsOp(")"))
            {
                while (true)
                {
                    args.Add(ParseExpr());
                    if (IsOp(",")) { Eat(); continue; }
                    break;
                }
            }
            ExpectOp(")");
            return new LlCall { Member = member, Args = args, Line = line };
        }
    }

    // ======================================================================================
    // Code generator: AST -> Phlox assembly text
    // ======================================================================================
    internal sealed class SLuaCodeGen
    {
        private StringBuilder _sb = new StringBuilder();
        private readonly SupportedEventList _events = new SupportedEventList();

        // top-level locals -> global slots (with type)
        private readonly Dictionary<string, VarVar> _globals = new Dictionary<string, VarVar>();
        private int _labelCounter;

        // current function/handler local scope (param + inner local -> slot/type)
        private Dictionary<string, VarVar> _locals;
        private int _nextLocalSlot;

        // Pseudo-type for dynamically-typed values (table reads, nil, table literals). No static
        // cast is emitted for these; the VM coerces at consumption (ConvToFloat/Int in arithmetic
        // ops, the syscall shim for ll args). This is the seam to the future dynamic-typing piece.
        private const VarType Dynamic = (VarType)99;

        private struct VarVar { public int Slot; public VarType Type; public VarVar(int s, VarType t) { Slot = s; Type = t; } }

        private int AllocNamedLocal(string name, VarType type) { int s = _nextLocalSlot++; _locals[name] = new VarVar(s, type); return s; }
        private int AllocTempLocal() { return _nextLocalSlot++; }

        public string Generate(List<Stmt> chunk)
        {
            var topLocals = new List<LocalDecl>();
            var funcs = new List<FuncDecl>();
            var execStmts = new List<Stmt>();

            foreach (var s in chunk)
            {
                if (s is LocalDecl ld) topLocals.Add(ld);
                else if (s is FuncDecl fd) funcs.Add(fd);
                else execStmts.Add(s);
            }

            bool explicitStateEntry = funcs.Exists(f => f.Name == "state_entry");
            if (explicitStateEntry && execStmts.Count > 0)
                throw new SLuaException("top-level code and an explicit state_entry() are both present; not supported (use one)", execStmts[0].Line);

            // ---- header + globals-init block ----
            Line(".globals " + topLocals.Count);
            Line(".statedef default");
            Line("");
            _locals = null; // global-init runs in no local frame
            for (int i = 0; i < topLocals.Count; i++)
            {
                var ld = topLocals[i];
                if (_globals.ContainsKey(ld.Name))
                    throw new SLuaException("duplicate top-level local '" + ld.Name + "'", ld.Line);
                VarType t = EmitExpr(ld.Init);          // register AFTER emit so init can't self-ref
                _globals[ld.Name] = new VarVar(i, t);   // global keeps its declared/init type
                Line("gstore " + i);                    // store natural value (no forced cast)
            }
            Line("halt");
            Line("");

            // ---- synthesized state_entry from top-level code ----
            if (execStmts.Count > 0)
                EmitHandler("state_entry", new List<string>(), execStmts, execStmts[0].Line);

            // ---- event handlers ----
            foreach (var f in funcs)
            {
                if (!_events.HasEventByName(f.Name))
                    throw new SLuaException("'" + f.Name + "' is not a known event; user functions are Tier-2+", f.Line);
                EmitHandler(f.Name, f.Params, f.Body, f.Line);
            }

            return _sb.ToString();
        }

        private void EmitHandler(string eventName, List<string> declaredParams, List<Stmt> body, int line)
        {
            var eventArgs = new List<VarType>(_events.GetArguments(eventName));
            if (declaredParams.Count > eventArgs.Count)
                throw new SLuaException("event '" + eventName + "' takes " + eventArgs.Count +
                                        " parameter(s); " + declaredParams.Count + " declared", line);

            _locals = new Dictionary<string, VarVar>();
            // params occupy slots 0..eventArgs.Count-1 with the event's arg types
            for (int i = 0; i < declaredParams.Count; i++)
                _locals[declaredParams[i]] = new VarVar(i, eventArgs[i]);
            _nextLocalSlot = eventArgs.Count;

            // Two-pass: emit the body into a temp buffer while allocating locals/temps on demand,
            // then emit the header with the final locals count (the .evt header precedes the body).
            StringBuilder outer = _sb;
            StringBuilder bodyBuf = new StringBuilder();
            _sb = bodyBuf;
            try { EmitBlock(body); }
            finally { _sb = outer; }

            int innerLocals = _nextLocalSlot - eventArgs.Count;
            Line(".evt default/" + eventName + ": args=" + eventArgs.Count + ", locals=" + innerLocals);
            _sb.Append(bodyBuf.ToString());
            Line("ret");
            Line("");
            _locals = null;
        }

        private void EmitBlock(List<Stmt> stmts)
        {
            foreach (var s in stmts) EmitStmt(s);
        }

        private void EmitStmt(Stmt s)
        {
            switch (s)
            {
                case LocalDecl ld:
                {
                    VarType t = EmitExpr(ld.Init);
                    int slot = _locals.TryGetValue(ld.Name, out var ex) ? ex.Slot : AllocNamedLocal(ld.Name, t);
                    // record the local's type (re-decl in the flat model just updates type)
                    _locals[ld.Name] = new VarVar(slot, t);
                    Line("store " + slot);
                    break;
                }
                case Assign a:
                {
                    VarType t = EmitExpr(a.Value);
                    if (_locals != null && _locals.TryGetValue(a.Name, out var lv))
                    {
                        Coerce(t, lv.Type, a.Line);
                        Line("store " + lv.Slot);
                    }
                    else if (_globals.TryGetValue(a.Name, out var gv))
                    {
                        Coerce(t, gv.Type, a.Line);
                        Line("gstore " + gv.Slot);
                    }
                    else throw new SLuaException("assignment to undeclared variable '" + a.Name + "'", a.Line);
                    break;
                }
                case IndexAssign ia:
                {
                    EmitExpr(ia.Target);   // table
                    EmitExpr(ia.Key);      // key (float number keys normalized to int in the VM)
                    EmitExpr(ia.Value);    // value (stored with its natural type)
                    Line("tabset");
                    break;
                }
                case TableInsert ti:
                    EmitTableInsert(ti);
                    break;
                case ExprStmt es:
                    EmitLlCall(es.Call, statementLevel: true);
                    break;
                case IfStmt ifs:
                    EmitIf(ifs);
                    break;
                case WhileStmt w:
                    EmitWhile(w);
                    break;
                case ForIn fi:
                    EmitForIn(fi);
                    break;
                case ReturnStmt r:
                    if (r.Value != null) EmitExpr(r.Value); // value discarded for void events
                    Line("ret");
                    break;
                default:
                    throw new SLuaException("unsupported statement", s.Line);
            }
        }

        private void EmitTableInsert(TableInsert ti)
        {
            // table.insert(t, v) == t[#t+1] = v. Tier-2: t must be a simple name (evaluated twice).
            if (!(ti.Table is NameRef))
                throw new SLuaException("table.insert's first argument must be a simple variable in the Tier-2 subset", ti.Line);

            EmitExpr(ti.Table);   // table (for tabset, deepest on stack)
            EmitExpr(ti.Table);   // table (for tablen)
            Line("tablen");       // -> int length
            Line("iconst 1");
            Line("iadd");         // key = #t + 1 (int)
            EmitExpr(ti.Value);   // value
            Line("tabset");
        }

        private void EmitForIn(ForIn f)
        {
            // for k [, v] in pairs(t) do ... end  (insertion-ordered next() protocol)
            int tSlot = AllocTempLocal();   // _t : the table
            int kSlot = AllocTempLocal();   // _k : iteration cursor
            int var0 = AllocNamedLocal(f.Vars[0], Dynamic);
            int var1 = (f.Vars.Count >= 2) ? AllocNamedLocal(f.Vars[1], Dynamic) : -1;

            EmitExpr(f.TableExpr);
            Line("store " + tSlot);   // _t = table
            Line("pushnil");
            Line("store " + kSlot);   // _k = nil (start)

            string top = NewLabel("forin");
            string end = NewLabel("forend");
            Label(top);
            Line("load " + tSlot);
            Line("load " + kSlot);
            Line("tabnext");          // stack: [nextValue, nextKey]
            Line("store " + kSlot);   // _k = nextKey (top)
            if (var1 >= 0) Line("store " + var1);  // v = nextValue
            else Line("pop");                      // (single-var for: discard value)
            Line("load " + kSlot);
            Line("isnil");
            Line("brt " + end);       // cursor exhausted -> done
            Line("load " + kSlot);
            Line("store " + var0);    // k = _k
            EmitBlock(f.Body);
            Line("jmp " + top);
            Label(end);
        }

        private void EmitIf(IfStmt ifs)
        {
            string elseL = NewLabel("else");
            EmitCondBool(ifs.Cond);
            Line("brf " + elseL);
            EmitBlock(ifs.Then);
            if (ifs.Else != null && ifs.Else.Count > 0)
            {
                string endL = NewLabel("endif");
                Line("jmp " + endL);
                Label(elseL);
                EmitBlock(ifs.Else);
                Label(endL);
            }
            else
            {
                Label(elseL);
            }
        }

        private void EmitWhile(WhileStmt w)
        {
            string topL = NewLabel("while");
            string endL = NewLabel("wend");
            Label(topL);
            EmitCondBool(w.Cond);
            Line("brf " + endL);
            EmitBlock(w.Body);
            Line("jmp " + topL);
            Label(endL);
        }

        // Emit a condition leaving an integer boolean on the stack (for brf/brt).
        // Emit a condition leaving an int 1/0 on the stack (for brf/brt), using correct Lua
        // truthiness (only nil and false are falsy; 0 and "" are truthy).
        private void EmitCondBool(Expr cond)
        {
            EmitExpr(cond);
            Line("luatruthy");
        }

        // ---- expressions ----
        private VarType EmitExpr(Expr e)
        {
            switch (e)
            {
                case NumberLit n:
                    Line("fconst " + FormatFloat(n.Value));
                    return VarType.Float;
                case StringLit s:
                    Line("sconst \"" + EscapeString(s.Value) + "\"");
                    return VarType.String;
                case BoolLit b:
                    Line(b.Value ? "pushtrue" : "pushfalse");
                    return Dynamic;
                case NameRef nr:
                    return EmitNameRef(nr);
                case Unary u:
                {
                    if (u.Op == "not") { EmitExpr(u.E); Line("lnot"); return Dynamic; }
                    VarType t = EmitExpr(u.E);
                    Coerce(t, VarType.Float, u.Line);
                    Line("fneg");
                    return VarType.Float;
                }
                case Builtin bi:
                    EmitExpr(bi.Arg);
                    Line(bi.Name == "type" ? "luatype" : bi.Name == "tostring" ? "luatostr" : "luatonum");
                    return bi.Name == "tonumber" ? Dynamic : VarType.String;
                case Binary bin:
                    return EmitBinary(bin);
                case LlCall c:
                    return EmitLlCall(c, statementLevel: false);
                case NilLit:
                    Line("pushnil");
                    return Dynamic;
                case TableLit tl:
                    return EmitTableLit(tl);
                case Index ix:
                    EmitExpr(ix.Target);   // table
                    EmitExpr(ix.Key);      // key
                    Line("tabget");
                    return Dynamic;        // value type only known at runtime
                case Len len:
                    EmitExpr(len.E);       // table
                    Line("tablen");
                    return VarType.Integer;
                default:
                    throw new SLuaException("unsupported expression", e.Line);
            }
        }

        private VarType EmitTableLit(TableLit tl)
        {
            int arrayIndex = 1;
            foreach (var field in tl.Fields)
            {
                if (field.Key == null)
                {
                    Line("iconst " + arrayIndex);  // positional key (1-based int)
                    arrayIndex++;
                }
                else
                {
                    EmitExpr(field.Key);           // string name, or [k] expr (normalized in VM)
                }
                EmitExpr(field.Value);             // value stored with its natural type
            }
            Line("buildtable " + tl.Fields.Count);
            return Dynamic;
        }

        private VarType EmitNameRef(NameRef nr)
        {
            if (_locals != null && _locals.TryGetValue(nr.Name, out var lv))
            {
                Line("load " + lv.Slot);
                return lv.Type;
            }
            if (_globals.TryGetValue(nr.Name, out var gv))
            {
                Line("gload " + gv.Slot);
                return gv.Type;
            }
            throw new SLuaException("reference to undeclared variable '" + nr.Name + "'", nr.Line);
        }

        private VarType EmitBinary(Binary bin)
        {
            // short-circuit logical operators return a value (not necessarily boolean)
            if (bin.Op == "and" || bin.Op == "or") return EmitAndOr(bin);

            // string concat: '..' coerces number/string operands in the VM
            if (bin.Op == "..")
            {
                EmitExpr(bin.L);
                EmitExpr(bin.R);
                Line("concat");
                return VarType.String;
            }

            // nil comparison: x == nil / x ~= nil
            if ((bin.Op == "==" || bin.Op == "~=") && (bin.L is NilLit || bin.R is NilLit))
            {
                Expr other = (bin.L is NilLit) ? bin.R : bin.L;
                EmitExpr(other);
                Line("isnil");
                Line("tobool");                  // -> boolean (is nil)
                if (bin.Op == "~=") Line("lnot");
                return Dynamic;
            }

            // Lua equality (type-aware: different types are never equal)
            if (bin.Op == "==" || bin.Op == "~=")
            {
                EmitExpr(bin.L);
                EmitExpr(bin.R);
                Line("luaeq");                   // -> boolean
                if (bin.Op == "~=") Line("lnot");
                return Dynamic;
            }

            // arithmetic + relational: operands coerced to number by the VM ops at consumption
            EmitExpr(bin.L);
            EmitExpr(bin.R);
            switch (bin.Op)
            {
                case "+": Line("fadd"); return VarType.Float;
                case "-": Line("fsub"); return VarType.Float;
                case "*": Line("fmul"); return VarType.Float;
                case "/": Line("fdiv"); return VarType.Float;
                case "%": Line("fmod"); return VarType.Float;
                case "<": Line("flt"); Line("tobool"); return Dynamic;
                case ">": Line("fgt"); Line("tobool"); return Dynamic;
                case "<=": Line("flte"); Line("tobool"); return Dynamic;
                case ">=": Line("fgte"); Line("tobool"); return Dynamic;
                default: throw new SLuaException("unsupported operator '" + bin.Op + "'", bin.Line);
            }
        }

        // a and b: a if a is falsy, else b.   a or b: a if a is truthy, else b.  (short-circuit)
        private VarType EmitAndOr(Binary bin)
        {
            string skip = NewLabel(bin.Op == "and" ? "and" : "or");
            EmitExpr(bin.L);                                   // [a]
            Line("dup");                                       // [a, a]
            Line("luatruthy");                                 // [a, t]
            Line((bin.Op == "and" ? "brf " : "brt ") + skip);  // and: keep a if falsy; or: keep a if truthy
            Line("pop");                                       // discard a
            EmitExpr(bin.R);                                   // [b]
            Label(skip);
            return Dynamic;
        }

        private VarType EmitLlCall(LlCall c, bool statementLevel)
        {
            string phloxName = "ll" + c.Member; // ll.Say -> llSay
            if (!Defaults.SystemMethods.TryGetValue(phloxName, out FunctionSig sig))
                throw new SLuaException("unknown ll function 'll." + c.Member + "' (-> " + phloxName + ")", c.Line);

            if (c.Args.Count != sig.ParamTypes.Length)
                throw new SLuaException("ll." + c.Member + " expects " + sig.ParamTypes.Length +
                                        " argument(s), got " + c.Args.Count, c.Line);

            for (int i = 0; i < c.Args.Count; i++)
            {
                VarType at = EmitExpr(c.Args[i]);
                Coerce(at, sig.ParamTypes[i], c.Line);
            }
            Line("syscall " + phloxName + "()");

            if (sig.ReturnType == VarType.Void)
            {
                if (!statementLevel)
                    throw new SLuaException("ll." + c.Member + " returns no value; cannot use in an expression", c.Line);
                return VarType.Void;
            }
            if (statementLevel) Line("pop"); // discard unused return value
            return sig.ReturnType;
        }

        // ---- coercion (uses existing cast opcodes; NO VM change) ----
        private void Coerce(VarType from, VarType to, int line)
        {
            if (from == to) return;
            if (from == Dynamic || to == Dynamic) return;   // VM coerces at consumption (no static cast)
            if (to == VarType.Integer && from == VarType.Float) { Line("icast"); return; }
            if (to == VarType.Float && from == VarType.Integer) { Line("fcast"); return; }
            // Remaining conversions (e.g. number<->string) are performed by the VM ops / syscall shim
            // at the point of consumption (ConvToInt/ConvToFloat/ConvToStr). Emit nothing rather than
            // fail, so the value flows to its coercing consumer.
        }

        // ---- emit helpers ----
        private void Line(string s) { _sb.Append(s); _sb.Append('\n'); }
        private void Label(string name) { _sb.Append(name); _sb.Append(":\n"); }
        private string NewLabel(string tag) { return "sl_" + tag + "_" + (_labelCounter++); }

        private static string FormatFloat(double d)
        {
            // Must match the assembler FLOAT token (INT '.' INT*); no exponent form.
            string s = d.ToString("0.0###############", CultureInfo.InvariantCulture);
            if (s.IndexOf('.') < 0) s += ".0";
            return s;
        }

        private static string EscapeString(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': break; // assembler STRING has no \r escape; drop
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
