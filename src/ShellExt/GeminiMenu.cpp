// Windows 11 main-context-menu commands: "תמלל הקלטה" (audio) and "סכם לי" (documents).
// Registered through a sparse MSIX package (see package/AppxManifest.xml).
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <new>
#include <string>

static HMODULE g_hModule = nullptr;
static volatile LONG g_objCount = 0;

// {7A1C0D55-3B7E-4F6B-9C21-5E8D4A6B1F01}
static const CLSID CLSID_TranscribeCmd = {0x7A1C0D55, 0x3B7E, 0x4F6B, {0x9C, 0x21, 0x5E, 0x8D, 0x4A, 0x6B, 0x1F, 0x01}};
// {7A1C0D55-3B7E-4F6B-9C21-5E8D4A6B1F02}
static const CLSID CLSID_SummarizeCmd = {0x7A1C0D55, 0x3B7E, 0x4F6B, {0x9C, 0x21, 0x5E, 0x8D, 0x4A, 0x6B, 0x1F, 0x02}};

static const wchar_t* const kAudioExt[] = {L".wav", L".mp3", L".m4a", L".ogg", L".amr", L".flac", L".aac", L".wma", L".opus"};
static const wchar_t* const kDocExt[] = {L".pdf", L".txt", L".docx", L".md"};

static std::wstring ModuleDir()
{
    wchar_t buf[MAX_PATH * 2] = {};
    DWORD n = GetModuleFileNameW(g_hModule, buf, ARRAYSIZE(buf));
    std::wstring p(buf, n);
    size_t pos = p.find_last_of(L'\\');
    return pos == std::wstring::npos ? p : p.substr(0, pos);
}

static bool IsGoodFile(const wchar_t* path, const wchar_t* const* exts, size_t count)
{
    DWORD attrs = GetFileAttributesW(path);
    if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_DIRECTORY)) return false;
    const wchar_t* ext = PathFindExtensionW(path);
    if (!ext || !*ext) return false;
    for (size_t i = 0; i < count; ++i)
        if (_wcsicmp(ext, exts[i]) == 0) return true;
    return false;
}

class Command : public IExplorerCommand
{
public:
    Command(const CLSID& id, const wchar_t* title, const wchar_t* icon, const wchar_t* mode,
            const wchar_t* const* exts, size_t extCount)
        : m_ref(1), m_id(id), m_title(title), m_icon(icon), m_mode(mode), m_exts(exts), m_extCount(extCount)
    {
        InterlockedIncrement(&g_objCount);
    }
    virtual ~Command() { InterlockedDecrement(&g_objCount); }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, __uuidof(IExplorerCommand)))
        {
            *ppv = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return r;
    }

    // IExplorerCommand
    STDMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override { return SHStrDupW(m_title, name); }
    STDMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        std::wstring p = ModuleDir() + L"\\assets\\" + m_icon;
        return SHStrDupW(p.c_str(), icon);
    }
    STDMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    STDMETHODIMP GetCanonicalName(GUID* guid) override { *guid = m_id; return S_OK; }

    STDMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
    {
        *state = ECS_HIDDEN;
        if (!items) return S_OK;
        DWORD n = 0;
        if (FAILED(items->GetCount(&n)) || n == 0) return S_OK;
        for (DWORD i = 0; i < n; ++i)
        {
            IShellItem* item = nullptr;
            if (FAILED(items->GetItemAt(i, &item))) return S_OK;
            PWSTR path = nullptr;
            HRESULT hr = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
            item->Release();
            if (FAILED(hr) || !path) return S_OK;
            bool ok = IsGoodFile(path, m_exts, m_extCount);
            CoTaskMemFree(path);
            if (!ok) return S_OK;  // any non-matching item hides the command
        }
        *state = ECS_ENABLED;
        return S_OK;
    }

    STDMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (!items) return S_OK;
        DWORD n = 0;
        if (FAILED(items->GetCount(&n))) return S_OK;
        std::wstring dir = ModuleDir();
        std::wstring exe = dir + L"\\GeminiMenu.Worker.exe";
        for (DWORD i = 0; i < n; ++i)  // every file gets its own worker process
        {
            IShellItem* item = nullptr;
            if (FAILED(items->GetItemAt(i, &item))) continue;
            PWSTR path = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path)
            {
                std::wstring cmd = L"\"" + exe + L"\" --mode " + m_mode + L" --file \"" + path + L"\"";
                STARTUPINFOW si = {};
                si.cb = sizeof(si);
                PROCESS_INFORMATION pi = {};
                if (CreateProcessW(nullptr, &cmd[0], nullptr, nullptr, FALSE, 0, nullptr, dir.c_str(), &si, &pi))
                {
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                }
                CoTaskMemFree(path);
            }
            item->Release();
        }
        return S_OK;
    }

    STDMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { *flags = ECF_DEFAULT; return S_OK; }
    STDMETHODIMP EnumSubCommands(IEnumExplorerCommand** e) override { *e = nullptr; return E_NOTIMPL; }

private:
    LONG m_ref;
    CLSID m_id;
    const wchar_t* m_title;
    const wchar_t* m_icon;
    const wchar_t* m_mode;
    const wchar_t* const* m_exts;
    size_t m_extCount;
};

class Factory : public IClassFactory
{
public:
    explicit Factory(const CLSID& id) : m_ref(1), m_id(id) { InterlockedIncrement(&g_objCount); }
    virtual ~Factory() { InterlockedDecrement(&g_objCount); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return r;
    }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        Command* cmd = nullptr;
        if (IsEqualCLSID(m_id, CLSID_TranscribeCmd))
            cmd = new (std::nothrow) Command(m_id, L"תמלל הקלטה", L"mic.ico", L"transcribe", kAudioExt, ARRAYSIZE(kAudioExt));
        else
            cmd = new (std::nothrow) Command(m_id, L"סכם לי", L"doc.ico", L"summarize", kDocExt, ARRAYSIZE(kDocExt));
        if (!cmd) return E_OUTOFMEMORY;
        HRESULT hr = cmd->QueryInterface(riid, ppv);
        cmd->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_objCount); else InterlockedDecrement(&g_objCount);
        return S_OK;
    }

private:
    LONG m_ref;
    CLSID m_id;
};

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hModule = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!IsEqualCLSID(clsid, CLSID_TranscribeCmd) && !IsEqualCLSID(clsid, CLSID_SummarizeCmd))
        return CLASS_E_CLASSNOTAVAILABLE;
    Factory* f = new (std::nothrow) Factory(clsid);
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return g_objCount == 0 ? S_OK : S_FALSE;
}
