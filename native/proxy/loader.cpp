

#include "proxy.h"
#include "random_name.h"
#include "trampolines.h"
#include "relay_handler.h"
#include "connection_hook.h"
#include "b_server.h"

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

std::atomic_bool g_initialised{false};

constexpr size_t kHookSize = 12;

BYTE g_originalNanoBytes[kHookSize] = {};
LPVOID g_nanoAddress = nullptr;

using Fn_JVM_NanoTime = jlong (JNICALL*)(JNIEnv*, jclass);
Fn_JVM_NanoTime g_realNanoTime = nullptr;

static void WriteJumpTo(void* target, void* dest) {
    BYTE stub[kHookSize] = {
        0x48, 0xB8,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xE0
    };
    std::memcpy(stub + 2, &dest, sizeof(dest));

    DWORD oldProtect = 0;
    VirtualProtect(target, kHookSize, PAGE_EXECUTE_READWRITE, &oldProtect);
    std::memcpy(target, stub, kHookSize);
    VirtualProtect(target, kHookSize, oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), target, kHookSize);
}

static void RestoreOriginal(void* target, const BYTE* saved) {
    DWORD oldProtect = 0;
    VirtualProtect(target, kHookSize, PAGE_EXECUTE_READWRITE, &oldProtect);
    std::memcpy(target, saved, kHookSize);
    VirtualProtect(target, kHookSize, oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), target, kHookSize);
}

jlong JNICALL HookedNanoTime(JNIEnv* env, jclass klass) {

    RestoreOriginal(g_nanoAddress, g_originalNanoBytes);

    bool expected = false;
    if (g_initialised.compare_exchange_strong(expected, true)) {
        LogTo("HookedNanoTime fired (first time); unhooked");
        if (env->GetJavaVM(&g_vm) == JNI_OK && g_vm) {
            LogTo("  GetJavaVM OK vm=%p", (void*)g_vm);
            if (g_vm->GetEnv(reinterpret_cast<void**>(&g_jvmti),
                             JVMTI_VERSION_1_2) != JNI_OK) {
                g_jvmti = nullptr;
                LogTo("  GetEnv(JVMTI) FAILED");
            } else {
                LogTo("  GetEnv(JVMTI) OK jvmti=%p", (void*)g_jvmti);
            }
        } else {
            LogTo("  GetJavaVM FAILED");
        }

        LogTo("about to call InitializeProxy");
        InitializeProxy(env);
        LogTo("InitializeProxy returned");
    }

    return g_realNanoTime(env, klass);
}

typedef jint (JNICALL* Fn_JNI_GetCreatedJavaVMs)(JavaVM**, jsize, jsize*);

DWORD WINAPI BootstrapThread(LPVOID) {
    LogTo("BootstrapThread start; waiting for jvm.dll");
    HMODULE jvm = GetModuleHandleA("jvm.dll");
    while (!jvm) {
        Sleep(50);
        jvm = GetModuleHandleA("jvm.dll");
    }
    LogTo("jvm.dll loaded at %p", (void*)jvm);

    auto getVms = reinterpret_cast<Fn_JNI_GetCreatedJavaVMs>(
        GetProcAddress(jvm, "JNI_GetCreatedJavaVMs"));
    if (!getVms) {
        LogTo("GetProcAddress(JNI_GetCreatedJavaVMs) FAILED");
        return 0;
    }
    for (int i = 0; i < 600; ++i) {
        jsize n = 0;
        if (getVms(&g_vm, 1, &n) == JNI_OK && n >= 1 && g_vm) break;
        g_vm = nullptr;
        Sleep(500);
    }
    if (!g_vm) {
        LogTo("BootstrapThread: JVM never appeared");
        return 0;
    }
    LogTo("Got JavaVM=%p", (void*)g_vm);

    JavaVMAttachArgs aa{};
    aa.version = JNI_VERSION_1_8;
    aa.name = const_cast<char*>("heypixelBoot");
    aa.group = nullptr;
    JNIEnv* env = nullptr;
    if (g_vm->AttachCurrentThreadAsDaemon(reinterpret_cast<void**>(&env), &aa) != JNI_OK
        || !env) {
        LogTo("BootstrapThread: AttachCurrentThreadAsDaemon FAILED");
        return 0;
    }
    LogTo("BootstrapThread: attached, env=%p", (void*)env);

    if (g_vm->GetEnv(reinterpret_cast<void**>(&g_jvmti), JVMTI_VERSION_1_2) != JNI_OK) {
        g_jvmti = nullptr;
        LogTo("GetEnv(JVMTI) FAILED");
        g_vm->DetachCurrentThread();
        return 0;
    }
    LogTo("Got jvmtiEnv=%p", (void*)g_jvmti);

    InitializeProxy(env);
    g_vm->DetachCurrentThread();
    return 0;
}

}

static void RawWriteFile(const char* line) {

    HANDLE h = CreateFileA(ProxyLogPath(),
                           FILE_APPEND_DATA,
                           FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD w;
    SetFilePointer(h, 0, NULL, FILE_END);

    DWORD n = 0; while (line[n]) ++n;
    WriteFile(h, line, n, &w, NULL);
    WriteFile(h, "\r\n", 2, &w, NULL);
    FlushFileBuffers(h);
    CloseHandle(h);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        RawWriteFile("[raw] DllMain entry");
        DisableThreadLibraryCalls(hModule);
        RawWriteFile("[raw] DisableThreadLibraryCalls done");
        HANDLE h = CreateThread(nullptr, 0, BootstrapThread, nullptr, 0, nullptr);
        if (h) CloseHandle(h);
        RawWriteFile("[raw] CreateThread done");

        LogTo("=== DllMain PROCESS_ATTACH ===");
        RawWriteFile("[raw] first LogTo returned");
    }
    return TRUE;
}

static DWORD WINAPI ProxyInitWorker(LPVOID);

void InitializeProxy(JNIEnv* ) {
    LogTo("InitializeProxy: spawning bootstrap worker");
    HANDLE h = CreateThread(nullptr, 0, ProxyInitWorker, nullptr, 0, nullptr);
    if (h) CloseHandle(h);
}

// ===================== Minecraft bootstrap 闸门 =====================
//
// 注入可以（也应该）在进程一起来就做，但**挂载**必须等 MC 自己 bootstrap 完。
//
// 原因：MC 的 Bootstrap.bootStrap() 跑完之前，任何线程碰到
// net.minecraft.core.registries.BuiltInRegistries 都会触发它的 <clinit>，
// 而那时 MappedRegistry 的构造会抛 IllegalArgumentException "Not bootstrapped"。
// <clinit> 抛异常会让 JVM 把这个类**永久**标记为初始化失败，主线程随后必然拿到
// NoClassDefFoundError，游戏死在 "Loading bootstrap resources"。
// cacheJavaRefs() 解析 ClientboundLoginPacket（b_server.cpp）正好会走到这条路上。
//
// GetLoadedClasses / GetClassStatus 都是只读的，不会触发任何类初始化，
// 所以拿它们轮询是安全的——这正是"在合适的时机挂载"需要的那个信号。

namespace {

constexpr const char* kGateSignature =
    "Lnet/minecraft/core/registries/BuiltInRegistries;";

// 单次查询闸门类的状态。返回 JVMTI 的 status 位图；类还没被加载时返回 -1。
jint QueryGateStatus(JNIEnv* env, const char* signature) {
    jint count = 0;
    jclass* classes = nullptr;
    if (g_jvmti->GetLoadedClasses(&count, &classes) != JVMTI_ERROR_NONE) {
        return -1;
    }

    jint status = -1;
    for (jint i = 0; i < count; ++i) {
        if (status == -1) {
            char* sig = nullptr;
            if (g_jvmti->GetClassSignature(classes[i], &sig, nullptr) == JVMTI_ERROR_NONE) {
                if (sig && std::strcmp(sig, signature) == 0) {
                    jint s = 0;
                    if (g_jvmti->GetClassStatus(classes[i], &s) == JVMTI_ERROR_NONE) {
                        status = s;
                    }
                }
                if (sig) g_jvmti->Deallocate(reinterpret_cast<unsigned char*>(sig));
            }
        }
        env->DeleteLocalRef(classes[i]);
    }

    g_jvmti->Deallocate(reinterpret_cast<unsigned char*>(classes));
    return status;
}

bool WaitForMinecraftBootstrapped(JNIEnv* env, int maxWaitSeconds) {
    const int intervalMs = 250;
    const int maxIterations = (maxWaitSeconds * 1000) / intervalMs;

    LogTo("Gate: waiting for %s to be initialized (max %ds)", kGateSignature, maxWaitSeconds);

    bool sawLoaded = false;
    for (int i = 0; i < maxIterations; ++i) {
        jint status = QueryGateStatus(env, kGateSignature);

        if (status >= 0) {
            if (!sawLoaded) {
                sawLoaded = true;
                LogTo("Gate: class loaded, status=0x%x — waiting for INITIALIZED", (unsigned)status);
            }

            if (status & JVMTI_CLASS_STATUS_ERROR) {
                // 已经被谁毒化了，再挂上去也没意义，而且会掩盖真正的错误
                LogTo("Gate: class is in ERROR state (status=0x%x) — aborting mount", (unsigned)status);
                return false;
            }

            if (status & JVMTI_CLASS_STATUS_INITIALIZED) {
                LogTo("Gate: initialized after %d ms — safe to mount", i * intervalMs);
                return true;
            }
        }

        Sleep(intervalMs);
    }

    LogTo("Gate: timed out after %ds (loaded=%d) — aborting mount", maxWaitSeconds, sawLoaded ? 1 : 0);
    return false;
}

}  // namespace

static DWORD WINAPI ProxyInitWorker(LPVOID) {
    LogTo("ProxyInitWorker: attaching to JVM as daemon");
    JavaVMAttachArgs args{};
    args.version = JNI_VERSION_1_8;
    args.name = const_cast<char*>("heypixelInit");
    args.group = nullptr;
    JNIEnv* env = nullptr;
    if (g_vm->AttachCurrentThreadAsDaemon(reinterpret_cast<void**>(&env), &args) != JNI_OK
        || !env) {
        LogTo("ProxyInitWorker: AttachCurrentThreadAsDaemon FAILED");
        return 0;
    }
    LogTo("ProxyInitWorker: attached");

    // 先过闸门再动任何 MC 类。注入时机可以任意早，挂载时机不行。
    if (!WaitForMinecraftBootstrapped(env, 600)) {
        LogTo("ProxyInitWorker: bootstrap gate not passed — not mounting");
        g_vm->DetachCurrentThread();
        return 0;
    }

    LogTo("ProxyInitWorker: polling for MC ClassLoader");

    bool installed = false;
    for (int i = 0; i < 240; ++i) {
        if (InstallHookBridge(env)) { installed = true; break; }
        Sleep(500);
    }
    if (!installed) {
        LogTo("ProxyInitWorker: gave up waiting for MC ClassLoader");
        g_vm->DetachCurrentThread();
        return 0;
    }
    LogTo("ProxyInitWorker: InstallHookBridge OK: %s", g_hook.dotName.c_str());

    if (!InstallRelayHandler(env)) {
        LogTo("ProxyInitWorker: InstallRelayHandler FAILED");
        g_vm->DetachCurrentThread();
        return 0;
    }
    LogTo("ProxyInitWorker: InstallRelayHandler OK: %s", g_relay.dotName.c_str());

    char buf[256];
    std::snprintf(buf, sizeof(buf),
                  "bridge=%s relay=%s (installed)",
                  g_hook.dotName.c_str(),
                  g_relay.dotName.c_str());
    jstring msg = env->NewStringUTF(buf);
    env->CallStaticVoidMethod(g_hook.klass, g_hook.trace, msg);
    LogAndClearException(env, "InitializeProxy/trace");
    env->DeleteLocalRef(msg);

    if (!InstallConnectionHook(env)) {
        LogTo("ProxyInitWorker: InstallConnectionHook FAILED");
        g_vm->DetachCurrentThread();
        return 0;
    }
    LogTo("ProxyInitWorker: InstallConnectionHook OK");

    if (!InstallBServer(env)) {
        LogTo("ProxyInitWorker: InstallBServer FAILED (B-side proxy unavailable)");

    } else {
        LogTo("ProxyInitWorker: InstallBServer OK — listening on 127.0.0.1:13337");

        if (BServer_TryCaptureLiveConnection(env)) {
            LogTo("ProxyInitWorker: mid-session — A already in-game; live connection captured, gate skipped");
        } else if (BServer_BlockAMainThreadUntilBConnected(env)) {
            LogTo("ProxyInitWorker: Queued A Render thread blocking gate task");
        } else {
            LogTo("ProxyInitWorker: Failed to queue A Render thread blocking gate task");
        }
    }

    LogTo("ProxyInitWorker: done — proxy fully armed");
    g_vm->DetachCurrentThread();
    return 0;
}
