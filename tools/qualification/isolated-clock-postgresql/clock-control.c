#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <sys/syscall.h>

#define OFFSET_FILE "/opt/sqlobserver-clock/clock-offset.txt"
#define TEMP_FILE "/opt/sqlobserver-clock/clock-offset.next"
#define MAX_DAYS 36500

/* Direct x86_64 syscall deliberately bypasses preload interposition. This reads
 * the shared kernel clock; it does not set it or require a clock capability. */
static int kernel_clock(clockid_t id, struct timespec *value)
{
    long result;
    __asm__ volatile("syscall" : "=a"(result)
        : "a"((long)SYS_clock_gettime), "D"((long)id), "S"(value)
        : "rcx", "r11", "memory");
    return result == 0 ? 0 : -1;
}

static int64_t ns(struct timespec value)
{
    return (int64_t)value.tv_sec * INT64_C(1000000000) + value.tv_nsec;
}

static int environment_valid(void)
{
    const char *names[] = {"LD_PRELOAD", "FAKETIME_TIMESTAMP_FILE",
        "FAKETIME_DONT_FAKE_MONOTONIC", "FAKETIME_NO_CACHE", "NO_FAKE_STAT"};
    const char *values[] = {"/usr/lib/x86_64-linux-gnu/faketime/libfaketime.so.1",
        OFFSET_FILE, "1", "1", "1"};
    for (size_t i = 0; i < sizeof(names) / sizeof(names[0]); i++) {
        const char *actual = getenv(names[i]);
        if (actual == NULL || strcmp(actual, values[i]) != 0) return 0;
    }
    return getenv("FAKETIME") == NULL;
}

static int read_days(void)
{
    char text[32];
    FILE *file = fopen(OFFSET_FILE, "r");
    if (file == NULL) return -1;
    if (fgets(text, sizeof(text), file) == NULL) { fclose(file); return -1; }
    int trailing = fgetc(file);
    fclose(file);
    if (trailing != EOF || text[0] != '+') return -1;
    char *end;
    errno = 0;
    long days = strtol(text + 1, &end, 10);
    if (errno != 0 || end == text + 1 || strcmp(end, "d\n") != 0
        || days < 0 || days > MAX_DAYS) return -1;
    return (int)days;
}

static int set_days(const char *text)
{
    if (text[0] == '\0' || (text[0] == '0' && text[1] != '\0')) return 1;
    for (const char *p = text; *p != '\0'; p++) if (*p < '0' || *p > '9') return 1;
    char *end;
    errno = 0;
    long days = strtol(text, &end, 10);
    int previous = read_days();
    if (errno != 0 || *end != '\0' || previous < 0 || days <= previous || days > MAX_DAYS) return 1;
    int fd = open(TEMP_FILE, O_WRONLY | O_CREAT | O_EXCL, 0644);
    if (fd < 0) return 1;
    char output[32];
    int length = snprintf(output, sizeof(output), "+%ldd\n", days);
    int failed = write(fd, output, (size_t)length) != length || fsync(fd) != 0;
    if (close(fd) != 0) failed = 1;
    if (!failed && rename(TEMP_FILE, OFFSET_FILE) != 0) failed = 1;
    if (failed) { unlink(TEMP_FILE); return 1; }
    int directory = open("/opt/sqlobserver-clock", O_RDONLY | O_DIRECTORY);
    if (directory < 0) return 1;
    failed = fsync(directory) != 0;
    if (close(directory) != 0) failed = 1;
    return failed;
}

int main(int argc, char **argv)
{
    if (!environment_valid()) { fputs("Isolated clock environment refused.\n", stderr); return 1; }
    if (argc == 3 && strcmp(argv[1], "set-days") == 0) return set_days(argv[2]);
    if (argc != 2 || strcmp(argv[1], "probe") != 0) return 1;
    int days = read_days();
    struct timespec wall, kernel_wall, mono, kernel_mono;
    if (days < 0 || clock_gettime(CLOCK_REALTIME, &wall) != 0
        || kernel_clock(CLOCK_REALTIME, &kernel_wall) != 0
        || clock_gettime(CLOCK_MONOTONIC, &mono) != 0
        || kernel_clock(CLOCK_MONOTONIC, &kernel_mono) != 0) return 1;
    printf("{\"contract\":\"isolated-forward-days-v1\",\"offsetDays\":%d,"
        "\"fakeRealtimeNs\":\"%" PRId64 "\",\"kernelRealtimeNs\":\"%" PRId64 "\","
        "\"monotonicNs\":\"%" PRId64 "\",\"kernelMonotonicNs\":\"%" PRId64 "\"}\n",
        days, ns(wall), ns(kernel_wall), ns(mono), ns(kernel_mono));
    return 0;
}
