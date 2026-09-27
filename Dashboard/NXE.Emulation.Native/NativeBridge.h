#pragma once

#ifdef NXE_EMULATION_NATIVE_EXPORTS
#define NXE_NATIVE_API extern "C" __declspec(dllexport)
#else
#define NXE_NATIVE_API extern "C" __declspec(dllimport)
#endif

// Stable ABI used by the packaged dashboard bridge. Engine implementations are
// selected by backend id and are deliberately kept out of the UI assembly.
NXE_NATIVE_API int __cdecl NXE_EmulationNative_GetBackendStatus(const wchar_t* backendId);
NXE_NATIVE_API int __cdecl NXE_EmulationNative_Start(const wchar_t* backendId, const wchar_t* gamePath);
NXE_NATIVE_API void __cdecl NXE_EmulationNative_Stop();
