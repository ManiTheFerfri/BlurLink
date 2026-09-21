#pragma once

// Tiny thread-safe console logger for blurlink-net's dashboard.
// Levels: error < warn < info < debug. Default level: info; --verbose raises
// it to debug. Every line is timestamped and flushed, so the helper's console
// window always tells its story — it must never sit blank while working.
//
// Console vs file: the console output is decorated (ANSI level colors,
// section rules) to make the story readable at a glance; the --log-file
// mirror always receives PLAIN text (metadata only — this API has no payload
// parameter, so packet contents cannot reach it by construction) with full
// dates, ready for copy/paste into bug reports. Rotation: when the file
// exceeds 1 MB it is renamed to <path>.1 (overwriting the previous
// generation) and a fresh file starts — bounded disk use.

#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <mutex>
#include <string>

#ifdef _WIN32
#include <windows.h>
#endif

namespace blurlink::blog {

enum class Level { kError = 0, kWarn = 1, kInfo = 2, kDebug = 3 };

inline const char* LevelName(Level lv) {
  switch (lv) {
    case Level::kError: return "ERROR";
    case Level::kWarn: return "WARN";
    case Level::kInfo: return "info";
    case Level::kDebug: return "debug";
  }
  return "?";
}

class Logger {
 public:
  static Logger& Instance() {
    static Logger instance;
    return instance;
  }

  void set_level(Level lv) {
    std::lock_guard<std::mutex> lock(mutex_);
    level_ = lv;
  }

  Level level() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return level_;
  }

#ifdef _WIN32
  // Prepares the Windows console for a readable dashboard:
  //   1. ENABLE_VIRTUAL_TERMINAL_PROCESSING — ANSI colors (Win10 1809+).
  //   2. QuickEdit OFF — a stray mouse click must never freeze the log
  //      (text-selection mode silently blocks every printf until a key is
  //      pressed; the #1 "the console just stops updating" report).
  //   3. UTF-8 output codepage — box-drawing rules render correctly.
  // Best effort at every step: any failure just means plainer output.
  void InitWindowsConsole() {
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    if (out != INVALID_HANDLE_VALUE && out != nullptr) {
      DWORD mode = 0;
      if (GetConsoleMode(out, &mode)) {
        if (SetConsoleMode(out, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING)) {
          console_vt_ = true;  // colors allowed only when actually enabled
        }
      }
    }
    if (in != INVALID_HANDLE_VALUE && in != nullptr) {
      DWORD mode = 0;
      if (GetConsoleMode(in, &mode)) {
        mode &= ~ENABLE_QUICK_EDIT_MODE;  // click-to-select must not freeze us
        mode |= ENABLE_EXTENDED_FLAGS;    // required for the QuickEdit bit
        SetConsoleMode(in, mode);
      }
    }
    SetConsoleOutputCP(65001);
  }
#else
  void InitWindowsConsole() {}
#endif

  // Opens the mirror file (append). Returns false when the path cannot be
  // opened (console logging continues regardless). Never throws.
  bool log_to_file(const std::string& path) {
    std::lock_guard<std::mutex> lock(mutex_);
    close_file_locked();
    if (path.empty()) return false;
    FILE* f = nullptr;
    if (fopen_s(&f, path.c_str(), "ab") != 0 || !f) return false;
    file_ = f;
    file_path_ = path;
    file_bytes_ = 0;
    if (fseek(f, 0, SEEK_END) == 0) {
      long end = ftell(f);
      if (end > 0) file_bytes_ = static_cast<long long>(end);
    }
    return true;
  }

  void close_log_file() {
    std::lock_guard<std::mutex> lock(mutex_);
    close_file_locked();
  }

  // Prints a full-width rule above the next line — use to separate lifecycle
  // phases (startup, start, sniff, stop) so the eye can find them fast.
  // Console gets a decorated rule; the file mirror records a plain marker.
  void Section(const char* title) {
    std::lock_guard<std::mutex> lock(mutex_);
#ifdef _WIN32
    if (console_vt_) {
      // Faint cyan rule + bold white title, then reset.
      std::printf("\x1b[2m\x1b[36m");
      for (int i = 0; i < 72; ++i) std::printf("%s", "\xe2\x94\x80");  // ─
      std::printf("\x1b[0m\n");
      std::printf("\x1b[1m\x1b[97m%s\x1b[0m\n", title);
    } else {
      std::printf("========== %s ==========\n", title);
    }
#else
    std::printf("========== %s ==========\n", title);
#endif
    std::fflush(stdout);
    if (file_) {
      MirrorLineLocked(std::string("---- ") + title + " ----");
    }
  }

  void Log(Level lv, const char* fmt, ...) {
    char msg[2048];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(msg, sizeof(msg), fmt, ap);
    va_end(ap);
    std::lock_guard<std::mutex> lock(mutex_);
    if (static_cast<int>(lv) > static_cast<int>(level_)) {
      return;
    }
    EmitConsoleLocked(lv, msg);
    if (file_) {
      MirrorLineLocked(std::string(LevelName(lv)) + " " + msg);
    }
  }

 private:
  Logger() = default;
  ~Logger() { close_file_locked(); }
  Logger(const Logger&) = delete;
  Logger& operator=(const Logger&) = delete;

  // Timestamped, colored console line. mutex_ must be held.
  void EmitConsoleLocked(Level lv, const char* msg) {
    namespace sc = std::chrono;
    const auto now = sc::system_clock::now();
    std::time_t t = sc::system_clock::to_time_t(now);
    std::tm tm{};
#if defined(_WIN32)
    localtime_s(&tm, &t);
#else
    localtime_r(&t, &tm);
#endif
    char ts[24];
    strftime(ts, sizeof(ts), "%H:%M:%S", &tm);
    const auto ms = sc::duration_cast<sc::milliseconds>(now.time_since_epoch()) % 1000;
#ifdef _WIN32
    if (console_vt_) {
      // gray timestamp, colored level tag, plain message.
      const char* color = lv == Level::kError ? "\x1b[91m"
                          : lv == Level::kWarn ? "\x1b[93m"
                          : lv == Level::kDebug ? "\x1b[90m"
                                                : "\x1b[97m";
      std::printf("\x1b[90m%s.%03d\x1b[0m %s%-5s\x1b[0m %s\n", ts,
                  static_cast<int>(ms.count()), color, LevelName(lv), msg);
      std::fflush(stdout);
      return;
    }
#endif
    std::printf("[%s.%03d] %-5s %s\n", ts, static_cast<int>(ms.count()), LevelName(lv), msg);
    std::fflush(stdout);
  }

  // Plain-text mirror with full date. mutex_ must be held.
  void MirrorLineLocked(const std::string& line) {
    if (!file_) return;
    std::time_t ft = std::time(nullptr);
    std::tm ftm{};
#if defined(_WIN32)
    localtime_s(&ftm, &ft);
#else
    localtime_r(&ft, &ftm);
#endif
    char fts[32];
    strftime(fts, sizeof(fts), "%Y-%m-%d %H:%M:%S", &ftm);
    int n = std::fprintf(file_, "[%s] %s\n", fts, line.c_str());
    std::fflush(file_);
    if (n > 0) file_bytes_ += n;
    RotateIfNeededLocked();
  }

  void close_file_locked() {
    if (file_) {
      std::fclose(file_);
      file_ = nullptr;
    }
    file_path_.clear();
    file_bytes_ = 0;
  }

  void RotateIfNeededLocked() {
    if (!file_ || file_path_.empty() || file_bytes_ < 1024 * 1024) return;
    std::fclose(file_);
    file_ = nullptr;
    std::string one = file_path_ + ".1";
    std::remove(one.c_str());
    std::rename(file_path_.c_str(), one.c_str());
    FILE* f = nullptr;
    if (fopen_s(&f, file_path_.c_str(), "ab") == 0 && f) {
      file_ = f;
      file_bytes_ = 0;
    } else {
      file_path_.clear();
    }
  }

  mutable std::mutex mutex_;
  Level level_ = Level::kInfo;
  bool console_vt_ = false;  // set by InitWindowsConsole() when VT is enabled
  FILE* file_ = nullptr;
  std::string file_path_;
  long long file_bytes_ = 0;
};

inline void Error(const char* fmt, ...) {
  char msg[2048];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(msg, sizeof(msg), fmt, ap);
  va_end(ap);
  Logger::Instance().Log(Level::kError, "%s", msg);
}

inline void Warn(const char* fmt, ...) {
  char msg[2048];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(msg, sizeof(msg), fmt, ap);
  va_end(ap);
  Logger::Instance().Log(Level::kWarn, "%s", msg);
}

inline void Info(const char* fmt, ...) {
  char msg[2048];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(msg, sizeof(msg), fmt, ap);
  va_end(ap);
  Logger::Instance().Log(Level::kInfo, "%s", msg);
}

inline void Debug(const char* fmt, ...) {
  char msg[2048];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(msg, sizeof(msg), fmt, ap);
  va_end(ap);
  Logger::Instance().Log(Level::kDebug, "%s", msg);
}

inline void Section(const char* title) { Logger::Instance().Section(title); }

}  // namespace blurlink::blog
