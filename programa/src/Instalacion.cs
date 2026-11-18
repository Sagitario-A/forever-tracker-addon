// Instalar, desinstalar y arrancar con Windows. Solo para la descarga directa: el paquete de la Store (MSIX) lo instala,
// pone en marcha al iniciar Windows (tarea de inicio del paquete) y desinstala Windows.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ForeverTracker
{
    public static class Instalacion
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ForeverTracker";
        const string Nombre = "ForeverTracker";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder name);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        const int ASFW_ANY = -1;

        // ¿Va dentro de NUESTRO paquete de la Store? Se mira la familia del paquete, no solo si hay alguno: cuando Configuración →
        // Aplicaciones lanza un desinstalador lo hace a través de un intermediario que sí es un paquete, y el programa heredaba esa
        // identidad, se creía el de la Store y con --desinstalar se cerraba sin hacer nada (visto el 2026-11-18).
        public static bool EsPaquete()
        {
            try
            {
                int len = 0;
                int r = GetCurrentPackageFamilyName(ref len, null);
                if (r == 15700) return false; // APPMODEL_ERROR_NO_PACKAGE
                var sb = new StringBuilder(len + 1);
                if (GetCurrentPackageFamilyName(ref len, sb) != 0) return false;
                return string.Equals(sb.ToString(), PaqueteFamilia, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; } // Windows sin paquetes
        }

        // En modo de pruebas (solo para probar la actualización): la carpeta de datos de las pruebas; el programa va en su
        // subcarpeta «programa» y no se toca nada de Windows (registro, menú Inicio).
        public static string CarpetaPruebas;

        public static string CarpetaPrograma { get { return CarpetaPruebas != null ? Path.Combine(CarpetaPruebas, "programa") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ForeverTracker"); } }
        public static string ExeInstalado { get { return Path.Combine(CarpetaPrograma, "ForeverTracker.exe"); } }
        static string ExeActual { get { return Assembly.GetExecutingAssembly().Location; } }
        static string Acceso { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Forever Tracker.lnk"); } }

        public static bool EstaInstalado() { return string.Equals(Path.GetFullPath(ExeActual), Path.GetFullPath(ExeInstalado), StringComparison.OrdinalIgnoreCase); }

        // Copia el programa a %LOCALAPPDATA%\Programs\ForeverTracker, pone el acceso del menú Inicio y la entrada de
        // «Aplicaciones instaladas», y lo arranca. Con «silencioso» (actualización) no pregunta nada.
        public static void Instala(bool silencioso, string idDatos)
        {
            if (!silencioso)
            {
                if (!Dialogo.Instalar()) return;
            }
            // Si hay uno funcionando, se le pide que se cierre.
            Programa.Senal("salir", idDatos);
            Thread.Sleep(1500);
            Directory.CreateDirectory(CarpetaPrograma);
            for (int i = 0; ; i++)
            {
                try { File.Copy(ExeActual, ExeInstalado, true); break; }
                catch (IOException)
                {
                    if (i >= 10) throw;
                    Thread.Sleep(500);
                }
            }
            if (CarpetaPruebas != null)
            {
                Registro.Apunta("Actualizado (pruebas) en " + ExeInstalado);
                Process.Start(new ProcessStartInfo(ExeInstalado, "--fondo --pruebas \"" + CarpetaPruebas + "\"") { UseShellExecute = false });
                return;
            }
            CreaAcceso();
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Forever Tracker");
                k.SetValue("DisplayVersion", Programa.Version);
                k.SetValue("Publisher", "Forever Tracker");
                k.SetValue("DisplayIcon", ExeInstalado);
                k.SetValue("InstallLocation", CarpetaPrograma);
                k.SetValue("UninstallString", "\"" + ExeInstalado + "\" --desinstalar");
                k.SetValue("URLInfoAbout", "https://forever-tracker.kikoernk.workers.dev");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(ExeInstalado).Length / 1024), RegistryValueKind.DWord);
            }
            Registro.Apunta((silencioso ? "Actualizado" : "Instalado") + " en " + ExeInstalado);
            // Que el programa recién instalado pueda poner su ventana delante (si no, sale detrás de las demás).
            try { AllowSetForegroundWindow(ASFW_ANY); } catch { }
            Process.Start(new ProcessStartInfo(ExeInstalado, silencioso ? "--fondo" : "") { UseShellExecute = false });
        }

        // Acceso directo del menú Inicio (con el objeto de Windows que los crea).
        static void CreaAcceso()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Acceso });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { ExeInstalado });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { CarpetaPrograma });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Forever Tracker: sube tus datos del juego a la web" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.FinalReleaseComObject(lnk);
                Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception e) { Registro.Apunta("No se pudo crear el acceso del menú Inicio: " + e.Message); }
        }

        // Desinstalar (desde «Aplicaciones instaladas»): cierra el que esté funcionando, desvincula el PC en la web, borra
        // el arranque con Windows, el acceso, la entrada, los datos, la carpeta del programa y el addon del juego (desde la 1.1.2).
        public static void Desinstala(string datos, string idDatos, Func<bool> desvincula)
        {
            // Se apunta antes de nada: así se sabe si quien pidió desinstalar (Configuración, el panel clásico) llegó a lanzarlo.
            Registro.Apunta("Desinstalar pedido (desde " + (Environment.GetEnvironmentVariable("__COMPAT_LAYER") ?? "-") + "; padre con sesión interactiva: " + Environment.UserInteractive + ").");
            if (!Dialogo.Pregunta(null, "¿Desinstalar Forever Tracker?\n\nEste PC se desvinculará de tu cuenta de la web y se quitará también el addon del juego (Interface\\AddOns\\ForeverTracker). Lo que el juego guardó de tus personajes no se toca.",
                "Desinstalar", "Cancelar")) return;
            Programa.Senal("salir", idDatos);
            Thread.Sleep(1500);
            try { desvincula(); } catch { }
            // El addon del juego también se quita (decidido el 2026-11-18); lo guardado por el juego en WTF se queda.
            try
            {
                var aj = Ajustes.Carga(datos);
                if (aj.Juego != null && Directory.Exists(Juego.CarpetaAddon(aj.Juego))) { Directory.Delete(Juego.CarpetaAddon(aj.Juego), true); Registro.Apunta("Addon quitado de " + Juego.CarpetaAddon(aj.Juego)); }
            }
            catch (Exception e) { Registro.Apunta("No se pudo quitar el addon: " + e.Message); }
            ArranqueConWindows(false);
            try { File.Delete(Acceso); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Directory.Delete(datos, true); } catch { }
            // El .exe no se puede borrar a sí mismo mientras funciona: lo borra un momento después la consola de Windows.
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", BorradoAplazado(CarpetaPrograma))
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
            Dialogo.Avisa(null, "Forever Tracker se ha desinstalado.");
        }

        // ───────── instalado por los dos caminos (descarga directa y Store) ─────────
        // Los dos comparten la carpeta de datos, así que solo funciona uno (el segundo en arrancar cede el paso). Al abrirlo con ventana,
        // si detecta al otro, ofrece quitarlo (decidido el 2026-11-18). Los paquetes instalados del usuario están apuntados en el registro.
        const string PaquetesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        const string PaqueteNombre = "KikoGabana.ForeverTracker_";

        public static bool HayPaquete()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(PaquetesKey))
                {
                    if (k == null) return false;
                    foreach (string n in k.GetSubKeyNames()) if (n.StartsWith(PaqueteNombre, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        public static bool HayDescargaDirecta()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) if (k != null) return true; } catch { }
            return File.Exists(ExeInstalado);
        }

        const string PaqueteFamilia = "KikoGabana.ForeverTracker_2swy4jnr5e7kc"; // identidad del paquete (la que dio la Store al reservar el nombre)

        // Devuelve si esta copia tiene que cerrarse (porque el usuario se ha quedado con la otra).
        public static bool ResuelveDoble(bool paquete, string idDatos)
        {
            if (paquete ? !HayDescargaDirecta() : !HayPaquete()) return false;
            Registro.Apunta("Instalado dos veces (descarga directa y Microsoft Store); esta copia es " + (paquete ? "la de la Store." : "el .exe."));
            // Dos botones claros, uno por cada forma (decidido el 2026-11-18): se queda la elegida y se quita la otra.
            int e = Dialogo.Elige(null,
                "Forever Tracker está instalado dos veces en este PC: desde la Microsoft Store y desde la descarga directa (.exe). Solo hace falta uno; los dos usan la misma cuenta vinculada y los mismos ajustes.\n\n¿Con cuál te quedas?",
                "Quedarme con el de la Store", "Quedarme con el .exe");
            if (e == 0) return false;
            bool store = e == 1;
            Programa.Senal("salir", idDatos); // la copia que esté funcionando (de la forma que sea) se cierra
            Thread.Sleep(1500);
            bool bien = true;
            try
            {
                if (store && paquete) bien = QuitaDescargaDirecta();                       // soy la Store: quito el .exe y sigo
                else if (!store && !paquete) bien = QuitaPaquete();                        // soy el .exe: quito la Store y sigo
                else if (store && !paquete)                                                // soy el .exe y se queda la Store: me quito y la abro
                {
                    bien = QuitaDescargaDirecta();
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + PaqueteFamilia + "!ForeverTracker") { UseShellExecute = false });
                }
                else                                                                       // soy la Store y se queda el .exe: PowerShell me quita y abre el .exe
                {
                    Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Start-Sleep 1; Get-AppxPackage -Name KikoGabana.ForeverTracker | Remove-AppxPackage; Start-Process '" + ExeInstalado + "'\"")
                    { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                }
            }
            catch (Exception ex) { Registro.Apunta("No se pudo quitar la otra copia: " + ex.Message); bien = false; }
            Registro.Apunta(bien ? "Se queda " + (store ? "la de la Store" : "el .exe") + "; la otra, quitada." : "No se pudo quitar la otra copia.");
            if (!bien) Dialogo.Avisa(null, "No he podido quitar la otra copia. Quítala desde Configuración → Aplicaciones.");
            return store != paquete; // me cierro si se queda la otra
        }

        // Quitar la descarga directa desde el paquete: arranque con Windows, acceso, entrada de «Aplicaciones instaladas» y su carpeta
        // (los datos se quedan: son los mismos).
        static bool QuitaDescargaDirecta()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null && k.GetValue(Nombre) != null) k.DeleteValue(Nombre); } catch { }
            try { File.Delete(Acceso); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", BorradoAplazado(CarpetaPrograma))
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
            return true;
        }

        // Quitar el paquete de la Store desde la descarga directa: lo hace PowerShell (sin administrador, es del propio usuario).
        static bool QuitaPaquete()
        {
            var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Get-AppxPackage -Name KikoGabana.ForeverTracker | Remove-AppxPackage\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            p.WaitForExit(120000);
            return !HayPaquete();
        }

        // El .exe no se puede borrar mientras funciona (y sigue funcionando mientras se lee el aviso de «desinstalado»): la consola de
        // Windows lo intenta cada 2 segundos hasta un minuto, hasta que la carpeta desaparece (visto el 2026-11-18: quedaba el .exe).
        static string BorradoAplazado(string carpeta)
        {
            return "/c for /l %i in (1,1,30) do (timeout /t 2 /nobreak >nul & rmdir /s /q \"" + carpeta + "\" 2>nul & if not exist \"" + carpeta + "\" exit)";
        }

        // ───────── arrancar con Windows (descarga directa: valor «ForeverTracker» de Run del usuario) ─────────
        public static bool ArrancaConWindows()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Nombre) != null;
        }

        public static void ArranqueConWindows(bool si)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (si) k.SetValue(Nombre, "\"" + ExeActual + "\" --fondo");
                    else if (k.GetValue(Nombre) != null) k.DeleteValue(Nombre);
                }
                if (!si) Registro.Apunta("Quitado el arranque con Windows.");
            }
            catch (Exception e) { Registro.Apunta("No se pudo cambiar el arranque con Windows: " + e.Message); }
        }
    }
}
