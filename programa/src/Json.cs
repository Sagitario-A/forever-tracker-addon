// JSON propio y pequeño, igual en Windows y en Mac (desde la 1.1.4 sustituye al JavaScriptSerializer de Windows, que no existe
// fuera de él). Lee a Dictionary<string, object> (objetos), object[] (listas), string, bool, long o double (números) y null; y
// escribe lo mismo. C# 5 (el compilador que trae Windows): sin $"", ?. ni nameof.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ForeverTracker
{
    public static class Json
    {
        // ───────── leer ─────────
        public static object Lee(string s)
        {
            var l = new Lector(s);
            l.Blancos();
            object v = l.Valor();
            l.Blancos();
            if (!l.Fin) throw l.Error("sobra texto al final");
            return v;
        }

        public static Dictionary<string, object> Objeto(string s)
        {
            return Lee(s) as Dictionary<string, object>;
        }

        class Lector
        {
            readonly string s;
            int i;
            public Lector(string texto) { s = texto ?? ""; }
            public bool Fin { get { return i >= s.Length; } }
            public Exception Error(string que) { return new FormatException("JSON mal formado (" + que + " en la posición " + i + ")"); }
            public void Blancos() { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++; }

            public object Valor()
            {
                if (Fin) throw Error("se acaba antes de tiempo");
                char c = s[i];
                if (c == '{') return Objeto();
                if (c == '[') return Lista();
                if (c == '"') return Cadena();
                if (c == 't') { Palabra("true"); return true; }
                if (c == 'f') { Palabra("false"); return false; }
                if (c == 'n') { Palabra("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return Numero();
                throw Error("carácter inesperado «" + c + "»");
            }

            void Palabra(string p)
            {
                if (string.CompareOrdinal(s, i, p, 0, p.Length) != 0) throw Error("se esperaba " + p);
                i += p.Length;
            }

            Dictionary<string, object> Objeto()
            {
                var d = new Dictionary<string, object>();
                i++; // {
                Blancos();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Blancos();
                    if (Fin || s[i] != '"') throw Error("se esperaba el nombre de un campo");
                    string k = Cadena();
                    Blancos();
                    if (Fin || s[i] != ':') throw Error("se esperaba «:»");
                    i++;
                    Blancos();
                    d[k] = Valor();
                    Blancos();
                    if (Fin) throw Error("objeto sin cerrar");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw Error("se esperaba «,» o «}»");
                }
            }

            object[] Lista()
            {
                var l = new List<object>();
                i++; // [
                Blancos();
                if (i < s.Length && s[i] == ']') { i++; return l.ToArray(); }
                while (true)
                {
                    Blancos();
                    l.Add(Valor());
                    Blancos();
                    if (Fin) throw Error("lista sin cerrar");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l.ToArray(); }
                    throw Error("se esperaba «,» o «]»");
                }
            }

            string Cadena()
            {
                i++; // "
                var sb = new StringBuilder();
                while (true)
                {
                    if (Fin) throw Error("texto sin cerrar");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (Fin) throw Error("escape a medias");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw Error("\\u a medias");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw Error("escape desconocido \\" + e);
                    }
                }
            }

            object Numero()
            {
                int inicio = i;
                bool real = false;
                if (s[i] == '-') i++;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c >= '0' && c <= '9') { i++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { real = true; i++; continue; }
                    break;
                }
                string t = s.Substring(inicio, i - inicio);
                long n;
                if (!real && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return n;
                double d;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw Error("número mal escrito «" + t + "»");
            }
        }

        // ───────── escribir ─────────
        public static string Escribe(object v)
        {
            var sb = new StringBuilder();
            Escribe(sb, v);
            return sb.ToString();
        }

        static void Escribe(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { Cadena(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long || v is short || v is byte) { sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double || v is float || v is decimal) { sb.Append(Convert.ToDouble(v).ToString("R", CultureInfo.InvariantCulture)); return; }
            var d = v as IDictionary<string, object>;
            if (d != null)
            {
                sb.Append('{');
                bool primero = true;
                foreach (var kv in d)
                {
                    if (!primero) sb.Append(',');
                    primero = false;
                    Cadena(sb, kv.Key);
                    sb.Append(':');
                    Escribe(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var ds = v as IDictionary<string, string>;
            if (ds != null)
            {
                sb.Append('{');
                bool primero = true;
                foreach (var kv in ds)
                {
                    if (!primero) sb.Append(',');
                    primero = false;
                    Cadena(sb, kv.Key);
                    sb.Append(':');
                    Escribe(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var lista = v as IEnumerable;
            if (lista != null)
            {
                sb.Append('[');
                bool primero = true;
                foreach (object x in lista)
                {
                    if (!primero) sb.Append(',');
                    primero = false;
                    Escribe(sb, x);
                }
                sb.Append(']');
                return;
            }
            Cadena(sb, v.ToString());
        }

        static void Cadena(StringBuilder sb, string t)
        {
            sb.Append('"');
            foreach (char c in t)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
