#include "proxy.h"

#include <cstdarg>
#include <cstdio>
#include <cstring>

JavaVM*   g_vm    = nullptr;
jvmtiEnv* g_jvmti = nullptr;

void Dbg(const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    MessageBoxA(nullptr, buf, "heypixel", MB_OK);
    LogTo("[DBG] %s", buf);
}

static char   g_logDir[MAX_PATH] = {};
static int    g_logDay = 0;
static HANDLE g_logFile = INVALID_HANDLE_VALUE;

static void CreateDirRecursive(const char* path) {
    char tmp[MAX_PATH];
    lstrcpynA(tmp, path, MAX_PATH);
    for (char* p = tmp + 3; *p; ++p) {
        if (*p != '\\') continue;
        *p = 0;
        CreateDirectoryA(tmp, NULL);
        *p = '\\';
    }
    CreateDirectoryA(tmp, NULL);
}

static void ResolveLogDir() {
    if (g_logDir[0]) return;

    const char* dir = getenv("WTFNEL_LOG_DIR");
    if (dir && *dir) {
        lstrcpynA(g_logDir, dir, MAX_PATH);
    } else {
        char tmp[MAX_PATH];
        DWORD n = GetTempPathA(MAX_PATH, tmp);
        if (n > 0 && n + 14 < MAX_PATH) lstrcatA(tmp, "WtfNel\\logs");
        else lstrcpynA(tmp, "C:\\WtfNel\\logs", MAX_PATH);
        lstrcpynA(g_logDir, tmp, MAX_PATH);
    }

    CreateDirRecursive(g_logDir);
}

const char* ProxyLogPath() {
    static char path[MAX_PATH];
    ResolveLogDir();

    SYSTEMTIME st;
    GetLocalTime(&st);
    wsprintfA(path, "%s\\proxy-%04d%02d%02d.log", g_logDir, st.wYear, st.wMonth, st.wDay);
    return path;
}

static HANDLE CurrentLogFile() {
    SYSTEMTIME st;
    GetLocalTime(&st);
    int day = st.wYear * 10000 + st.wMonth * 100 + st.wDay;

    if (g_logFile != INVALID_HANDLE_VALUE && day == g_logDay) return g_logFile;

    if (g_logFile != INVALID_HANDLE_VALUE) {
        CloseHandle(g_logFile);
        g_logFile = INVALID_HANDLE_VALUE;
    }

    g_logDay = day;
    g_logFile = CreateFileA(ProxyLogPath(), FILE_APPEND_DATA,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    return g_logFile;
}

void LogTo(const char* fmt, ...) {

    static CRITICAL_SECTION cs;
    static bool cs_init = false;
    if (!cs_init) {
        InitializeCriticalSection(&cs);
        cs_init = true;
    }
    EnterCriticalSection(&cs);

    HANDLE h = CurrentLogFile();
    if (h != INVALID_HANDLE_VALUE) {
        SYSTEMTIME st;
        GetLocalTime(&st);

        char msg[1200];
        int base = wsprintfA(msg, "[%04d-%02d-%02d %02d:%02d:%02d.%03d] ",
                             st.wYear, st.wMonth, st.wDay,
                             st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
        va_list ap;
        va_start(ap, fmt);
        vsnprintf(msg + base, sizeof(msg) - base - 2, fmt, ap);
        va_end(ap);
        size_t len = strlen(msg);
        if (len < sizeof(msg) - 2) {
            msg[len++] = '\r';
            msg[len++] = '\n';
        }

        DWORD wrote = 0;
        WriteFile(h, msg, (DWORD)len, &wrote, NULL);
    }
    LeaveCriticalSection(&cs);
}

void LogAndClearException(JNIEnv* env, const char* where) {
    if (!env || !env->ExceptionCheck()) return;
    jthrowable t = env->ExceptionOccurred();
    env->ExceptionClear();

    jclass thrCls = env->FindClass("java/lang/Throwable");
    jmethodID toStringMid =
        env->GetMethodID(thrCls, "toString", "()Ljava/lang/String;");
    jstring msg = (jstring)env->CallObjectMethod(t, toStringMid);

    const char* c = msg ? env->GetStringUTFChars(msg, nullptr) : "<unknown>";
    Dbg("[%s] %s", where, c);
    if (msg) env->ReleaseStringUTFChars(msg, c);

    jmethodID pst = env->GetMethodID(thrCls, "printStackTrace", "()V");
    if (pst) env->CallVoidMethod(t, pst);
    if (env->ExceptionCheck()) env->ExceptionClear();

    env->DeleteLocalRef(t);
    env->DeleteLocalRef(thrCls);
    if (msg) env->DeleteLocalRef(msg);
}

JniAttach::JniAttach() {
    if (!g_vm) return;
    jint rc = g_vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_8);
    if (rc == JNI_EDETACHED) {
        JavaVMAttachArgs args{};
        args.version = JNI_VERSION_1_8;
        args.name = const_cast<char*>("heypixelNative");
        args.group = nullptr;
        if (g_vm->AttachCurrentThreadAsDaemon(
                reinterpret_cast<void**>(&env), &args) == JNI_OK) {
            attached = true;
        } else {
            env = nullptr;
        }
    } else if (rc != JNI_OK) {
        env = nullptr;
    }
}

JniAttach::~JniAttach() {
    if (attached && g_vm) g_vm->DetachCurrentThread();
}

jobject GetMinecraftClassLoader(JNIEnv* env, jvmtiEnv* jvmti) {
    if (!env || !jvmti) return nullptr;

    jint count = 0;
    jthread* threads = nullptr;
    if (jvmti->GetAllThreads(&count, &threads) != JVMTI_ERROR_NONE) {
        return nullptr;
    }

    static int s_dump_count = 0;
    bool dump = (s_dump_count++ < 3);
    if (dump) LogTo("GetMinecraftClassLoader: enumerating %d threads", (int)count);

    jobject result = nullptr;
    for (jint i = 0; i < count; ++i) {
        jvmtiThreadInfo info{};
        if (jvmti->GetThreadInfo(threads[i], &info) != JVMTI_ERROR_NONE) continue;

        bool hasLoader = (info.context_class_loader != nullptr);
        if (dump) {
            LogTo("  thread[%d] name='%s' has_loader=%d",
                  (int)i,
                  info.name ? info.name : "(null)",
                  hasLoader ? 1 : 0);
        }

        bool hit = info.name && hasLoader &&
                   std::strcmp(info.name, "Render thread") == 0;

        if (!hit && info.name && hasLoader) {
            jclass conn = LoadClassInLoader(env, info.context_class_loader,
                                            "net.minecraft.network.Connection");
            if (env->ExceptionCheck()) env->ExceptionClear();
            if (conn) {
                if (dump) LogTo("     -> can load MC.Connection, PICKED");
                hit = true;
                env->DeleteLocalRef(conn);
            }
        }

        if (hit) result = env->NewGlobalRef(info.context_class_loader);

        if (info.name) jvmti->Deallocate(reinterpret_cast<unsigned char*>(info.name));
        if (result) break;
    }

    jvmti->Deallocate(reinterpret_cast<unsigned char*>(threads));
    return result;
}

jclass LoadClassInLoader(JNIEnv* env, jobject classLoader, const char* dotName) {
    if (!env || !classLoader || !dotName) return nullptr;

    jclass clCls = env->GetObjectClass(classLoader);
    jmethodID mid =
        env->GetMethodID(clCls, "loadClass", "(Ljava/lang/String;)Ljava/lang/Class;");
    if (!mid) {
        env->ExceptionClear();
        env->DeleteLocalRef(clCls);
        return nullptr;
    }

    jstring jname = env->NewStringUTF(dotName);
    jclass  result = (jclass)env->CallObjectMethod(classLoader, mid, jname);
    if (env->ExceptionCheck()) {
        env->ExceptionClear();
        result = nullptr;
    }

    env->DeleteLocalRef(jname);
    env->DeleteLocalRef(clCls);
    return result;
}
