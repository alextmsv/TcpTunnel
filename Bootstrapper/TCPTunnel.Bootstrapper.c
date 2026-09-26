#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

#define RESOURCE_MAIN_ASSEMBLY 101
#define RESOURCE_DEPS_JSON 102
#define RESOURCE_RUNTIME_CONFIG 103
#define RESOURCE_SHARP_OPEN_NAT 104
#define RESOURCE_PAYLOAD_ID 105
#define RESOURCE_WINDOWS_SDK 106
#define RESOURCE_WINRT_RUNTIME 107

#define COREHOST_LIB_LOAD_FAILURE ((int32_t)0x80008082)
#define COREHOST_LIB_MISSING_FAILURE ((int32_t)0x80008083)
#define FRAMEWORK_MISSING_FAILURE ((int32_t)0x80008096)

#define DOWNLOAD_URL L"https://dotnet.microsoft.com/ru-ru/download/dotnet/thank-you/runtime-8.0.31-windows-x64-installer"
#define MAX_RUNTIME_PATH 32768

typedef void* hostfxr_handle;

struct hostfxr_initialize_parameters
{
    size_t size;
    const wchar_t* host_path;
    const wchar_t* dotnet_root;
};

typedef int32_t(__cdecl* hostfxr_initialize_for_dotnet_command_line_fn)(
    int argc,
    const wchar_t** argv,
    const struct hostfxr_initialize_parameters* parameters,
    hostfxr_handle* host_context_handle);
typedef int32_t(__cdecl* hostfxr_run_app_fn)(hostfxr_handle host_context_handle);
typedef int32_t(__cdecl* hostfxr_close_fn)(hostfxr_handle host_context_handle);

typedef struct version_number
{
    unsigned int major;
    unsigned int minor;
    unsigned int patch;
    unsigned int revision;
} version_number;

static BOOL combine_path(
    wchar_t* destination,
    size_t destination_count,
    const wchar_t* left,
    const wchar_t* right)
{
    if (destination == NULL || destination_count == 0 || left == NULL || right == NULL)
        return FALSE;

    return _snwprintf_s(destination, destination_count, _TRUNCATE, L"%s\\%s", left, right) >= 0;
}

static BOOL directory_exists(const wchar_t* path)
{
    DWORD attributes = GetFileAttributesW(path);
    return attributes != INVALID_FILE_ATTRIBUTES &&
           (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 &&
           (attributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0;
}

static BOOL ensure_directory(const wchar_t* path)
{
    if (CreateDirectoryW(path, NULL))
        return TRUE;
    return GetLastError() == ERROR_ALREADY_EXISTS && directory_exists(path);
}

static BOOL parse_version(const wchar_t* text, version_number* version)
{
    int parsed;
    if (text == NULL || version == NULL)
        return FALSE;

    ZeroMemory(version, sizeof(*version));
    parsed = swscanf_s(
        text,
        L"%u.%u.%u.%u",
        &version->major,
        &version->minor,
        &version->patch,
        &version->revision);
    return parsed >= 2;
}

static int compare_versions(const version_number* left, const version_number* right)
{
    if (left->major != right->major)
        return left->major > right->major ? 1 : -1;
    if (left->minor != right->minor)
        return left->minor > right->minor ? 1 : -1;
    if (left->patch != right->patch)
        return left->patch > right->patch ? 1 : -1;
    if (left->revision != right->revision)
        return left->revision > right->revision ? 1 : -1;
    return 0;
}

static BOOL find_hostfxr_in_root(
    const wchar_t* dotnet_root,
    wchar_t* hostfxr_path,
    size_t hostfxr_path_count)
{
    wchar_t fxr_root[MAX_RUNTIME_PATH];
    wchar_t search_pattern[MAX_RUNTIME_PATH];
    wchar_t best_name[MAX_PATH] = L"";
    version_number best_version = { 0, 0, 0, 0 };
    WIN32_FIND_DATAW entry;
    HANDLE search;

    if (dotnet_root == NULL || dotnet_root[0] == L'\0' || !directory_exists(dotnet_root))
        return FALSE;
    if (!combine_path(fxr_root, _countof(fxr_root), dotnet_root, L"host\\fxr") ||
        !combine_path(search_pattern, _countof(search_pattern), fxr_root, L"*"))
        return FALSE;

    search = FindFirstFileW(search_pattern, &entry);
    if (search == INVALID_HANDLE_VALUE)
        return FALSE;

    do
    {
        version_number candidate;
        if ((entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0 ||
            entry.cFileName[0] == L'.' ||
            !parse_version(entry.cFileName, &candidate))
            continue;

        if (best_name[0] == L'\0' || compare_versions(&candidate, &best_version) > 0)
        {
            wcscpy_s(best_name, _countof(best_name), entry.cFileName);
            best_version = candidate;
        }
    } while (FindNextFileW(search, &entry));

    FindClose(search);
    if (best_name[0] == L'\0' ||
        !combine_path(search_pattern, _countof(search_pattern), fxr_root, best_name) ||
        !combine_path(hostfxr_path, hostfxr_path_count, search_pattern, L"hostfxr.dll"))
        return FALSE;

    return GetFileAttributesW(hostfxr_path) != INVALID_FILE_ATTRIBUTES;
}

static BOOL try_dotnet_root(
    const wchar_t* candidate,
    wchar_t* dotnet_root,
    size_t dotnet_root_count,
    wchar_t* hostfxr_path,
    size_t hostfxr_path_count)
{
    if (!find_hostfxr_in_root(candidate, hostfxr_path, hostfxr_path_count))
        return FALSE;
    return wcscpy_s(dotnet_root, dotnet_root_count, candidate) == 0;
}

static BOOL find_dotnet_root_and_hostfxr(
    wchar_t* dotnet_root,
    size_t dotnet_root_count,
    wchar_t* hostfxr_path,
    size_t hostfxr_path_count)
{
    wchar_t candidate[MAX_RUNTIME_PATH];
    DWORD length;
    HKEY key;

    length = GetEnvironmentVariableW(L"DOTNET_ROOT_X64", candidate, _countof(candidate));
    if (length > 0 && length < _countof(candidate) &&
        try_dotnet_root(candidate, dotnet_root, dotnet_root_count, hostfxr_path, hostfxr_path_count))
        return TRUE;

    length = GetEnvironmentVariableW(L"DOTNET_ROOT", candidate, _countof(candidate));
    if (length > 0 && length < _countof(candidate) &&
        try_dotnet_root(candidate, dotnet_root, dotnet_root_count, hostfxr_path, hostfxr_path_count))
        return TRUE;

    if (RegOpenKeyExW(
            HKEY_LOCAL_MACHINE,
            L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64\\sharedhost",
            0,
            KEY_QUERY_VALUE | KEY_WOW64_64KEY,
            &key) == ERROR_SUCCESS)
    {
        DWORD type = 0;
        DWORD bytes = sizeof(candidate);
        LONG result = RegQueryValueExW(
            key,
            L"Path",
            NULL,
            &type,
            (LPBYTE)candidate,
            &bytes);
        RegCloseKey(key);
        if (result == ERROR_SUCCESS && type == REG_SZ &&
            try_dotnet_root(candidate, dotnet_root, dotnet_root_count, hostfxr_path, hostfxr_path_count))
            return TRUE;
    }

    length = GetEnvironmentVariableW(L"ProgramFiles", candidate, _countof(candidate));
    if (length > 0 && length < _countof(candidate))
    {
        wchar_t program_files_dotnet[MAX_RUNTIME_PATH];
        if (combine_path(program_files_dotnet, _countof(program_files_dotnet), candidate, L"dotnet") &&
            try_dotnet_root(program_files_dotnet, dotnet_root, dotnet_root_count, hostfxr_path, hostfxr_path_count))
            return TRUE;
    }

    return FALSE;
}

static BOOL get_resource_bytes(int resource_id, const void** data, DWORD* size)
{
    HRSRC resource;
    HGLOBAL loaded;

    if (data == NULL || size == NULL)
        return FALSE;
    resource = FindResourceW(NULL, MAKEINTRESOURCEW(resource_id), RT_RCDATA);
    if (resource == NULL)
        return FALSE;
    loaded = LoadResource(NULL, resource);
    if (loaded == NULL)
        return FALSE;
    *size = SizeofResource(NULL, resource);
    *data = LockResource(loaded);
    return *data != NULL && *size > 0;
}

static BOOL get_payload_id(wchar_t* payload_id, size_t payload_id_count)
{
    const char* bytes;
    DWORD size;
    size_t index;

    if (!get_resource_bytes(RESOURCE_PAYLOAD_ID, (const void**)&bytes, &size) ||
        size < 8 || size >= payload_id_count)
        return FALSE;

    for (index = 0; index < size; ++index)
    {
        char value = bytes[index];
        if (!((value >= '0' && value <= '9') || (value >= 'A' && value <= 'F')))
            return FALSE;
        payload_id[index] = (wchar_t)value;
    }
    payload_id[size] = L'\0';
    return TRUE;
}

static BOOL file_matches_bytes(const wchar_t* path, const void* data, DWORD size)
{
    HANDLE file;
    LARGE_INTEGER file_size;
    BYTE* buffer;
    DWORD read = 0;
    BOOL matches = FALSE;

    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE)
        return FALSE;
    if (!GetFileSizeEx(file, &file_size) || file_size.QuadPart != size)
    {
        CloseHandle(file);
        return FALSE;
    }

    buffer = (BYTE*)HeapAlloc(GetProcessHeap(), 0, size);
    if (buffer != NULL)
    {
        if (ReadFile(file, buffer, size, &read, NULL) && read == size)
            matches = memcmp(buffer, data, size) == 0;
        HeapFree(GetProcessHeap(), 0, buffer);
    }
    CloseHandle(file);
    return matches;
}

static BOOL write_resource_file(int resource_id, const wchar_t* path)
{
    const void* data;
    DWORD size;
    HANDLE file;
    DWORD written = 0;
    DWORD attributes;
    wchar_t temporary_path[MAX_RUNTIME_PATH];

    if (!get_resource_bytes(resource_id, &data, &size))
        return FALSE;
    if (file_matches_bytes(path, data, size))
        return TRUE;

    attributes = GetFileAttributesW(path);
    if (attributes != INVALID_FILE_ATTRIBUTES &&
        ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ||
         (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0))
        return FALSE;
    if (_snwprintf_s(
            temporary_path,
            _countof(temporary_path),
            _TRUNCATE,
            L"%s.%lu.tmp",
            path,
            GetCurrentProcessId()) < 0)
        return FALSE;

    file = CreateFileW(
        temporary_path,
        GENERIC_WRITE,
        0,
        NULL,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_TEMPORARY,
        NULL);
    if (file == INVALID_HANDLE_VALUE)
        return FALSE;
    if (!WriteFile(file, data, size, &written, NULL) || written != size || !FlushFileBuffers(file))
    {
        CloseHandle(file);
        DeleteFileW(temporary_path);
        return FALSE;
    }
    if (!CloseHandle(file))
    {
        DeleteFileW(temporary_path);
        return FALSE;
    }
    if (MoveFileExW(
            temporary_path,
            path,
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        return TRUE;

    DeleteFileW(temporary_path);
    return file_matches_bytes(path, data, size);
}

static BOOL extract_managed_payload(
    wchar_t* assembly_path,
    size_t assembly_path_count)
{
    wchar_t local_app_data[MAX_RUNTIME_PATH];
    wchar_t tcptunnel_directory[MAX_RUNTIME_PATH];
    wchar_t runtime_directory[MAX_RUNTIME_PATH];
    wchar_t version_directory[MAX_RUNTIME_PATH];
    wchar_t payload_id[65];
    wchar_t file_path[MAX_RUNTIME_PATH];

    if (FAILED(SHGetFolderPathW(
            NULL,
            CSIDL_LOCAL_APPDATA | CSIDL_FLAG_CREATE,
            NULL,
            SHGFP_TYPE_CURRENT,
            local_app_data)) ||
        !get_payload_id(payload_id, _countof(payload_id)) ||
        !combine_path(tcptunnel_directory, _countof(tcptunnel_directory), local_app_data, L"TCPTunnel") ||
        !ensure_directory(tcptunnel_directory) ||
        !combine_path(runtime_directory, _countof(runtime_directory), tcptunnel_directory, L"runtime") ||
        !ensure_directory(runtime_directory) ||
        !combine_path(version_directory, _countof(version_directory), runtime_directory, payload_id) ||
        !ensure_directory(version_directory))
        return FALSE;

    if (!combine_path(assembly_path, assembly_path_count, version_directory, L"TCPTunnel.dll") ||
        !write_resource_file(RESOURCE_MAIN_ASSEMBLY, assembly_path) ||
        !combine_path(file_path, _countof(file_path), version_directory, L"TCPTunnel.deps.json") ||
        !write_resource_file(RESOURCE_DEPS_JSON, file_path) ||
        !combine_path(file_path, _countof(file_path), version_directory, L"TCPTunnel.runtimeconfig.json") ||
        !write_resource_file(RESOURCE_RUNTIME_CONFIG, file_path) ||
        !combine_path(file_path, _countof(file_path), version_directory, L"SharpOpenNat.dll") ||
        !write_resource_file(RESOURCE_SHARP_OPEN_NAT, file_path) ||
        !combine_path(file_path, _countof(file_path), version_directory, L"Microsoft.Windows.SDK.NET.dll") ||
        !write_resource_file(RESOURCE_WINDOWS_SDK, file_path) ||
        !combine_path(file_path, _countof(file_path), version_directory, L"WinRT.Runtime.dll") ||
        !write_resource_file(RESOURCE_WINRT_RUNTIME, file_path))
        return FALSE;

    return TRUE;
}

static void write_stderr_text(const wchar_t* text)
{
    HANDLE error_output = GetStdHandle(STD_ERROR_HANDLE);
    DWORD written;
    int utf8_size;
    char* utf8;

    if (error_output == NULL || error_output == INVALID_HANDLE_VALUE || text == NULL)
        return;
    if (WriteConsoleW(error_output, text, (DWORD)wcslen(text), &written, NULL))
        return;

    utf8_size = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    if (utf8_size <= 1)
        return;
    utf8 = (char*)HeapAlloc(GetProcessHeap(), 0, (size_t)utf8_size);
    if (utf8 == NULL)
        return;
    if (WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, utf8_size, NULL, NULL) > 1)
        WriteFile(error_output, utf8, (DWORD)(utf8_size - 1), &written, NULL);
    HeapFree(GetProcessHeap(), 0, utf8);
}

static int exit_after_key(int exit_code)
{
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    DWORD mode;
    DWORD read;
    INPUT_RECORD record;

    if (input == NULL || input == INVALID_HANDLE_VALUE ||
        GetFileType(input) != FILE_TYPE_CHAR || !GetConsoleMode(input, &mode))
        return exit_code;

    write_stderr_text(L"\r\nНажмите любую клавишу для выхода... / Press any key to exit...\r\n");
    FlushConsoleInputBuffer(input);
    for (;;)
    {
        if (!ReadConsoleInputW(input, &record, 1, &read) || read == 0)
            break;
        if (record.EventType == KEY_EVENT && record.Event.KeyEvent.bKeyDown)
            break;
    }
    return exit_code;
}

static void open_runtime_download_page(void)
{
    HINSTANCE result;
    wchar_t suppress_browser[2];
    write_stderr_text(
        L"\r\nДля запуска TCPTunnel требуется .NET 8 Runtime (x64).\r\n"
        L"TCPTunnel requires .NET 8 Runtime (x64).\r\n"
        L"Скачиваю установщик .NET 8 Runtime через браузер / Downloading the .NET 8 Runtime installer: " DOWNLOAD_URL L"\r\n"
        L"Запустите скачанный установщик, затем снова откройте TCPTunnel.\r\n"
        L"Run the downloaded installer, then open TCPTunnel again.\r\n\r\n");
    if (GetEnvironmentVariableW(
            L"TCPTUNNEL_SUPPRESS_RUNTIME_DOWNLOAD",
            suppress_browser,
            _countof(suppress_browser)) == 1 &&
        suppress_browser[0] == L'1')
        return;
    result = ShellExecuteW(NULL, L"open", DOWNLOAD_URL, NULL, NULL, SW_SHOWNORMAL);
    if ((INT_PTR)result <= 32)
        write_stderr_text(
            L"Не удалось открыть браузер. Откройте ссылку вручную: " DOWNLOAD_URL L"\r\n");
}

static BOOL is_runtime_missing_error(int32_t error_code)
{
    return error_code == FRAMEWORK_MISSING_FAILURE ||
           error_code == COREHOST_LIB_MISSING_FAILURE ||
           error_code == COREHOST_LIB_LOAD_FAILURE;
}

int wmain(int argc, wchar_t** argv)
{
    wchar_t dotnet_root[MAX_RUNTIME_PATH];
    wchar_t hostfxr_path[MAX_RUNTIME_PATH];
    wchar_t host_path[MAX_RUNTIME_PATH];
    wchar_t assembly_path[MAX_RUNTIME_PATH];
    HMODULE hostfxr;
    hostfxr_initialize_for_dotnet_command_line_fn initialize;
    hostfxr_run_app_fn run_app;
    hostfxr_close_fn close_context;
    const wchar_t** managed_argv;
    struct hostfxr_initialize_parameters parameters;
    hostfxr_handle context = NULL;
    int32_t result;
    int index;

    if (!find_dotnet_root_and_hostfxr(
            dotnet_root,
            _countof(dotnet_root),
            hostfxr_path,
            _countof(hostfxr_path)))
    {
        open_runtime_download_page();
        return exit_after_key((int)FRAMEWORK_MISSING_FAILURE);
    }

    if (!extract_managed_payload(assembly_path, _countof(assembly_path)))
    {
        write_stderr_text(L"TCPTunnel: не удалось подготовить файлы приложения.\r\n");
        return exit_after_key(ERROR_WRITE_FAULT);
    }

    hostfxr = LoadLibraryW(hostfxr_path);
    if (hostfxr == NULL)
    {
        open_runtime_download_page();
        return exit_after_key((int)COREHOST_LIB_LOAD_FAILURE);
    }

    initialize = (hostfxr_initialize_for_dotnet_command_line_fn)GetProcAddress(
        hostfxr,
        "hostfxr_initialize_for_dotnet_command_line");
    run_app = (hostfxr_run_app_fn)GetProcAddress(hostfxr, "hostfxr_run_app");
    close_context = (hostfxr_close_fn)GetProcAddress(hostfxr, "hostfxr_close");
    if (initialize == NULL || run_app == NULL || close_context == NULL)
    {
        FreeLibrary(hostfxr);
        open_runtime_download_page();
        return exit_after_key((int)COREHOST_LIB_LOAD_FAILURE);
    }

    managed_argv = (const wchar_t**)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(wchar_t*) * argc);
    if (managed_argv == NULL)
    {
        FreeLibrary(hostfxr);
        write_stderr_text(L"TCPTunnel: недостаточно памяти для запуска.\r\n");
        return exit_after_key(ERROR_NOT_ENOUGH_MEMORY);
    }
    managed_argv[0] = assembly_path;
    for (index = 1; index < argc; ++index)
        managed_argv[index] = argv[index];

    if (GetModuleFileNameW(NULL, host_path, _countof(host_path)) == 0)
    {
        DWORD error = GetLastError();
        HeapFree(GetProcessHeap(), 0, managed_argv);
        FreeLibrary(hostfxr);
        write_stderr_text(L"TCPTunnel: не удалось определить путь к программе.\r\n");
        return exit_after_key((int)error);
    }

    parameters.size = sizeof(parameters);
    parameters.host_path = host_path;
    parameters.dotnet_root = dotnet_root;
    result = initialize(argc, managed_argv, &parameters, &context);
    HeapFree(GetProcessHeap(), 0, managed_argv);

    if (result < 0)
    {
        if (context != NULL)
            close_context(context);
        FreeLibrary(hostfxr);
        if (is_runtime_missing_error(result))
            open_runtime_download_page();
        return exit_after_key((int)result);
    }

    result = run_app(context);
    close_context(context);
    FreeLibrary(hostfxr);
    return (int)result;
}
