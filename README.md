# Forever Tracker: addon y programa para Windows

Addon de **World of Warcraft: Forever** y programa para Windows de [Forever Tracker](https://forever-tracker.kikoernk.workers.dev),
una web gratuita para seguir el progreso de tus personajes y el de tu hermandad.

- **El addon** (`addon/ForeverTracker/`) guarda lo que el juego deja leer de tus personajes para que la web lo enseñe.
  Solo lee: nunca hace nada por ti en el juego.
- **El programa** (`programa/`) sube ese archivo a tu cuenta de la web cada vez que el juego lo guarda y mantiene el
  addon al día. No tiene ventana: funciona en segundo plano y arranca con Windows.

Web de jugadores, no oficial: sin relación con Blizzard Entertainment. World of Warcraft es una marca de Blizzard
Entertainment.

*English: in-game addon and Windows companion app for the Forever Tracker website. The addon only reads your own
characters' data (it never automates anything); the app uploads the addon's saved file to your account and keeps the
addon up to date. MIT licensed.*

## Instalar

- **Programa para Windows:** en la [página de descargas](https://forever-tracker.kikoernk.workers.dev/descargas) de la web.
  Se instala para tu usuario (sin administrador) y abre la web para vincular tu PC a tu cuenta.
- **Solo el addon:** descárgalo de la misma página y descomprímelo en
  `World of Warcraft\_forever_\Interface\AddOns\`. Después sube el archivo que guarda desde la web (Ajustes → «Datos del
  juego»).

## Qué guarda el addon

En `WTF\Account\<cuenta>\SavedVariables\ForeverTracker.lua`, al cerrar sesión, al salir del juego o con `/reload`:

1. **De tus personajes:** nombre, región, clase, raza, nivel, hermandad, misiones hechas, profesiones y recetas,
   habilidades, equipo, estadísticas, talentos, bolsas, dinero, monedas, reputaciones y colecciones. Banco, correo y
   mascota del cazador, cuando abres su ventana.
2. **De los jugadores que ves** fuera de combate: lo mismo que enseña de ellos la armería pública de Blizzard (nombre,
   nivel, clase, raza, facción y hermandad) y, para ti, cuándo y dónde te los cruzaste.
3. **Nunca:** el chat, el nombre o número de tu cuenta de World of Warcraft, ni nada de tu ordenador.

Quién ve cada cosa en la web lo eliges tú. Más en la
[política de privacidad](https://forever-tracker.kikoernk.workers.dev/privacidad).

## Qué hace el programa

1. **Vincular:** crea una clave propia de tu PC (guardada cifrada con Windows), abre la web y espera a que aceptes con
   el mismo código en los dos sitios. La web solo guarda una huella de la clave. Se puede anular desde la web.
2. **Subir:** Windows le avisa cuando el juego escribe su archivo; entonces lo lee y lo sube si ha cambiado. No mira nada
   más del disco.
3. **Addon:** comprueba la versión, lo descarga, comprueba su huella SHA-256 y cambia la carpeta de golpe.
4. **Actualizarse:** la descarga directa se pone al día sola de la misma forma; la versión de la Microsoft Store, por la
   Store.
5. **Desinstalar:** Configuración → Aplicaciones. Desvincula el PC y borra todo lo suyo; el addon se queda en el juego.

## Compilar

Sin instalar nada: el programa es C# 5 para .NET Framework 4.8 y se compila con el compilador que trae Windows.

```
programa\compilar.cmd      → programa\salida\ForeverTracker.exe
programa\empaquetar.cmd    → programa\salida\ForeverTracker.msix (necesita makeappx del SDK de Windows)
```

Para probarlo sin instalarlo ni tocar el registro de Windows:

```
ForeverTracker.exe --pruebas <carpeta> --web <dirección de la web>
```

## Licencia

[MIT](LICENSE) © 2026 Kiko Gabana.
