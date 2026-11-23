// Tiny native launcher for the portable package: starts Program\NimCap.Screenshot.exe
// and forwards the arguments, resolving bare file paths to absolute paths.
#include <windows.h>
#include <string>

static std::wstring DirectoryOf(const std::wstring& path)
{
    const auto position = path.find_last_of(L"\\/");
    return position == std::wstring::npos ? path : path.substr(0, position);
}

int WINAPI WinMain(HINSTANCE, HINSTANCE, LPSTR, int)
{
    wchar_t exePath[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    const std::wstring program = DirectoryOf(exePath) + L"\\Program\\NimCap.Screenshot.exe";
    if (GetFileAttributesW(program.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        MessageBoxW(nullptr, L"Missing Program\\NimCap.Screenshot.exe", L"NimCap Screenshot", MB_OK | MB_ICONERROR);
        return 1;
    }

    std::wstring command = L"\"" + program + L"\"";
    int argumentCount = 0;
    LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
    if (arguments != nullptr)
    {
        for (int index = 1; index < argumentCount; ++index)
        {
            wchar_t full[MAX_PATH] = {};
            const bool resolved = GetFileAttributesW(arguments[index]) != INVALID_FILE_ATTRIBUTES &&
                GetFullPathNameW(arguments[index], MAX_PATH, full, nullptr) != 0;
            command += L" \"";
            command += resolved ? full : arguments[index];
            command += L"\"";
        }
        LocalFree(arguments);
    }

    STARTUPINFOW startup = {};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};
    if (!CreateProcessW(program.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
    {
        MessageBoxW(nullptr, L"Could not start Program\\NimCap.Screenshot.exe", L"NimCap Screenshot", MB_OK | MB_ICONERROR);
        return 1;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;
}
