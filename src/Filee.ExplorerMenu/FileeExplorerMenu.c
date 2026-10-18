// "Convert with Filee" in the top-level Windows 11 File Explorer context menu: a tiny in-process COM server
// (IClassFactory + IExplorerCommand) that Explorer loads through the Filee sparse package. Built with
// build/build-explorer-menu.ps1; the package side lives in src/Filee.Platform.Windows/ExplorerMenu*.cs.
//
// This code runs inside explorer.exe, so it is deliberately small and defensive:
//  - no C runtime (linked with -nostdlib): only kernel32/user32/ole32/shell32/shlwapi are used;
//  - menu construction (GetTitle/GetState) only reads one tiny local file and shell attributes;
//  - Invoke starts Filee.exe next to this DLL and returns immediately (no waiting, no network).

#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#define CINTERFACE
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shlwapi.h>

// {6DB0E670-807F-4D22-BE3F-20492D0F4AB1}; must match ExplorerMenuPackage.ClassId (C#) and the package manifest.
static const CLSID kClsidCommand = {0x6db0e670, 0x807f, 0x4d22, {0xbe, 0x3f, 0x20, 0x49, 0x2d, 0x0f, 0x4a, 0xb1}};
// Local copies of the interface and folder ids, so the DLL does not need libuuid.
static const IID kIidUnknown = {0x00000000, 0x0000, 0x0000, {0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};
static const IID kIidClassFactory = {0x00000001, 0x0000, 0x0000, {0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};
static const IID kIidExplorerCommand = {0xa08ce4d0, 0xfa25, 0x44ab, {0xb5, 0x7c, 0xc7, 0xb1, 0xc3, 0x23, 0xe0, 0xb9}};
static const KNOWNFOLDERID kFolderRoamingAppData = {0x3eb685db, 0x65f9, 0x4cf6, {0xa0, 0x3a, 0xe3, 0xef, 0x65, 0x72, 0x9f, 0x3d}};

#define DEFAULT_TITLE L"Convert with Filee"
// Written by the app into its data folder (%APPDATA%\Filee, which survives app updates) while the Explorer menu
// setting is on; first line = the title in the UI language. No file = the entry is hidden, so the setting can switch
// the entry off and on again without re-registering the package (which needs administrator rights).
// Must match ExplorerMenuPackage.TitleFileName.
#define TITLE_FILE L"Filee\\explorer-menu.txt"
#define APP_EXE L"Filee.exe"
#define LIST_PREFIX L"Filee-convert-"       // CommandLine.ListFilePrefix in the app
// CreateProcess accepts at most 32767 characters; longer selections go through a list file.
#define MAX_COMMAND_LINE 32000

static HMODULE g_module;
static volatile LONG g_locks; // live objects + class factory references + LockServer calls

static BOOL SameGuid(const GUID *a, const GUID *b)
{
    const BYTE *x = (const BYTE *)a;
    const BYTE *y = (const BYTE *)b;
    for (int i = 0; i < (int)sizeof(GUID); i++)
    {
        if (x[i] != y[i])
            return FALSE;
    }
    return TRUE;
}

// ---- Small helpers (no CRT) ------------------------------------------------------------------------------------

/// Growable UTF-16 buffer on the process heap.
typedef struct Text
{
    WCHAR *data;
    SIZE_T length;
    SIZE_T capacity;
    BOOL failed;
} Text;

static void TextAppendN(Text *text, const WCHAR *value, SIZE_T count)
{
    if (text->failed)
        return;
    if (text->length + count + 1 > text->capacity)
    {
        SIZE_T capacity = text->capacity ? text->capacity : 1024;
        while (text->length + count + 1 > capacity)
            capacity *= 2;
        WCHAR *grown = text->data
                           ? (WCHAR *)HeapReAlloc(GetProcessHeap(), 0, text->data, capacity * sizeof(WCHAR))
                           : (WCHAR *)HeapAlloc(GetProcessHeap(), 0, capacity * sizeof(WCHAR));
        if (!grown)
        {
            text->failed = TRUE;
            return;
        }
        text->data = grown;
        text->capacity = capacity;
    }
    for (SIZE_T i = 0; i < count; i++)
        text->data[text->length + i] = value[i];
    text->length += count;
    text->data[text->length] = 0;
}

static void TextAppend(Text *text, const WCHAR *value)
{
    TextAppendN(text, value, (SIZE_T)lstrlenW(value));
}

static void TextFree(Text *text)
{
    if (text->data)
        HeapFree(GetProcessHeap(), 0, text->data);
    text->data = NULL;
    text->length = text->capacity = 0;
}

/// Full path of a file next to this DLL (the Filee install folder, i.e. the package's external location).
static BOOL PathNextToModule(const WCHAR *name, WCHAR *buffer, DWORD size)
{
    DWORD length = GetModuleFileNameW(g_module, buffer, size);
    if (length == 0 || length >= size)
        return FALSE;
    while (length > 0 && buffer[length - 1] != L'\\')
        length--;
    if (length == 0 || length + (DWORD)lstrlenW(name) >= size)
        return FALSE;
    lstrcpyW(buffer + length, name);
    return TRUE;
}

/// Reads the localized title the app writes into its data folder. Returns FALSE when missing or unusable.
static BOOL ReadTitle(WCHAR *title, int size)
{
    PWSTR folder = NULL;
    if (FAILED(SHGetKnownFolderPath(&kFolderRoamingAppData, 0, NULL, &folder)) || !folder)
        return FALSE;
    WCHAR path[1024];
    BOOL fits = lstrlenW(folder) + 1 + lstrlenW(TITLE_FILE) < (int)ARRAYSIZE(path);
    if (fits)
    {
        lstrcpyW(path, folder);
        lstrcatW(path, L"\\");
        lstrcatW(path, TITLE_FILE);
    }
    CoTaskMemFree(folder);
    if (!fits)
        return FALSE;
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE)
        return FALSE;
    char bytes[512];
    DWORD read = 0;
    BOOL ok = ReadFile(file, bytes, sizeof(bytes) - 1, &read, NULL);
    CloseHandle(file);
    if (!ok)
        return FALSE;

    int start = 0;
    if (read >= 3 && (BYTE)bytes[0] == 0xEF && (BYTE)bytes[1] == 0xBB && (BYTE)bytes[2] == 0xBF)
        start = 3; // UTF-8 byte order mark
    int end = start;
    while (end < (int)read && bytes[end] != '\r' && bytes[end] != '\n')
        end++;
    if (end == start)
        return FALSE;
    int chars = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes + start, end - start, title, size - 1);
    if (chars <= 0)
        return FALSE;
    title[chars] = 0;
    while (chars > 0 && (title[chars - 1] == L' ' || title[chars - 1] == L'\t'))
        title[--chars] = 0;
    return chars > 0;
}

/// Writes the paths (one per line, UTF-8) to a new file in %TEMP% for `Filee.exe --convert-list <file>`.
static BOOL WriteListFile(const Text *lines, WCHAR *path, DWORD size)
{
    WCHAR folder[MAX_PATH + 1];
    DWORD length = GetTempPathW(ARRAYSIZE(folder), folder);
    if (length == 0 || length >= ARRAYSIZE(folder))
        return FALSE;

    int bytes = WideCharToMultiByte(CP_UTF8, 0, lines->data, (int)lines->length, NULL, 0, NULL, NULL);
    if (bytes <= 0)
        return FALSE;
    char *utf8 = (char *)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)bytes);
    if (!utf8)
        return FALSE;
    WideCharToMultiByte(CP_UTF8, 0, lines->data, (int)lines->length, utf8, bytes, NULL, NULL);

    BOOL written = FALSE;
    for (int attempt = 0; attempt < 5 && !written; attempt++)
    {
        if (wsprintfW(path, L"%s" LIST_PREFIX L"%lu-%lu-%d.txt", folder, GetCurrentProcessId(), GetTickCount(), attempt) <= 0
            || (DWORD)lstrlenW(path) >= size)
            break;
        HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, NULL);
        if (file == INVALID_HANDLE_VALUE)
            continue; // name collision: try the next suffix
        DWORD done = 0;
        written = WriteFile(file, utf8, (DWORD)bytes, &done, NULL) && done == (DWORD)bytes;
        CloseHandle(file);
        if (!written)
            DeleteFileW(path);
    }
    HeapFree(GetProcessHeap(), 0, utf8);
    return written;
}

/// Starts Filee.exe with the given command line and returns without waiting for it.
static HRESULT StartFilee(const WCHAR *exe, WCHAR *commandLine)
{
    WCHAR folder[1024];
    lstrcpynW(folder, exe, ARRAYSIZE(folder));
    for (int i = lstrlenW(folder) - 1; i >= 0 && folder[i] != L'\\'; i--)
        folder[i] = 0;

    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);
    if (!CreateProcessW(exe, commandLine, NULL, NULL, FALSE, 0, NULL, folder, &startup, &process))
        return HRESULT_FROM_WIN32(GetLastError());
    // Explorer owns the foreground; let the running Filee bring its donut to the front.
    AllowSetForegroundWindow(process.dwProcessId);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return S_OK;
}

// ---- IExplorerCommand ------------------------------------------------------------------------------------------

typedef struct Command
{
    IExplorerCommand iface;
    volatile LONG refs;
} Command;

static HRESULT STDMETHODCALLTYPE Command_QueryInterface(IExplorerCommand *self, REFIID riid, void **result)
{
    if (!result)
        return E_POINTER;
    if (SameGuid(riid, &kIidUnknown) || SameGuid(riid, &kIidExplorerCommand))
    {
        *result = self;
        IExplorerCommand_AddRef(self);
        return S_OK;
    }
    *result = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE Command_AddRef(IExplorerCommand *self)
{
    return (ULONG)InterlockedIncrement(&((Command *)self)->refs);
}

static ULONG STDMETHODCALLTYPE Command_Release(IExplorerCommand *self)
{
    LONG refs = InterlockedDecrement(&((Command *)self)->refs);
    if (refs == 0)
    {
        HeapFree(GetProcessHeap(), 0, self);
        InterlockedDecrement(&g_locks);
    }
    return (ULONG)refs;
}

static HRESULT STDMETHODCALLTYPE Command_GetTitle(IExplorerCommand *self, IShellItemArray *items, LPWSTR *name)
{
    (void)self;
    (void)items;
    if (!name)
        return E_POINTER;
    *name = NULL;
    WCHAR title[128];
    if (!ReadTitle(title, ARRAYSIZE(title)))
        lstrcpyW(title, DEFAULT_TITLE);
    return SHStrDupW(title, name);
}

static HRESULT STDMETHODCALLTYPE Command_GetIcon(IExplorerCommand *self, IShellItemArray *items, LPWSTR *icon)
{
    (void)self;
    (void)items;
    if (!icon)
        return E_POINTER;
    *icon = NULL;
    WCHAR path[1024];
    if (!PathNextToModule(APP_EXE L",0", path, ARRAYSIZE(path)))
        return E_FAIL;
    return SHStrDupW(path, icon);
}

static HRESULT STDMETHODCALLTYPE Command_GetToolTip(IExplorerCommand *self, IShellItemArray *items, LPWSTR *tip)
{
    (void)self;
    (void)items;
    if (tip)
        *tip = NULL;
    return E_NOTIMPL;
}

static HRESULT STDMETHODCALLTYPE Command_GetCanonicalName(IExplorerCommand *self, GUID *name)
{
    (void)self;
    if (!name)
        return E_POINTER;
    *name = kClsidCommand;
    return S_OK;
}

/// Shown only while the app's setting is on (the title file exists) and every selected item is a file on disk:
/// folders, drives and virtual items (e.g. inside a ZIP folder) have no file-system stream.
static HRESULT STDMETHODCALLTYPE Command_GetState(IExplorerCommand *self, IShellItemArray *items, BOOL okToBeSlow,
                                                  EXPCMDSTATE *state)
{
    (void)self;
    (void)okToBeSlow;
    if (!state)
        return E_POINTER;
    *state = ECS_HIDDEN;
    WCHAR title[128];
    if (!ReadTitle(title, ARRAYSIZE(title)))
        return S_OK;
    const SFGAOF wanted = SFGAO_FILESYSTEM | SFGAO_STREAM;
    SFGAOF attributes = 0;
    if (items && SUCCEEDED(IShellItemArray_GetAttributes(items, SIATTRIBFLAGS_AND, wanted, &attributes))
        && (attributes & wanted) == wanted)
        *state = ECS_ENABLED;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Command_Invoke(IExplorerCommand *self, IShellItemArray *items, IBindCtx *context)
{
    (void)self;
    (void)context;
    if (!items)
        return E_INVALIDARG;
    WCHAR exe[1024];
    if (!PathNextToModule(APP_EXE, exe, ARRAYSIZE(exe)))
        return E_FAIL;

    DWORD count = 0;
    HRESULT hr = IShellItemArray_GetCount(items, &count);
    if (FAILED(hr))
        return hr;

    // Build both forms at once: quoted arguments for the usual case, one path per line for huge selections.
    Text args = {0};
    Text lines = {0};
    TextAppend(&args, L"\"");
    TextAppend(&args, exe);
    TextAppend(&args, L"\" --convert");
    DWORD added = 0;
    for (DWORD i = 0; i < count; i++)
    {
        IShellItem *item = NULL;
        if (FAILED(IShellItemArray_GetItemAt(items, i, &item)))
            continue;
        LPWSTR path = NULL;
        if (SUCCEEDED(IShellItem_GetDisplayName(item, SIGDN_FILESYSPATH, &path)) && path)
        {
            TextAppend(&args, L" \"");
            TextAppend(&args, path);
            TextAppend(&args, L"\"");
            TextAppend(&lines, path);
            TextAppend(&lines, L"\r\n");
            added++;
            CoTaskMemFree(path);
        }
        IShellItem_Release(item);
    }

    if (args.failed || lines.failed)
        hr = E_OUTOFMEMORY;
    else if (added == 0)
        hr = S_FALSE;
    else if (args.length < MAX_COMMAND_LINE)
        hr = StartFilee(exe, args.data);
    else
    {
        WCHAR list[MAX_PATH + 64];
        hr = E_FAIL;
        if (WriteListFile(&lines, list, ARRAYSIZE(list)))
        {
            Text command = {0};
            TextAppend(&command, L"\"");
            TextAppend(&command, exe);
            TextAppend(&command, L"\" --convert-list \"");
            TextAppend(&command, list);
            TextAppend(&command, L"\"");
            hr = command.failed ? E_OUTOFMEMORY : StartFilee(exe, command.data);
            if (FAILED(hr))
                DeleteFileW(list);
            TextFree(&command);
        }
    }
    TextFree(&args);
    TextFree(&lines);
    return hr;
}

static HRESULT STDMETHODCALLTYPE Command_GetFlags(IExplorerCommand *self, EXPCMDFLAGS *flags)
{
    (void)self;
    if (!flags)
        return E_POINTER;
    *flags = ECF_DEFAULT;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Command_EnumSubCommands(IExplorerCommand *self, IEnumExplorerCommand **commands)
{
    (void)self;
    if (commands)
        *commands = NULL;
    return E_NOTIMPL;
}

static IExplorerCommandVtbl g_commandVtbl = {
    Command_QueryInterface,
    Command_AddRef,
    Command_Release,
    Command_GetTitle,
    Command_GetIcon,
    Command_GetToolTip,
    Command_GetCanonicalName,
    Command_GetState,
    Command_Invoke,
    Command_GetFlags,
    Command_EnumSubCommands,
};

// ---- IClassFactory (a single static instance) ------------------------------------------------------------------

static HRESULT STDMETHODCALLTYPE Factory_QueryInterface(IClassFactory *self, REFIID riid, void **result)
{
    if (!result)
        return E_POINTER;
    if (SameGuid(riid, &kIidUnknown) || SameGuid(riid, &kIidClassFactory))
    {
        *result = self;
        IClassFactory_AddRef(self);
        return S_OK;
    }
    *result = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE Factory_AddRef(IClassFactory *self)
{
    (void)self;
    return (ULONG)InterlockedIncrement(&g_locks);
}

static ULONG STDMETHODCALLTYPE Factory_Release(IClassFactory *self)
{
    (void)self;
    return (ULONG)InterlockedDecrement(&g_locks);
}

static HRESULT STDMETHODCALLTYPE Factory_CreateInstance(IClassFactory *self, IUnknown *outer, REFIID riid, void **result)
{
    (void)self;
    if (!result)
        return E_POINTER;
    *result = NULL;
    if (outer)
        return CLASS_E_NOAGGREGATION;
    Command *command = (Command *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Command));
    if (!command)
        return E_OUTOFMEMORY;
    command->iface.lpVtbl = &g_commandVtbl;
    command->refs = 1;
    InterlockedIncrement(&g_locks);
    HRESULT hr = Command_QueryInterface(&command->iface, riid, result);
    Command_Release(&command->iface); // the caller's reference (if any) keeps it alive
    return hr;
}

static HRESULT STDMETHODCALLTYPE Factory_LockServer(IClassFactory *self, BOOL lock)
{
    (void)self;
    if (lock)
        InterlockedIncrement(&g_locks);
    else
        InterlockedDecrement(&g_locks);
    return S_OK;
}

static IClassFactoryVtbl g_factoryVtbl = {
    Factory_QueryInterface,
    Factory_AddRef,
    Factory_Release,
    Factory_CreateInstance,
    Factory_LockServer,
};
static IClassFactory g_factory = {&g_factoryVtbl};

// ---- DLL exports -----------------------------------------------------------------------------------------------

HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void **result)
{
    if (!result)
        return E_POINTER;
    *result = NULL;
    if (!SameGuid(clsid, &kClsidCommand))
        return CLASS_E_CLASSNOTAVAILABLE;
    return Factory_QueryInterface(&g_factory, riid, result);
}

HRESULT __stdcall DllCanUnloadNow(void)
{
    return g_locks == 0 ? S_OK : S_FALSE;
}

// Entry point (there is no C runtime to initialise).
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
