// Información de versión del .exe (la de Propiedades → Detalles en Windows, el Administrador de tareas, SmartScreen y Configuración →
// Aplicaciones, que sin ella no llegaba a montar la desinstalación; visto el 2026-11-18). El compilador la mete en el .exe desde estos
// atributos. El número tiene que ir a la par de Programa.Version (empaquetar.py lo comprueba).
using System.Reflection;

[assembly: AssemblyTitle("Forever Tracker")]
[assembly: AssemblyDescription("Forever Tracker: sube tus datos de World of Warcraft: Forever a la web y mantiene su addon al día")]
[assembly: AssemblyProduct("Forever Tracker")]
[assembly: AssemblyCompany("Forever Tracker")]
[assembly: AssemblyCopyright("© 2026 Kiko Gabana")]
[assembly: AssemblyVersion("1.1.4.0")]
[assembly: AssemblyFileVersion("1.1.4.0")]
[assembly: AssemblyInformationalVersion("1.1.4")]
