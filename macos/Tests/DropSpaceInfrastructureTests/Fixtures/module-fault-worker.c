#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
// Process-boundary fault fixture only; never a product helper or official DLC.
int main(int argc, char **argv) {
    if (argc != 5) return 64;
    char mode[64] = {0}; FILE *configuration = fopen("fixture-mode", "r");
    if (!configuration || !fgets(mode, sizeof(mode), configuration)) return 65;
    fclose(configuration);
    char line[65538]; int ready = 0;
    while (fgets(line, sizeof(line), stdin)) {
        char *start = strstr(line, "\"Id\":\""); if (!start) return 66;
        start += 6; char *end = strchr(start, '"'); if (!end) return 67; *end = 0;
        char id[32]; snprintf(id, sizeof(id), "%s", start); *end = '"';
        if (!ready) {
            printf("{\"Protocol\":1,\"Session\":\"%s\",\"Id\":\"%s\",\"Method\":\"result\",\"Payload\":{\"Result\":{\"moduleId\":\"dropspace.sample\",\"moduleVersion\":\"1.0.0\",\"protocol\":1,\"dataVersion\":1}}}\n", argv[2], id);
            fflush(stdout); ready = 1; continue;
        }
        if (!strcmp(mode, "oversized")) {
            for (int i = 0; i < 65537; ++i) fputc('x', stdout); fflush(stdout);
        } else if (!strcmp(mode, "stderr")) {
            char noise[4096]; memset(noise, 'x', sizeof(noise));
            for (int i = 0; i < 257; ++i) fwrite(noise, 1, sizeof(noise), stderr); fflush(stderr);
        } else if (!strcmp(mode, "stale")) {
            printf("{\"Protocol\":1,\"Session\":\"%s\",\"Id\":\"1\",\"Method\":\"result\",\"Payload\":{\"Result\":null}}\n", argv[2]); fflush(stdout);
        } else if (!strcmp(mode, "descendant")) {
            pid_t child = fork();
            if (!child) { signal(SIGTERM, SIG_IGN); for (;;) pause(); }
            FILE *pid = fopen("descendant-pid", "w"); fprintf(pid, "%d", child); fclose(pid);
        }
        signal(SIGTERM, SIG_IGN); for (;;) pause();
    }
    return 0;
}
