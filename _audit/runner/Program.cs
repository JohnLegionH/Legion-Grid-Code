using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

using InWorldz.Phlox.Types;

namespace PhloxConformance
{
    static class Bootstrap
    {
        public static readonly string BinDir = @"D:\legion-grid-source\bin";
        [System.Runtime.CompilerServices.ModuleInitializer]
        public static void Init()
        {
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                var probe = Path.Combine(BinDir, name.Name + ".dll");
                if (File.Exists(probe))
                {
                    try { return ctx.LoadFromAssemblyPath(probe); } catch { }
                }
                return null;
            };
        }
    }

    class CollectingListener : ILSLListener
    {
        public List<string> Errors = new List<string>();
        public List<string> Info = new List<string>();
        public bool _hasErrors;
        public void Info_(string m) { }
        void ILSLListener.Info(string message) { Info.Add(message); }
        void ILSLListener.Error(string message) { Errors.Add(message); _hasErrors = true; }
        bool ILSLListener.HasErrors() { return _hasErrors; }
        void ILSLListener.CompilationFinished() { }
    }

    enum Verdict { CONFORMANT, UNDER, OVER, ERROR_RUNNING }

    class Case
    {
        public string Id;
        public string Category;
        public bool ExpectPass;
        public string Why;
        public string Source;

        public Case(string id, string category, bool expectPass, string why, string source)
        { Id = id; Category = category; ExpectPass = expectPass; Why = why; Source = source; }
    }

    class Result
    {
        public Case Case;
        public bool PhloxPass;
        public string FirstError;
        public string Exception;
        public Verdict Verdict;
    }

    class Program
    {
        static string Wrap(string body)
        {
            return body.Contains("default") || body.Contains("state ") ? body :
                "default { state_entry() {\n" + body + "\n} }";
        }

        static List<Case> BuildCorpus()
        {
            var c = new List<Case>();

            // ============ OPERATORS — valid SL forms, expect PASS ============
            void Op(string id, string body, string why)
                => c.Add(new Case(id, "operator", true, why, Wrap("integer a;integer b;integer r;\n" + body)));
            void OpFail(string id, string body, string why)
                => c.Add(new Case(id, "operator-invalid", false, why, Wrap("integer a;integer b;integer r;\n" + body)));

            Op("op-add",         "r = a + b;",  "additive +");
            Op("op-sub",         "r = a - b;",  "additive -");
            Op("op-mul",         "r = a * b;",  "multiplicative *");
            Op("op-div",         "r = a / b;",  "multiplicative /");
            Op("op-mod",         "r = a % b;",  "multiplicative %");
            Op("op-shl",         "r = a << b;", "shift <<");
            Op("op-shr",         "r = a >> b;", "shift >>");
            Op("op-lt",          "r = (a < b);", "relational <");
            Op("op-le",          "r = (a <= b);", "relational <=");
            Op("op-gt",          "r = (a > b);", "relational >");
            Op("op-ge",          "r = (a >= b);", "relational >=");
            Op("op-eq",          "r = (a == b);", "equality ==");
            Op("op-ne",          "r = (a != b);", "equality !=");
            Op("op-band",        "r = a & b;",  "bitwise &");
            Op("op-bxor",        "r = a ^ b;",  "bitwise ^");
            Op("op-bor",         "r = a | b;",  "bitwise |");
            Op("op-and",         "r = a && b;", "logical &&");
            Op("op-or",          "r = a || b;", "logical ||");
            Op("op-assign",      "r = a;",      "assign =");
            Op("op-assign-add",  "r += a;",     "assign +=");
            Op("op-assign-sub",  "r -= a;",     "assign -=");
            Op("op-assign-mul",  "r *= a;",     "assign *=");
            Op("op-assign-div",  "r /= a;",     "assign /=");
            Op("op-assign-mod",  "r %= a;",     "assign %=");

            Op("op-unary-minus", "r = -a;",     "unary -");
            Op("op-unary-bang",  "r = !a;",     "unary !");
            Op("op-unary-tilde", "r = ~a;",     "unary ~");
            Op("op-preinc",      "++a; r = a;", "pre-increment");
            Op("op-predec",      "--a; r = a;", "pre-decrement");
            Op("op-postinc",     "a++; r = a;", "post-increment");
            Op("op-postdec",     "a--; r = a;", "post-decrement");

            // ============ OPERATORS — invalid C-isms that SL rejects, expect FAIL ============
            OpFail("op-ternary",    "r = (a > b) ? 1 : 2;", "ternary ?: does not exist in LSL");
            OpFail("op-bor-assign", "r |= a;",              "|= does not exist in LSL");
            OpFail("op-band-assign","r &= a;",              "&= does not exist in LSL");
            OpFail("op-bxor-assign","r ^= a;",              "^= does not exist in LSL");
            OpFail("op-shl-assign", "r <<= a;",             "<<= does not exist in LSL");
            OpFail("op-shr-assign", "r >>= a;",             ">>= does not exist in LSL");

            // ============ TYPE CASTS ============
            void Cast(string id, string body, bool expectPass, string why)
                => c.Add(new Case(id, "cast", expectPass, why, Wrap(body)));

            Cast("cast-int-to-string", "string s = (string)42;", true, "integer to string");
            Cast("cast-int-to-float",  "float f = (float)42;",   true, "integer to float");
            Cast("cast-float-to-int",  "integer i = (integer)3.7;", true, "float to int");
            Cast("cast-string-to-int", "integer i = (integer)\"42\";", true, "string to int");
            Cast("cast-string-to-float","float f = (float)\"3.14\";",  true, "string to float");
            Cast("cast-string-to-key", "key k = (key)\"00000000-0000-0000-0000-000000000000\";", true, "string to key");
            Cast("cast-key-to-string", "string s = (string)((key)\"00000000-0000-0000-0000-000000000000\");", true, "key to string");
            Cast("cast-vec-to-string", "string s = (string)<1.0,2.0,3.0>;", true, "vector to string");
            Cast("cast-string-to-vec", "vector v = (vector)\"<1.0,2.0,3.0>\";", true, "string to vector");
            Cast("cast-rot-to-string", "string s = (string)<1.0,2.0,3.0,4.0>;", true, "rotation to string");
            Cast("cast-string-to-rot", "rotation r = (rotation)\"<1.0,2.0,3.0,4.0>\";", true, "string to rotation");
            Cast("cast-list-to-string","string s = (string)[1,2,3];", true, "list to string");
            Cast("cast-int-to-list",   "list l = (list)42;",       false, "SL does NOT allow direct (list)int — must use [val]");

            // ============ VECTOR/ROTATION ARITHMETIC ============
            void V(string id, string body, bool expectPass, string why)
                => c.Add(new Case(id, "vector-rotation", expectPass, why, Wrap("vector v=<1.0,2.0,3.0>; vector w=<4.0,5.0,6.0>; rotation r=<0.0,0.0,0.0,1.0>; rotation q=<0.0,0.0,1.0,0.0>;\n" + body)));

            V("vec-add",       "vector x = v + w;", true,  "vector + vector");
            V("vec-sub",       "vector x = v - w;", true,  "vector - vector");
            V("vec-mul-scalar","vector x = v * 2.0;", true,"vector * scalar");
            V("vec-div-scalar","vector x = v / 2.0;", true,"vector / scalar");
            V("vec-dot",       "float f = v * w;",   true, "vector dot product");
            V("vec-cross",     "vector x = v % w;",  true, "vector cross product (%)");
            V("vec-mul-rot",   "vector x = v * r;",  true, "vector * rotation rotates the vector");
            V("vec-div-rot",   "vector x = v / r;",  true, "vector / rotation inverse-rotates");
            V("rot-mul",       "rotation x = r * q;",true, "rotation * rotation");
            V("rot-div",       "rotation x = r / q;",true, "rotation / rotation");
            V("rot-add",       "rotation x = r + q;",false,"SL does NOT define rotation + rotation");
            V("rot-sub",       "rotation x = r - q;",false,"SL does NOT define rotation - rotation");

            // ============ LIST OPERATIONS ============
            void L(string id, string body, bool expectPass, string why)
                => c.Add(new Case(id, "list", expectPass, why, Wrap(body)));

            L("list-empty",   "list l = [];",            true, "empty list literal");
            L("list-mixed",   "list l = [1, 2.0, \"x\", (key)\"00000000-0000-0000-0000-000000000000\", <1.0,0.0,0.0>];", true, "mixed-type list literal");
            L("list-concat",  "list l = [1] + [2];",     true, "list concatenation");
            L("list-promote", "list l = [1] + 2;",       true, "scalar promoted on concat");
            L("list-list-mul","list l = [1] * [2];",     false,"SL does NOT define list * list");

            // ============ EVENTS — diff against SupportedEventList ============
            // For canonical SL events that should compile
            void Ev(string id, string evtSig, bool expectPass, string why)
                => c.Add(new Case("event-" + id, "event", expectPass, why,
                    "default { state_entry() {} " + evtSig + " {} }"));

            Ev("at_rot_target",            "at_rot_target(integer h, rotation a, rotation b)",                       true, "at_rot_target");
            Ev("at_target",                "at_target(integer h, vector a, vector b)",                               true, "at_target");
            Ev("attach",                   "attach(key id)",                                                          true, "attach");
            Ev("changed",                  "changed(integer change)",                                                 true, "changed");
            Ev("collision",                "collision(integer total)",                                                true, "collision");
            Ev("collision_start",          "collision_start(integer total)",                                          true, "collision_start");
            Ev("collision_end",            "collision_end(integer total)",                                            true, "collision_end");
            Ev("control",                  "control(key id, integer held, integer change)",                           true, "control");
            Ev("dataserver",               "dataserver(key qid, string data)",                                        true, "dataserver");
            Ev("email",                    "email(string time, string addr, string subj, string msg, integer queued)",true, "email");
            Ev("http_response",            "http_response(key id, integer status, list meta, string body)",           true, "http_response");
            Ev("http_request",             "http_request(key id, string method, string body)",                        true, "http_request");
            Ev("land_collision",           "land_collision(vector pos)",                                              true, "land_collision");
            Ev("land_collision_start",     "land_collision_start(vector pos)",                                        true, "land_collision_start");
            Ev("land_collision_end",       "land_collision_end(vector pos)",                                          true, "land_collision_end");
            Ev("link_message",             "link_message(integer sender, integer num, string str, key id)",           true, "link_message");
            Ev("listen",                   "listen(integer ch, string name, key id, string msg)",                     true, "listen");
            Ev("money",                    "money(key giver, integer amount)",                                        true, "money");
            Ev("moving_start",             "moving_start()",                                                          true, "moving_start");
            Ev("moving_end",               "moving_end()",                                                            true, "moving_end");
            Ev("no_sensor",                "no_sensor()",                                                             true, "no_sensor");
            Ev("not_at_rot_target",        "not_at_rot_target()",                                                     true, "not_at_rot_target");
            Ev("not_at_target",            "not_at_target()",                                                         true, "not_at_target");
            Ev("object_rez",               "object_rez(key id)",                                                      true, "object_rez");
            Ev("on_rez",                   "on_rez(integer param)",                                                   true, "on_rez");
            Ev("remote_data",              "remote_data(integer t, key ch, key msg, string sdr, integer i, string s)",true, "remote_data");
            Ev("run_time_permissions",     "run_time_permissions(integer perm)",                                      true, "run_time_permissions");
            Ev("sensor",                   "sensor(integer total)",                                                   true, "sensor");
            Ev("state_exit",               "state_exit()",                                                            true, "state_exit");
            Ev("timer",                    "timer()",                                                                 true, "timer");
            Ev("touch",                    "touch(integer total)",                                                    true, "touch");
            Ev("touch_start",              "touch_start(integer total)",                                              true, "touch_start");
            Ev("touch_end",                "touch_end(integer total)",                                                true, "touch_end");
            Ev("transaction_result",       "transaction_result(key tid, integer success, string data)",               true, "transaction_result");
            Ev("linkset_data",             "linkset_data(integer action, string name, string value)",                 true, "linkset_data");
            Ev("experience_permissions",   "experience_permissions(key id)",                                          true, "experience_permissions");
            Ev("experience_permissions_denied","experience_permissions_denied(key id, integer reason)",               true, "experience_permissions_denied");
            // Events SL has that Phlox MAY lack — expect they currently fail in Phlox
            Ev("path_update",              "path_update(integer t, list d)",                                          true, "path_update — pathfinding event in SL");
            Ev("game_control",             "game_control(key id, integer button, list axes)",                         true, "game_control — newer SL");
            Ev("on_damage",                "on_damage(integer count)",                                                true, "on_damage — combat 2.0");
            Ev("on_death",                 "on_death()",                                                              true, "on_death — combat 2.0");
            Ev("final_damage",             "final_damage(integer count)",                                             true, "final_damage — combat 2.0");
            // Bogus event — should fail
            Ev("bogus_event_xyz",          "this_is_not_a_real_event_xyz(integer x)",                                 false, "made-up event name should be rejected");

            // ============ STATEMENTS / FLOW CONTROL ============
            void S(string id, string body, bool expectPass, string why)
                => c.Add(new Case("stmt-" + id, "statement", expectPass, why, Wrap(body)));

            S("if",        "integer x; if (x > 0) { x = 1; }", true, "if");
            S("if-else",   "integer x; if (x > 0) { x = 1; } else { x = 2; }", true, "if/else");
            S("while",     "integer x; while (x < 10) { x++; }", true, "while");
            S("do-while",  "integer x; do { x++; } while (x < 10);", true, "do/while");
            S("for",       "integer i; for (i = 0; i < 10; ++i) { }", true, "for");
            S("for-empty-init","integer i = 0; for (; i < 10; ++i) { }", true, "for with empty init");
            S("jump-label","@start; jump start;", true, "jump/label");
            S("return-void","", true, "function with no explicit return is fine in default block");
            S("multi-return-paths","integer x;\n if (x) return; else return;", true, "valueless return in void event is valid in SL");
            S("nested-block","{ { integer x = 1; } }", true, "nested anonymous blocks");

            // Function-level: missing return path on a typed function
            c.Add(new Case("stmt-missing-return", "statement", false,
                "function returning integer must return on all paths",
                "integer f() { integer x; if (x) return 1; }\ndefault { state_entry() {} }"));
            c.Add(new Case("stmt-return-typed-from-void", "statement", false,
                "void function cannot return a value",
                "f() { return 1; }\ndefault { state_entry() {} }"));

            // Additional type/keyword tests
            c.Add(new Case("type-quaternion", "type", true,
                "SL accepts 'quaternion' as an alias for 'rotation'",
                "default { state_entry() { quaternion q = <0.0,0.0,0.0,1.0>; q = q; } }"));
            c.Add(new Case("type-quaternion-cast", "type", true,
                "SL accepts (quaternion) cast",
                "default { state_entry() { rotation r = (quaternion)<0.0,0.0,0.0,1.0>; r = r; } }"));

            // Function return-type tests
            c.Add(new Case("func-typed-correct-return", "function", true,
                "typed function with correct return",
                "integer f(integer x) { return x + 1; }\ndefault { state_entry() {} }"));
            c.Add(new Case("func-multi-typed-correct", "function", true,
                "typed function with multiple return paths",
                "integer f(integer x) { if (x > 0) return 1; else return 2; }\ndefault { state_entry() {} }"));
            c.Add(new Case("func-typed-wrong-return-type", "function", false,
                "string function returning integer should fail",
                "string f() { return 42; }\ndefault { state_entry() {} }"));

            // Empty-arg function call
            c.Add(new Case("func-no-args-call", "function", true,
                "calling a no-arg function",
                "f() { } default { state_entry() { f(); } }"));

            // ============ STRING LITERALS / ESCAPES ============
            void Lit(string id, string body, bool expectPass, string why)
                => c.Add(new Case("lit-" + id, "literal", expectPass, why, Wrap(body)));

            Lit("string-basic",   "string s = \"hello\";",  true,  "basic string");
            Lit("string-newline", "string s = \"a\\nb\";",  true,  "newline escape");
            Lit("string-tab",     "string s = \"a\\tb\";",  true,  "tab escape");
            Lit("string-quote",   "string s = \"a\\\"b\";", true,  "escaped quote");
            Lit("string-backslash","string s = \"a\\\\b\";",true,  "escaped backslash");
            Lit("int-hex",        "integer i = 0xFF;",     true,   "hex integer literal");
            Lit("float-exp",      "float f = 1.5e3;",      true,   "float with exponent");
            Lit("float-neg-exp",  "float f = 1.5e-3;",     true,   "float with negative exponent");

            return c;
        }

        static Result Run(Case kase, string templatePath)
        {
            var listener = new CollectingListener();
            var r = new Result { Case = kase };
            try
            {
                var fe = new InWorldz.Phlox.Glue.CompilerFrontend(listener, templatePath, true);
                fe.Compile(kase.Source);
                r.PhloxPass = !((ILSLListener)listener).HasErrors();
                r.FirstError = listener.Errors.FirstOrDefault();
            }
            catch (Exception ex)
            {
                r.PhloxPass = false;
                r.Exception = ex.GetType().Name + ": " + ex.Message;
                r.FirstError = listener.Errors.FirstOrDefault();
            }
            r.Verdict = (kase.ExpectPass == r.PhloxPass) ? Verdict.CONFORMANT
                       : (kase.ExpectPass ? Verdict.UNDER : Verdict.OVER);
            return r;
        }

        static int Main(string[] args)
        {
            // grammar path: not actually used by Compile() in current CompilerFrontend
            string templatePath = args.Length > 0 ? args[0] : "";

            var corpus = BuildCorpus();
            var results = corpus.Select(k => Run(k, templatePath)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("id\tcategory\texpect\tphlox_result\tverdict\tfirst_error\texception");
            foreach (var r in results)
            {
                sb.Append(r.Case.Id).Append('\t');
                sb.Append(r.Case.Category).Append('\t');
                sb.Append(r.Case.ExpectPass ? "PASS" : "FAIL").Append('\t');
                sb.Append(r.PhloxPass ? "PASS" : "FAIL").Append('\t');
                sb.Append(r.Verdict).Append('\t');
                sb.Append((r.FirstError ?? "").Replace('\t',' ').Replace('\n',' ').Replace('\r',' ')).Append('\t');
                sb.Append((r.Exception ?? "").Replace('\t',' ').Replace('\n',' ').Replace('\r',' '));
                sb.AppendLine();
            }

            Console.Out.Write(sb.ToString());

            // Summary on stderr
            int conf = results.Count(r => r.Verdict == Verdict.CONFORMANT);
            int under = results.Count(r => r.Verdict == Verdict.UNDER);
            int over = results.Count(r => r.Verdict == Verdict.OVER);
            Console.Error.WriteLine($"Total: {results.Count}  Conformant: {conf}  Under: {under}  Over: {over}");
            return 0;
        }
    }
}
