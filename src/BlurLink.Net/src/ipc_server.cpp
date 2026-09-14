// Authenticated local named-pipe server implementation.

#include "blurlink/ipc.h"

#ifdef _WIN32

#include <accctrl.h>
#include <aclapi.h>
#include <sddl.h>

#include <sstream>
#include <vector>

#include "blurlink/console_log.h"
#include "blurlink/json_min.h"

namespace blurlink {

// IPC protocol version this helper speaks. Must match
// BlurLinkConstants.IpcProtocolVersion in the managed Contracts layer.
constexpr int kIpcProtocolVersion = 1;

namespace {

// Reads one '\n'-terminated line (max 256KB). Returns false on disconnect.
bool ReadLine(HANDLE pipe, std::string& out) {
  out.clear();
  out.reserve(512);
  char ch = 0;
  DWORD got = 0;
  while (out.size() < 262144) {
    BOOL ok = ReadFile(pipe, &ch, 1, &got, nullptr);
    if (!ok || got == 0) return false;
    if (ch == '\n') return true;
    if (ch != '\r') out.push_back(ch);
  }
  return true;
}

bool WriteLine(HANDLE pipe, const std::string& s) {
  std::string msg = s + "\n";
  const char* p = msg.data();
  DWORD left = static_cast<DWORD>(msg.size());
  while (left > 0) {
    DWORD w = 0;
    if (!WriteFile(pipe, p, left, &w, nullptr) || w == 0) return false;
    p += w;
    left -= w;
  }
  return FlushFileBuffers(pipe) != 0;
}

}  // namespace

// Builds a DACL granting GENERIC_ALL to: current user, Administrators,
// SYSTEM. Everyone else: no access. Built once; freed via FreeSecurity().
bool IpcServer::BuildSecurity() {
  HANDLE tok = nullptr;
  if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &tok)) {
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) return false;
  }
  DWORD need = 0;
  GetTokenInformation(tok, TokenUser, nullptr, 0, &need);
  std::vector<std::uint8_t> userBuf(need ? need : 1);
  PTOKEN_USER user = reinterpret_cast<PTOKEN_USER>(userBuf.data());
  if (!GetTokenInformation(tok, TokenUser, user, need, &need)) {
    CloseHandle(tok);
    return false;
  }

  SID_IDENTIFIER_AUTHORITY ntAuth = SECURITY_NT_AUTHORITY;
  PSID adminSid = nullptr, systemSid = nullptr;
  AllocateAndInitializeSid(&ntAuth, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS,
                           0, 0, 0, 0, 0, 0, &adminSid);
  AllocateAndInitializeSid(&ntAuth, 1, SECURITY_LOCAL_SYSTEM_RID, 0, 0, 0, 0, 0, 0, 0,
                           &systemSid);
  if (!adminSid || !systemSid) {
    if (adminSid) FreeSid(adminSid);
    if (systemSid) FreeSid(systemSid);
    CloseHandle(tok);
    return false;
  }

  EXPLICIT_ACCESSA ea[3]{};
  ea[0].grfAccessPermissions = GENERIC_ALL;
  ea[0].grfAccessMode = SET_ACCESS;
  ea[0].grfInheritance = NO_INHERITANCE;
  ea[0].Trustee.TrusteeForm = TRUSTEE_IS_SID;
  ea[0].Trustee.TrusteeType = TRUSTEE_IS_USER;
  ea[0].Trustee.ptstrName = static_cast<LPSTR>(user->User.Sid);
  const char* names[2] = {"admins", "system"};
  PSID sids[2] = {adminSid, systemSid};
  for (int i = 0; i < 2; ++i) {
    ea[i + 1].grfAccessPermissions = GENERIC_ALL;
    ea[i + 1].grfAccessMode = SET_ACCESS;
    ea[i + 1].grfInheritance = NO_INHERITANCE;
    ea[i + 1].Trustee.TrusteeForm = TRUSTEE_IS_SID;
    ea[i + 1].Trustee.TrusteeType = TRUSTEE_IS_GROUP;
    ea[i + 1].Trustee.ptstrName = static_cast<LPSTR>(sids[i]);
    (void)names;
  }
  PACL acl = nullptr;
  PSECURITY_DESCRIPTOR sd = nullptr;
  if (SetEntriesInAclA(3, ea, nullptr, &acl) != ERROR_SUCCESS) {
    FreeSid(adminSid);
    FreeSid(systemSid);
    CloseHandle(tok);
    return false;
  }
  sd = static_cast<PSECURITY_DESCRIPTOR>(LocalAlloc(LPTR, SECURITY_DESCRIPTOR_MIN_LENGTH));
  if (sd && InitializeSecurityDescriptor(sd, SECURITY_DESCRIPTOR_REVISION) &&
      SetSecurityDescriptorDacl(sd, TRUE, acl, FALSE)) {
    admin_sid_ = adminSid;
    system_sid_ = systemSid;
    sec_acl_ = acl;
    sec_desc_ = sd;
    CloseHandle(tok);
    return true;
  }
  if (sd) LocalFree(sd);
  if (acl) LocalFree(acl);
  FreeSid(adminSid);
  FreeSid(systemSid);
  CloseHandle(tok);
  return false;
}

void IpcServer::FreeSecurity() {
  if (sec_desc_) {
    LocalFree(static_cast<PSECURITY_DESCRIPTOR>(sec_desc_));
    sec_desc_ = nullptr;
  }
  if (sec_acl_) {
    LocalFree(static_cast<PACL>(sec_acl_));
    sec_acl_ = nullptr;
  }
  if (admin_sid_) {
    FreeSid(static_cast<PSID>(admin_sid_));
    admin_sid_ = nullptr;
  }
  if (system_sid_) {
    FreeSid(static_cast<PSID>(system_sid_));
    system_sid_ = nullptr;
  }
}

IpcServer::IpcServer(std::string pipe_name, std::string token, IpcCallbacks cb)
    : pipe_name_(std::move(pipe_name)), token_(std::move(token)), cb_(std::move(cb)) {}

IpcServer::~IpcServer() { Stop(); }

bool IpcServer::Start(std::string& error) {
  if (running_.exchange(true)) return true;
  // Fail fast if the DACL cannot be built: never listen without it.
  if (!BuildSecurity()) {
    running_.store(false);
    error = "Failed to build restrictive pipe security descriptor; refusing to listen.";
    return false;
  }
  try {
    thread_ = std::thread(&IpcServer::AcceptLoop, this);
  } catch (const std::exception& ex) {
    running_.store(false);
    FreeSecurity();
    error = ex.what();
    return false;
  }
  return true;
}

void IpcServer::Stop() {
  running_.store(false);
  // Unblock a pending ConnectNamedPipe by connecting to ourselves.
  std::string full = "\\\\.\\pipe\\" + pipe_name_;
  HANDLE h = CreateFileA(full.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                         0, nullptr);
  if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
  if (thread_.joinable()) thread_.join();
  FreeSecurity();
}

bool IpcServer::TokensEqual(const std::string& a, const std::string& b) {
  if (a.empty() || b.empty() || a.size() != b.size()) return false;
  volatile unsigned diff = 0;
  for (std::size_t i = 0; i < a.size(); ++i) diff |= static_cast<unsigned>(a[i] ^ b[i]);
  return diff == 0;
}

std::string IpcServer::ErrorJson(const std::string& msg) {
  return "{\"type\":\"error\",\"message\":\"" + minjson::Escape(msg) + "\"}";
}

void IpcServer::AcceptLoop() {
  std::string full = "\\\\.\\pipe\\" + pipe_name_;
  SECURITY_ATTRIBUTES sa{};
  sa.nLength = sizeof(sa);
  sa.lpSecurityDescriptor = static_cast<PSECURITY_DESCRIPTOR>(sec_desc_);
  sa.bInheritHandle = FALSE;
  while (running_.load()) {
    HANDLE pipe =
        CreateNamedPipeA(full.c_str(), PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE |
                                                          PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                         1, 65536, 65536, 0, sec_desc_ ? &sa : nullptr);
    if (pipe == INVALID_HANDLE_VALUE) {
      if (running_.load()) Sleep(200);
      continue;
    }
    BOOL connected = ConnectNamedPipe(pipe, nullptr) ||
                     (GetLastError() == ERROR_PIPE_CONNECTED);
    if (!running_.load()) {
      CloseHandle(pipe);
      break;
    }
    if (connected) {
      HandleConnection(pipe);
    }
    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
  }
}

void IpcServer::HandleConnection(HANDLE pipe) {
  std::string line;
  while (running_.load() && ReadLine(pipe, line)) {
    if (line.empty()) continue;
    std::string type;
    std::string token;
    minjson::GetString(line, "type", type);
    minjson::GetString(line, "token", token);
    if (!TokensEqual(token_, token)) {
      blog::Warn("ipc: rejected command with bad capability token (type='%s')",
                 type.empty() ? "?" : type.c_str());
      if (!WriteLine(pipe, ErrorJson("unauthorized: bad capability token"))) break;
      continue;
    }
    if (cb_.on_activity) cb_.on_activity();

    // Protocol-version gate. A `hello` MUST carry protocolVersion; the peer
    // may then either match (proceed) or disagree (explicit error, no
    // confusing per-command failures later).
    long long peer_version = 0;
    const bool is_hello = (type == "hello");
    if (is_hello) {
      if (!minjson::GetInt(line, "protocolVersion", peer_version)) {
        if (!WriteLine(pipe, ErrorJson("hello is missing protocolVersion (this helper speaks protocol "
                                       + std::to_string(kIpcProtocolVersion) + ")")))
          break;
        continue;
      }
      if (cb_.on_version) cb_.on_version(static_cast<int>(peer_version));
    }
    if (peer_protocol_version_ != kIpcProtocolVersion) {
      // First message, or version changed: verify. hello == agreement point.
      if (is_hello) {
        if (peer_version != kIpcProtocolVersion) {
          blog::Warn("ipc: protocol version mismatch (peer=%lld helper=%d)", peer_version,
                     kIpcProtocolVersion);
          if (!WriteLine(pipe, ErrorJson("protocol version mismatch: peer " +
                                         std::to_string(peer_version) + " vs helper " +
                                         std::to_string(kIpcProtocolVersion))))
            break;
          continue;
        }
        peer_protocol_version_ = kIpcProtocolVersion;
      } else {
        if (!WriteLine(pipe, ErrorJson("send hello with protocolVersion " +
                                       std::to_string(kIpcProtocolVersion) + " first")))
          break;
        continue;
      }
    }
    std::string resp;
    if (type == "hello") {
      // Version already agreed above; report live state like get_status.
      resp = cb_.on_status ? cb_.on_status() : ErrorJson("not ready");
    } else if (type == "start") {
      resp = cb_.on_start ? cb_.on_start(line) : ErrorJson("no start handler");
    } else if (type == "stop") {
      resp = cb_.on_stop ? cb_.on_stop() : ErrorJson("no stop handler");
    } else if (type == "sniff") {
      resp = cb_.on_sniff ? cb_.on_sniff(line) : ErrorJson("no sniff handler");
    } else if (type == "stop_sniff") {
      resp = cb_.on_stop_sniff ? cb_.on_stop_sniff() : ErrorJson("no sniff handler");
    } else if (type == "start_host") {
      resp = cb_.on_start_host ? cb_.on_start_host(line) : ErrorJson("no host mode handler");
    } else if (type == "revoke_host_player") {
      resp = cb_.on_revoke_host_player ? cb_.on_revoke_host_player(line)
                                       : ErrorJson("no host mode handler");
    } else if (type == "get_status") {
      resp = cb_.on_status ? cb_.on_status() : ErrorJson("no status handler");
    } else if (type == "shutdown") {
      resp = "{\"type\":\"status\",\"active\":false,\"shutdown\":true}";
      WriteLine(pipe, resp);
      if (cb_.on_shutdown) cb_.on_shutdown();
      break;
    } else {
      resp = ErrorJson("unknown command '" + type + "'");
    }
    if (!WriteLine(pipe, resp)) break;
  }
}

}  // namespace blurlink

#endif  // _WIN32
