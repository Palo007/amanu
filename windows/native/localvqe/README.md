# LocalVQE for Windows (offline echo cancellation)

`liblocalvqe.dll` and the two models are build assets, not
runtime downloads. `Amanu.App.csproj` copies them to `echo-runtime\` beside
`Amanu.exe` on build and publish; the app loads the DLL by full path
(`NativeLibrary.Load`), so it never meets the different `ggml.dll` in
`local-runtime\`. Behaviour contract: `docs/specs/2026-09-06-offline-echo.md`.

| Asset | Pin |
| --- | --- |
| LocalVQE | `f53063c9eb2a85f96479867d1dd911dc3bf6319b` (same as macOS) |
| ggml (vendored submodule) | `c044a8eeae2591faa0950c8b5e514cbc4bbfc4ca` |
| `fast` model (default), `localvqe-v1.4-aec-2.7K-f32.gguf` SHA-256 | `d79f824f6ee6f58b2a7108d1df90db51c42c15a7400a5addbd3be9972ba657a4` (16,864 bytes) |
| `thorough` model, `localvqe-v1.4-aec-200K-f32.gguf` SHA-256 | `b6e43138588a83bfe903ab5e143b4020b91c1e1629f5a575ac5855ff0003c731` (2,924,224 bytes) |
| DLL SHA-256 | `6b4aafd6003d213f7b0c14ace3a74fd32cf38e5e50345112e6ff87258c1240d8` |

The DLL is x86-64, CPU-only (SSE4.2 baseline, no BLAS, no OpenMP), built with
MinGW GCC and linked statically: it imports only KERNEL32, ADVAPI32 and
msvcrt. The model is checked against its SHA-256 every time it is loaded.
The `echo_model` setting picks the model. The 2.7K one is the adaptive filter
without the neural mask: on two real calls it removed echo 2.2 times faster
than the 200K one with the same word errors within three words. Both come from
https://huggingface.co/LocalAI-io/LocalVQE.

AVX2 and OpenMP builds of the DLL were measured on 2026-11-10 (workflow on the
`build-localvqe` branch): AVX2 gave bit-identical output at the same 2.3 ms per
hop, and OpenMP was eight times slower, so the SSE4.2 build stays.
Licenses: `windows/Licenses/LocalVQE.txt` (Apache-2.0) and
`windows/Licenses/ggml.txt` (MIT).

## Rebuilding the DLL

Toolchain: [w64devkit](https://github.com/skeeto/w64devkit) (GCC 16) with
CMake and Ninja on `PATH`, e.g. `C:\Users\<you>\.tools\w64devkit\bin`.

1. Check out LocalVQE at the pinned revision with its ggml submodule:

   ```sh
   git clone https://github.com/localai-org/LocalVQE.git LocalVQE
   cd LocalVQE
   git checkout f53063c9eb2a85f96479867d1dd911dc3bf6319b
   git submodule update --init --recursive
   git -C ggml/vendor/ggml rev-parse HEAD   # must print c044a8ee…
   ```

2. Apply `localvqe-windows.patch`. It keeps Windows out of LocalVQE's x86
   branch that builds every CPU variant as a separate runtime-loaded backend
   (`... AND NOT APPLE AND NOT WIN32`), so one self-contained DLL results.
   It also adds `localvqe_prime_delay_begin/feed/end`: a whole-file echo-delay
   prime, because the cascade's online GCC-PHAT estimate freezes after ~3.5 s
   and can lock a wrong delay for the whole file (seen with NAudio-decoded AAC).
   The ggml GRU patch is applied by LocalVQE's own CMake as usual:

   ```sh
   git apply path/to/amanu/windows/native/localvqe/localvqe-windows.patch
   ```

3. Configure and build from the w64devkit shell:

   ```sh
   cmake -S ggml -B build-win -G Ninja -DCMAKE_BUILD_TYPE=Release \
     -DLOCALVQE_BUILD_SHARED=ON -DGGML_SSE42=ON -DGGML_BLAS=OFF -DGGML_OPENMP=OFF \
     -DCMAKE_SHARED_LINKER_FLAGS="-static -static-libgcc -static-libstdc++"
   cmake --build build-win --config Release -j
   ```

4. Check the imports are only system DLLs, then copy it here:

   ```sh
   objdump -p build-win/liblocalvqe.dll | grep "DLL Name"   # KERNEL32, ADVAPI32, msvcrt
   cp build-win/liblocalvqe.dll path/to/amanu/windows/native/localvqe/
   ```

5. The model is `model/localvqe-v1.4-aec-200K-f32.gguf` from the same
   LocalVQE release; verify `sha256sum` against the pin above before copying.

After replacing either file, run `dotnet test tests\Amanu.Core.Tests` — the
real-DLL echo test runs whenever `echo-runtime\` is present in the test output.

Note: `localvqe_new` takes a narrow path that the MinGW C runtime opens in the
ANSI code page. Amanu passes an ASCII path, the ANSI form, the 8.3 short name,
or else a verified copy of the model in an ASCII-only temp folder.
