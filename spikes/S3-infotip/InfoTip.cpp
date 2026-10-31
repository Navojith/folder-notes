// Spike S3: minimal folder infotip handler (IQueryInfo + IPersistFile). Throwaway code.
// Returns a static note-shaped tip and appends one line per call to %TEMP%\fn-s3.log so we can
// see which Explorer views actually call us. (Production must NOT write logs: REQ-042.)
#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <strsafe.h>
#include <new>

#pragma comment(lib, "shlwapi.lib")

// {027D38C1-1502-4DF3-A632-A6A30EDF7876}
static const CLSID CLSID_FnInfoTip = {0x027d38c1, 0x1502, 0x4df3, {0xa6, 0x32, 0xa6, 0xa3, 0x0e, 0xdf, 0x78, 0x76}};
static const wchar_t* kClsidStr = L"{027D38C1-1502-4DF3-A632-A6A30EDF7876}";

static HMODULE g_module = nullptr;
static LONG g_objects = 0, g_locks = 0;

static void Log(const wchar_t* what, const wchar_t* path, double ms)
{
    wchar_t file[MAX_PATH];
    if (!GetTempPathW(MAX_PATH, file)) return;
    StringCchCatW(file, MAX_PATH, L"fn-s3.log");
    HANDLE h = CreateFileW(file, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME st; GetLocalTime(&st);
    wchar_t exe[MAX_PATH] = L""; GetModuleFileNameW(nullptr, exe, MAX_PATH);
    wchar_t line[2048];
    StringCchPrintfW(line, 2048, L"%02d:%02d:%02d.%03d pid=%lu tid=%lu exe=%s %s %.3fms path=%s\r\n",
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, GetCurrentProcessId(), GetCurrentThreadId(),
        PathFindFileNameW(exe), what, ms, path ? path : L"");
    // write UTF-8 so emoji/unicode paths are readable
    char utf8[6144];
    int n = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8), nullptr, nullptr);
    DWORD written;
    if (n > 1) WriteFile(h, utf8, n - 1, &written, nullptr);
    CloseHandle(h);
}

static double NowMs()
{
    static LARGE_INTEGER f = [] { LARGE_INTEGER x; QueryPerformanceFrequency(&x); return x; }();
    LARGE_INTEGER c; QueryPerformanceCounter(&c);
    return c.QuadPart * 1000.0 / f.QuadPart;
}

class InfoTip final : public IQueryInfo, public IPersistFile
{
    LONG _ref = 1;
    wchar_t _path[MAX_PATH * 4] = L"";
public:
    InfoTip() { InterlockedIncrement(&g_objects); }
    ~InfoTip() { InterlockedDecrement(&g_objects); }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IQueryInfo) *ppv = static_cast<IQueryInfo*>(this);
        else if (riid == IID_IPersistFile || riid == IID_IPersist) *ppv = static_cast<IPersistFile*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG r = InterlockedDecrement(&_ref);
        if (r == 0) delete this;
        return r;
    }

    // IPersist / IPersistFile: Explorer hands us the hovered item's path here.
    IFACEMETHODIMP GetClassID(CLSID* p) override { if (!p) return E_POINTER; *p = CLSID_FnInfoTip; return S_OK; }
    IFACEMETHODIMP IsDirty() override { return S_FALSE; }
    IFACEMETHODIMP Load(LPCOLESTR file, DWORD) override
    {
        if (!file) return E_INVALIDARG;
        StringCchCopyW(_path, ARRAYSIZE(_path), file);
        Log(L"Load", _path, 0);
        return S_OK;
    }
    IFACEMETHODIMP Save(LPCOLESTR, BOOL) override { return E_NOTIMPL; }
    IFACEMETHODIMP SaveCompleted(LPCOLESTR) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetCurFile(LPOLESTR*) override { return E_NOTIMPL; }

    // IQueryInfo
    IFACEMETHODIMP GetInfoTip(DWORD flags, PWSTR* tip) override
    {
        if (!tip) return E_POINTER;
        *tip = nullptr;
        double t0 = NowMs();
        wchar_t buf[2048];
        StringCchPrintfW(buf, ARRAYSIZE(buf),
            L"\U0001F4CC Client: Acme Ltd [S3 spike]\n"
            L"Keep · client, do-not-delete · Keep until 1 Jan 2027\n"
            L"Contract docs and signed SOWs. Ask Sam before\n"
            L"archiving anything here.\n"
            L"(flags=0x%lx, %s)", flags, PathFindFileNameW(_path));
        HRESULT hr = SHStrDupW(buf, tip);
        Log(L"GetInfoTip", _path, NowMs() - t0);
        return hr;
    }
    IFACEMETHODIMP GetInfoFlags(DWORD* flags) override { if (flags) *flags = 0; return S_OK; }
};

class Factory final : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = this; return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }
    IFACEMETHODIMP_(ULONG) Release() override { return 1; }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* p = new (std::nothrow) InfoTip();
        if (!p) return E_OUTOFMEMORY;
        HRESULT hr = p->QueryInterface(riid, ppv);
        p->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        lock ? InterlockedIncrement(&g_locks) : InterlockedDecrement(&g_locks);
        return S_OK;
    }
};
static Factory g_factory;

BOOL APIENTRY DllMain(HMODULE m, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = m; DisableThreadLibraryCalls(m); }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (clsid != CLSID_FnInfoTip) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow() { return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE; }

static LSTATUS SetKey(const wchar_t* sub, const wchar_t* name, const wchar_t* value)
{
    HKEY k;
    LSTATUS s = RegCreateKeyExW(HKEY_CURRENT_USER, sub, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &k, nullptr);
    if (s != ERROR_SUCCESS) return s;
    s = RegSetValueExW(k, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value), (DWORD)((wcslen(value) + 1) * sizeof(wchar_t)));
    RegCloseKey(k);
    return s;
}

// Per-user registration (HKCU only, no admin). regsvr32 calls these.
STDAPI DllRegisterServer()
{
    wchar_t dll[MAX_PATH];
    GetModuleFileNameW(g_module, dll, MAX_PATH);
    wchar_t key[256];
    StringCchPrintfW(key, 256, L"Software\\Classes\\CLSID\\%s", kClsidStr);
    if (SetKey(key, nullptr, L"Folder Notes S3 infotip spike")) return E_FAIL;
    StringCchPrintfW(key, 256, L"Software\\Classes\\CLSID\\%s\\InprocServer32", kClsidStr);
    if (SetKey(key, nullptr, dll) || SetKey(key, L"ThreadingModel", L"Apartment")) return E_FAIL;
    if (SetKey(L"Software\\Classes\\Directory\\shellex\\{00021500-0000-0000-C000-000000000046}", nullptr, kClsidStr)) return E_FAIL;
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    wchar_t key[256];
    StringCchPrintfW(key, 256, L"Software\\Classes\\CLSID\\%s", kClsidStr);
    RegDeleteTreeW(HKEY_CURRENT_USER, key);
    RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\Classes\\Directory\\shellex\\{00021500-0000-0000-C000-000000000046}");
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}
