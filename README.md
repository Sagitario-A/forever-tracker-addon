# Forever Tracker: addon y programas para Windows y Mac

Addon de **World of Warcraft: Forever** y programas para Windows y Mac de [Forever Tracker](https://forever-tracker.kikoernk.workers.dev),
una web gratuita para seguir el progreso de tus personajes y el de tu hermandad.

- **El addon** (`addon/ForeverTracker/`) guarda lo que el juego deja leer de tus personajes para que la web lo enseñe.
  Solo lee: nunca hace nada por ti en el juego.
- **El programa** (`programa/` para Windows, `programa-mac/` para Mac) instala el addon en tu juego y lo mantiene al día solo,
  sube ese archivo a tu cuenta de la web cada vez que el juego lo guarda y escribe en el addon la lista de encargos de la base
  común. Funciona en segundo plano y arranca con el sistema; su ventana (menú Inicio o Aplicaciones) solo enseña el estado. Los dos
  programas comparten la lógica (`programa/src`); cada uno trae su «caja».

Web de jugadores, no oficial: sin relación con Blizzard Entertainment. World of Warcraft es una marca de Blizzard
Entertainment.

*English: in-game addon and Windows/macOS companion app for the Forever Tracker website. The addon only reads your own
characters' data (it never automates anything); the app uploads the addon's saved file to your account and keeps the
addon up to date. MIT licensed.*

## Instalar

- **Programa para Windows o Mac:** en la [página de descargas](https://forever-tracker.kikoernk.workers.dev/descargas) de la web.
  Se instala para tu usuario (sin administrador ni contraseña) y abre la web para vincular tu PC o tu Mac a tu cuenta.
- **Solo el addon:** descárgalo de la misma página y descomprímelo en
  `World of Warcraft\_forever_\Interface\AddOns\`. Para que la web vea tus datos hace falta el programa: es quien sube el
  archivo.

## Qué guarda el addon

En `WTF\Account\<cuenta>\SavedVariables\ForeverTracker.lua`, al cerrar sesión, al salir del juego o con `/reload`:

1. **De tus personajes:** nombre, región, clase, raza, nivel, hermandad, misiones hechas, profesiones y recetas,
   habilidades, equipo, estadísticas, talentos, bolsas, dinero, monedas, reputaciones y colecciones. Banco, correo y
   mascota del cazador, cuando abres su ventana.
2. **De los jugadores que ves** fuera de combate: lo mismo que enseña de ellos la armería pública de Blizzard (nombre,
   nivel, clase, raza, facción y hermandad) y, para ti, cuándo y dónde te los cruzaste.
3. **Para la base común del juego** (desde la 1.3.0, se puede apagar en Ajustes de la web): lo que ves jugando (nombres
   de personajes del juego, vendedores, misiones, botín, precios de la subasta…) en lotes con seudónimo, nunca con tu
   nombre ni el de tu personaje.
4. **Nunca:** el chat, el nombre o número de tu cuenta de World of Warcraft, ni nada de tu ordenador.

Quién ve cada cosa en la web lo eliges tú. Más en la
[política de privacidad](https://forever-tracker.kikoernk.workers.dev/privacidad).

## Qué hace el programa

1. **Vincular:** la primera vez enseña una bienvenida con lo que hace; al pulsar «Vincular este PC» (o «este Mac») crea una clave
   propia de tu PC (en Windows, cifrada con el sistema; en Mac, en una carpeta solo de tu usuario), abre la web y espera a que aceptes con el mismo código en los dos sitios. La
   web solo guarda una huella de la clave. Se puede anular desde la web.
2. **Subir:** el sistema le avisa cuando el juego escribe su archivo; entonces lo lee y lo sube si ha cambiado. No mira nada
   más del disco.
3. **Addon:** comprueba la versión, lo descarga, comprueba su huella SHA-256 y cambia la carpeta de golpe, sin preguntar.
   Después escribe `Encargos.lua` con la lista que baja de la web (al arrancar, cada hora, tras actualizar el addon y tras
   cada subida).
4. **Actualizarse:** la descarga directa (Windows y Mac) se pone al día sola de la misma forma; la versión de la Microsoft
   Store, por la Store.
5. **Desinstalar:** en Windows, Configuración → Aplicaciones; en Mac, el botón «Desinstalar Forever Tracker…» de su ventana. Desvincula el
   equipo, borra todo lo suyo y quita el addon del juego (lo que el juego guardó de tus personajes se queda).

## Compilar

Windows, sin instalar nada: el programa es C# 5 para .NET Framework 4.8 y se compila con el compilador que trae Windows.

```
programa\compilar.cmd      → programa\salida\ForeverTracker.exe
programa\empaquetar.cmd    → programa\salida\ForeverTracker.msix (necesita makeappx del SDK de Windows)
```

Mac, desde cualquier sistema con el SDK de .NET 10 (la interfaz es Avalonia): el paquete `.app` es la salida de `dotnet publish`
dentro de `Forever Tracker.app/Contents/MacOS`, con un `Info.plist`; sin firma de Apple hay que firmarlo «ad hoc» (`codesign -s -`
en un Mac, o `rcodesign sign` desde cualquier sistema).

```
dotnet publish programa-mac/ForeverTrackerMac.csproj -c Release -r osx-arm64 --self-contained   (o -r osx-x64)
```

El paquete publicado vale para los dos chips: lleva dentro las dos aplicaciones (`Contents/Helpers/arm64` y `x64`) y un lanzador
universal (`programa-mac/lanzador/lanzador.c`, compilado con `zig cc -target aarch64-macos` y `x86_64-macos` y juntado con
`rcodesign macho-universal-create`) que arranca la de su chip.

Para probarlo sin instalarlo ni tocar el registro de Windows:

```
ForeverTracker.exe --pruebas <carpeta> --web <dirección de la web>
```

## Licencia

[MIT](LICENSE) © 2026 Kiko Gabana.
