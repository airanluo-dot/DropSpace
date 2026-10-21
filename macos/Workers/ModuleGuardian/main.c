// An owned native worker group. EOF from the host retires the group even if the
// app crashes. This is lifetime isolation for trusted code, not a sandbox:
// a process that deliberately creates another session can escape a POSIX group.
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

extern char **environ;
static volatile sig_atomic_t stopping;
static void stop_signal(int value) { (void)value; stopping = 1; }
typedef struct { unsigned char bytes[65537]; size_t count; } buffer;
static int nonblocking(int fd) {
    return fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK);
}
static int receive(int fd, buffer *target) {
    ssize_t n = read(fd, target->bytes + target->count, sizeof(target->bytes) - target->count);
    if (n > 0) { target->count += (size_t)n; return 1; }
    return n < 0 && (errno == EAGAIN || errno == EINTR) ? 1 : 0;
}
static int transmit(int fd, buffer *source) {
    ssize_t n = write(fd, source->bytes, source->count);
    if (n > 0) { source->count -= (size_t)n; memmove(source->bytes, source->bytes + n, source->count); return 1; }
    return n < 0 && (errno == EAGAIN || errno == EINTR) ? 1 : 0;
}
int main(int argc, char **argv) {
    if (argc != 8 || strcmp(argv[2], "--module-session") || strcmp(argv[4], "--data-version") || strcmp(argv[6], "--data-directory")) return 64;
    signal(SIGPIPE, SIG_IGN); signal(SIGTERM, stop_signal); signal(SIGINT, stop_signal);
    int input[2], output[2], error[2];
    if (pipe(input) || pipe(output) || pipe(error)) return 70;
    int fds[] = {input[0], input[1], output[0], output[1], error[0], error[1]};
    for (size_t i = 0; i < 6; ++i) if (fcntl(fds[i], F_SETFD, FD_CLOEXEC)) return 70;
    posix_spawn_file_actions_t actions; posix_spawnattr_t attributes;
    if (posix_spawn_file_actions_init(&actions) || posix_spawnattr_init(&attributes)) return 70;
    if (posix_spawn_file_actions_adddup2(&actions, input[0], STDIN_FILENO) ||
        posix_spawn_file_actions_adddup2(&actions, output[1], STDOUT_FILENO) ||
        posix_spawn_file_actions_adddup2(&actions, error[1], STDERR_FILENO) ||
        posix_spawn_file_actions_addchdir(&actions, argv[7])) return 70;
    sigset_t empty, defaults; sigemptyset(&empty); sigemptyset(&defaults);
    sigaddset(&defaults, SIGPIPE); sigaddset(&defaults, SIGTERM); sigaddset(&defaults, SIGINT);
    if (posix_spawnattr_setflags(&attributes, POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_CLOEXEC_DEFAULT) ||
        posix_spawnattr_setpgroup(&attributes, 0) || posix_spawnattr_setsigmask(&attributes, &empty) || posix_spawnattr_setsigdefault(&attributes, &defaults)) return 70;
    char *arguments[] = {argv[1], argv[2], argv[3], argv[4], argv[5], NULL};
    pid_t child = 0;
    int launched = posix_spawn(&child, argv[1], &actions, &attributes, arguments, environ);
    posix_spawn_file_actions_destroy(&actions); posix_spawnattr_destroy(&attributes);
    close(input[0]); close(output[1]); close(error[1]);
    if (launched) return 71;
    buffer incoming = {0}, outgoing = {0};
    outgoing.count = (size_t)snprintf((char *)outgoing.bytes, sizeof(outgoing.bytes), "DropSpaceModuleGuardian/1 %d\n", child);
    if (nonblocking(0) || nonblocking(1) || nonblocking(input[1]) || nonblocking(output[0]) || nonblocking(error[0])) stopping = 1;
    int output_eof = 0, error_eof = 0, exited = 0, status = 0;
    size_t stderr_bytes = 0;
    while (!stopping) {
        struct pollfd polls[] = {
            {0, incoming.count < sizeof(incoming.bytes) ? POLLIN : 0, 0},
            {input[1], incoming.count ? POLLOUT : 0, 0},
            {output_eof ? -1 : output[0], outgoing.count < sizeof(outgoing.bytes) ? POLLIN : 0, 0},
            {1, outgoing.count ? POLLOUT : 0, 0},
            {error_eof ? -1 : error[0], POLLIN, 0}
        };
        if (poll(polls, 5, 25) < 0 && errno != EINTR) break;
        if (polls[0].revents & (POLLHUP | POLLERR | POLLNVAL)) break;
        if (polls[0].revents & (POLLIN | POLLHUP | POLLERR)) {
            if (incoming.count < sizeof(incoming.bytes) && !receive(0, &incoming)) break;
        }
        if ((polls[1].revents & POLLOUT) && !transmit(input[1], &incoming)) break;
        if (polls[2].revents & (POLLIN | POLLHUP | POLLERR)) {
            if (outgoing.count < sizeof(outgoing.bytes) && !receive(output[0], &outgoing)) output_eof = 1;
        }
        if ((polls[3].revents & (POLLOUT | POLLERR | POLLHUP)) && !transmit(1, &outgoing)) break;
        if (polls[4].revents & (POLLIN | POLLHUP | POLLERR)) {
            unsigned char bytes[4096]; ssize_t n = read(error[0], bytes, sizeof(bytes));
            if (n > 0) { stderr_bytes += (size_t)n; if (stderr_bytes > 1024 * 1024) break; }
            else if (!n) error_eof = 1;
        }
        // WNOWAIT keeps the leader PID owned until group cleanup, even if its
        // stdout is inherited by a child that outlives it.
        siginfo_t info = {0};
        if (!exited && !waitid(P_PID, (id_t)child, &info, WEXITED | WNOHANG | WNOWAIT) && info.si_pid == child) {
            exited = 1; kill(-child, SIGKILL);
        }
        if (exited) {
            // Retain already-produced response bytes before retiring descendants.
            if (output_eof && !outgoing.count) break;
        }
    }
    close(input[1]); close(output[0]); close(error[0]);
    kill(-child, SIGKILL);
    while (waitpid(child, &status, 0) < 0 && errno == EINTR) {}
    // Never report successful teardown while owned descendants still exist.
    // The host retains this guardian on a bounded stop timeout and can retry.
    struct timespec pause = {0, 20000000};
    while (kill(-child, 0) == 0 || errno == EPERM) nanosleep(&pause, NULL);
    return 0;
}
