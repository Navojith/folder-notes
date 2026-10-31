// FolderNotes.ShellExt: scaffold (T1.1). Exports the COM entry points but registers no classes yet.
// The infotip handler arrives in T4.3; see specs/04-explorer-integration.md for the hard rules.
#include <windows.h>
#include <objbase.h>

namespace
{
HMODULE g_module = nullptr;
LONG g_objects = 0;
LONG g_locks = 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID, REFIID, void** ppv)
{
    if (ppv) *ppv = nullptr;
    return CLASS_E_CLASSNOTAVAILABLE;
}

STDAPI DllCanUnloadNow()
{
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}
