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
        static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        const int ASFW_ANY = -1;

        // ¿Va dentro del paquete de la Store?
        public static bool EsPaquete()
        {
            try
            {
                int len = 0;
                return GetCurrentPackageFullName(ref len, null) != 15700; // 15700 = APPMODEL_ERROR_NO_PACKAGE
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
        // el arranque con Windows, el acceso, la entrada, los datos y la carpeta del programa. El addon del juego se queda.
        public static void Desinstala(string datos, string idDatos, Func<bool> desvincula)
        {
            if (!Dialogo.Pregunta(null, "¿Desinstalar Forever Tracker?\n\nEste PC se desvinculará de tu cuenta de la web. El addon del juego se queda: si quieres, bórralo de Interface\\AddOns.",
                "Desinstalar", "Cancelar")) return;
            Programa.Senal("salir", idDatos);
            Thread.Sleep(1500);
            try { desvincula(); } catch { }
            ArranqueConWindows(false);
            try { File.Delete(Acceso); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Directory.Delete(datos, true); } catch { }
            // El .exe no se puede borrar a sí mismo mientras funciona: lo borra un momento después la consola de Windows.
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 3 /nobreak >nul & rmdir /s /q \"" + CarpetaPrograma + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
            Dialogo.Avisa(null, "Forever Tracker se ha desinstalado.");
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
