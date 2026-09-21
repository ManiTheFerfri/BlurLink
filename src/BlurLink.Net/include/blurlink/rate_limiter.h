#pragma once

// Token-bucket rate limiter (thread-safe). Default 10/s, burst 20.

#include <chrono>
#include <mutex>

namespace blurlink {

class RateLimiter {
 public:
  RateLimiter(int per_second, int burst)
      : refill_per_second_(static_cast<double>(per_second)),
        max_tokens_(static_cast<double>(burst)),
        tokens_(static_cast<double>(burst)),
        last_(std::chrono::steady_clock::now()) {}

  bool TryAcquire() {
    std::lock_guard<std::mutex> lock(mutex_);
    Refill();
    if (tokens_ >= 1.0) {
      tokens_ -= 1.0;
      return true;
    }
    ++rejected_;
    return false;
  }

  int rejected() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return rejected_;
  }

 private:
  void Refill() {
    auto now = std::chrono::steady_clock::now();
    double elapsed = std::chrono::duration<double>(now - last_).count();
    last_ = now;
    if (elapsed > 0) {
      tokens_ += elapsed * refill_per_second_;
      if (tokens_ > max_tokens_) {
        tokens_ = max_tokens_;
      }
    }
  }

  double refill_per_second_;
  double max_tokens_;
  double tokens_;
  std::chrono::steady_clock::time_point last_;
  int rejected_ = 0;
  mutable std::mutex mutex_;
};

}  // namespace blurlink
