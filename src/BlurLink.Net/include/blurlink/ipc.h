#pragma once

// Local authenticated IPC: Windows named pipe server with:
//  - random per-launch pipe name (passed on the command line),
//  - restrictive DACL: current user + Administrators (+ SYSTEM for the
//    elevated helper itself), no one else,
//  - per-launch capability token: every command must carry it; constant-time
//    comparison; any mismatch => error response, no action,
//  - JSON-lines messages, local only (no network listener).
//
// Commands: hello | start | stop | sniff | stop_sniff | start_host |
//           revoke_host_player | get_status | shutdown.
// Responses: status | error (+ event_packet lines are folded into status.recent).

#ifdef _WIN32

#include <windows.h>

#include <atomic>
#include <functional>
#include <string>
#include <thread>

namespace blurlink {

struct IpcCallbacks {
  std::function<std::string(const std::string& body)> on_start;  // body=start JSON
  std::function<std::string()> on_stop;
  std::function<std::string(const std::string& body)> on_sniff;  // body=sniff JSON
  std::function<std::string()> on_stop_sniff;
  std::function<std::string(const std::string& body)> on_start_host;  // body=start_host JSON
  std::function<std::string(const std::string& body)> on_revoke_host_player;
  std::function<std::string()> on_status;
  std::function<void()> on_shutdown;
  // Invoked when a `hello` arrives, with the peer's protocolVersion.
  // Lets the app record/report version skew for diagnostics.
  std::function<void(int)> on_version;
  // Invoked on every line that carries a valid token (any command).
  // Used by the watchdog: if the GUI dies, authenticated traffic stops.
  std::function<void()> on_activity;
};

class IpcServer {
 public:
  IpcServer(std::string pipe_name, std::string token, IpcCallbacks cb);
  ~IpcServer();

  IpcServer(const IpcServer&) = delete;
  IpcServer& operator=(const IpcServer&) = delete;

  bool Start(std::string& error);
  void Stop();

 private:
  void AcceptLoop();
  void HandleConnection(HANDLE pipe);
  bool BuildSecurity();
  void FreeSecurity();
  static bool TokensEqual(const std::string& a, const std::string& b);
  static std::string ErrorJson(const std::string& msg);

  std::string pipe_name_;
  std::string token_;
  IpcCallbacks cb_;
  std::atomic<bool> running_{false};
  std::thread thread_;
  int peer_protocol_version_ = 0;  // set after a matching `hello`
  // Restrictive DACL, built once in Start(), freed in Stop().
  void* sec_desc_ = nullptr;  // PSECURITY_DESCRIPTOR (Windows-only header here)
  void* sec_acl_ = nullptr;   // PACL
  void* admin_sid_ = nullptr;
  void* system_sid_ = nullptr;
};

}  // namespace blurlink

#endif  // _WIN32
