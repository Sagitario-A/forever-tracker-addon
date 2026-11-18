// Lo que el programa habla con la web. Estas rutas no cambian nunca en la web (lo nuevo se añade al lado), así que un
// programa viejo sigue funcionando.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ForeverTracker
{
    public class Respuesta
    {
        public int Estado;                          // código HTTP; 0 si no hubo respuesta (sin internet, la web no contesta…)
        public Dictionary<string, object> Json;     // cuerpo, si era JSON con un objeto
        public string FalloRed;                     // por qué no hubo respuesta

        public bool Bien { get { return Estado >= 200 && Estado < 300; } }
        public string Error
        {
            get
            {
                if (FalloRed != null) return FalloRed;
                object e;
                if (Json != null && Json.TryGetValue("error", out e) && e is string) return (string)e;
                return "La web ha respondido con el código " + Estado + ".";
            }
        }
        public bool Anulado
        {
            get { object a; return Estado == 401 && Json != null && Json.TryGetValue("anulado", out a) && a is bool && (bool)a; }
        }
        public string Texto(string campo)
        {
            object v;
            return Json != null && Json.TryGetValue(campo, out v) && v is string ? (string)v : null;
        }
    }

    public class Web
    {
        public readonly string Base;
        readonly HttpClient http;

        public Web(string baseUrl)
        {
            Base = baseUrl.TrimEnd('/');
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ForeverTracker-Programa/" + Programa.Version);
            http.DefaultRequestHeaders.Add("X-Programa-Version", Programa.Version);
        }

        public string Url(string ruta) { return ruta.StartsWith("http") ? ruta : Base + ruta; }

        async Task<Respuesta> Envia(HttpMethod metodo, string ruta, string clave, HttpContent cuerpo)
        {
            var r = new Respuesta();
            try
            {
                using (var req = new HttpRequestMessage(metodo, Url(ruta)))
                {
                    if (clave != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clave);
                    req.Content = cuerpo;
                    using (var res = await http.SendAsync(req).ConfigureAwait(false))
                    {
                        r.Estado = (int)res.StatusCode;
                        string texto = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                        try { r.Json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(texto); } catch { }
                    }
                }
            }
            catch (Exception e)
            {
                r.Estado = 0;
                r.FalloRed = "No se puede hablar con la web (" + Raiz(e).Message + ")";
            }
            return r;
        }

        static Exception Raiz(Exception e) { while (e.InnerException != null) e = e.InnerException; return e; }

        public Task<Respuesta> Get(string ruta, string clave) { return Envia(HttpMethod.Get, ruta, clave, null); }
        public Task<Respuesta> Delete(string ruta, string clave) { return Envia(HttpMethod.Delete, ruta, clave, null); }

        public Task<Respuesta> PostJson(string ruta, object cuerpo)
        {
            var c = new StringContent(new JavaScriptSerializer().Serialize(cuerpo), Encoding.UTF8, "application/json");
            return Envia(HttpMethod.Post, ruta, null, c);
        }

        public Task<Respuesta> PostArchivo(string ruta, byte[] datos, string clave)
        {
            var c = new ByteArrayContent(datos);
            c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return Envia(HttpMethod.Post, ruta, clave, c);
        }

        // Descargar un archivo (el addon o el programa). null si falla.
        public async Task<byte[]> Descarga(string ruta)
        {
            try
            {
                using (var res = await http.GetAsync(Url(ruta)).ConfigureAwait(false))
                {
                    if (!res.IsSuccessStatusCode) return null;
                    return await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e) { Registro.Apunta("Descarga fallida de " + ruta + ": " + Raiz(e).Message); return null; }
        }
    }

    // Leer números y textos de un JSON ya convertido (JavaScriptSerializer da int, long, decimal…).
    public static class J
    {
        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }
        public static string Txt(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? v as string : null;
        }
        public static bool Si(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) && v is bool && (bool)v;
        }
        public static long Num(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return 0;
            try { return Convert.ToInt64(v); } catch { return 0; }
        }
        public static object[] Lista(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v)) return new object[0];
            var a = v as System.Collections.ArrayList;
            return a != null ? a.ToArray() : (v as object[] ?? new object[0]);
        }
    }

    // Comparar versiones «1.2.10» > «1.2.9».
    public static class Versiones
    {
        public static int Compara(string a, string b)
        {
            Version va, vb;
            if (!Version.TryParse(a ?? "", out va)) va = new Version(0, 0);
            if (!Version.TryParse(b ?? "", out vb)) vb = new Version(0, 0);
            return va.CompareTo(vb);
        }
    }
}
