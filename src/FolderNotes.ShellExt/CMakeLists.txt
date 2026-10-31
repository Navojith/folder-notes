# FolderNotes.ShellExt: native in-proc shell extension loaded by explorer.exe (ADR-002).
# Rules (CLAUDE.md, specs/04): C++20, /utf-8 /W4 /WX, static CRT, no runtime dependencies, read-only.
cmake_minimum_required(VERSION 3.25)
project(FolderNotesShellExt LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")   # static CRT (/MT)

add_library(FolderNotes.ShellExt SHARED
    src/DllMain.cpp
    src/FolderNotes.ShellExt.def
)
target_compile_definitions(FolderNotes.ShellExt PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(FolderNotes.ShellExt PRIVATE /utf-8 /W4 /WX /permissive- /EHsc)
target_link_libraries(FolderNotes.ShellExt PRIVATE ole32 shell32 shlwapi advapi32)
