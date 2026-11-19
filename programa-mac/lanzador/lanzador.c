// El lanzador del paquete universal de Mac (desde la 1.1.5). Es «Contents/MacOS/ForeverTracker», un binario universal (chip Apple
// e Intel) de unas pocas líneas: macOS elige la mitad de su chip, y esa mitad arranca el programa de verdad de su arquitectura, que
// va dentro del paquete como una aplicación completa: Contents/Helpers/<arm64|x64>/Forever Tracker.app. (.NET no sabe hacer una
// sola aplicación para los dos chips: sus archivos compilados son distintos para cada uno; probado el 2026-11-19, docs/programa.md 6.)
//
// Antes de arrancarlo le quita la marca de cuarentena (la que pone macOS a lo descargado) a la aplicación interior: el usuario ya
// autorizó el paquete exterior con «Abrir igualmente», y así macOS no vuelve a preguntar por la interior. Se sustituye a sí mismo por
// el programa (execv): el proceso es el mismo, con los mismos argumentos, y para macOS y para el LaunchAgent sigue siendo este.
//
// Se compila desde Windows con zig (herramientas/programa/empaquetar_mac.py): una vez para cada chip y se juntan con rcodesign.
#include <ftw.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

// zig no trae sys/xattr.h de macOS; la función está en libSystem igualmente.
int removexattr(const char *path, const char *name, int options);
#define XATTR_NOFOLLOW 0x0001

#if defined(__aarch64__) || defined(__arm64__)
#define ARQUITECTURA "arm64"
#else
#define ARQUITECTURA "x64"
#endif

static int quita_cuarentena(const char *ruta, const struct stat *st, int tipo, struct FTW *f)
{
    (void)st; (void)tipo; (void)f;
    removexattr(ruta, "com.apple.quarantine", XATTR_NOFOLLOW);
    return 0;
}

int main(int argc, char **argv)
{
    (void)argc;
    char propio[PATH_MAX];
    uint32_t n = sizeof propio;
    if (_NSGetExecutablePath(propio, &n) != 0) { fprintf(stderr, "Forever Tracker: no sé dónde estoy\n"); return 1; }
    char real[PATH_MAX];
    if (realpath(propio, real) == NULL) strncpy(real, propio, sizeof real - 1);
    char *barra = strrchr(real, '/');           // …/Contents/MacOS
    if (barra == NULL) { fprintf(stderr, "Forever Tracker: ruta rara: %s\n", real); return 1; }
    *barra = '\0';
    char app[PATH_MAX], exe[PATH_MAX];
    snprintf(app, sizeof app, "%s/../Helpers/" ARQUITECTURA "/Forever Tracker.app", real);
    snprintf(exe, sizeof exe, "%s/Contents/MacOS/ForeverTracker", app);
    nftw(app, quita_cuarentena, 16, FTW_PHYS);
    execv(exe, argv);
    perror("Forever Tracker: no se pudo arrancar el programa");
    fprintf(stderr, "  %s\n", exe);
    return 1;
}
