#import <Foundation/Foundation.h>
#include <signal.h>
#include <sys/stat.h>
#include <unistd.h>
// Native process fixture for successful upgrades, crash, expiry and retirement.
// It is compiled only for focused tests and never included in the app/catalog.
int main(int argc, char **argv) {
    @autoreleasepool {
        if (argc != 5) return 64;
        NSString *session = @(argv[2]);
        char *line = NULL; size_t size = 0; ssize_t length;
        while ((length = getline(&line, &size, stdin)) > 0) {
            if (length > 65537) return 65;
            NSDictionary *request = [NSJSONSerialization JSONObjectWithData:[NSData dataWithBytes:line length:length] options:0 error:NULL];
            if (![request[@"Session"] isEqual:session]) return 66;
            NSString *method = request[@"Method"];
            id result = @{}; NSDictionary *island = nil;
            if ([method isEqual:@"hello"]) result = request[@"Payload"];
            if ([method isEqual:@"settings"]) {
                NSString *mode = [NSString stringWithContentsOfFile:@"runtime-mode" encoding:NSUTF8StringEncoding error:NULL];
                if ([mode isEqual:@"readonly-save"]) chmod(".", 0500);
            }
            if ([method isEqual:@"action"]) {
                NSString *mode = [NSString stringWithContentsOfFile:@"runtime-mode" encoding:NSUTF8StringEncoding error:NULL];
                if ([mode isEqual:@"crash"]) _exit(73);
                if ([mode isEqual:@"stall"]) {
                    [@"ready" writeToFile:@"request-started" atomically:YES encoding:NSUTF8StringEncoding error:NULL];
                    signal(SIGTERM, SIG_IGN); for (;;) pause();
                }
                result = @{@"messageKey": @"message"};
                NSISO8601DateFormatter *format = [NSISO8601DateFormatter new];
                island = @{@"Compact": @{@"Key": @"compact"}, @"Expanded": @{@"Key": @"expanded"}, @"Actions": @[],
                           @"ExpiresAt": [format stringFromDate:[NSDate dateWithTimeIntervalSinceNow:2]]};
            }
            NSDictionary *reply = @{@"Protocol": @1, @"Session": session, @"Id": request[@"Id"], @"Method": @"result",
                                     @"Payload": @{@"Result": result, @"Island": island ?: NSNull.null}};
            NSData *bytes = [NSJSONSerialization dataWithJSONObject:reply options:0 error:NULL];
            fwrite(bytes.bytes, 1, bytes.length, stdout); fputc('\n', stdout); fflush(stdout);
            if ([method isEqual:@"stop"]) { free(line); return 0; }
        }
        free(line);
    }
    return 0;
}
