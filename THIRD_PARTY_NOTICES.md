# Third-party notices

BikeSync includes or links to the following third-party components.

## iGPSPORT signature module

- File: `BikeSync.Core/Resources/edge_core.wasm`
- Source: `https://app.igpsport.cn/assets/edge_core_wasm_bg-GHC38n8Q.wasm`
- SHA-256: `7b9d7936b75afc21d0cca11a3eddddea09ecdd782c6064dd1f65d6e674313bfb`
- Size: 151,263 bytes
- Purpose: executes the same request-signature ABI used by iGPSPORT's public web client.
- This asset contains no user credentials. It is embedded so the app does not download code at runtime.
- The asset remains subject to iGPSPORT's terms; it is used only to sign requests for the signed-in user's own account.

## Wasmtime

- Package: `Wasmtime` 22.0.0 (NuGet)
- Project: https://github.com/bytecodealliance/wasmtime-dotnet
- License: Apache-2.0 with LLVM exception
- Purpose: runs the embedded iGPSPORT WebAssembly signer locally.

## Protocol references

The MyWhoosh and iGPSPORT adapters were implemented with reference to these community projects:

- `luo703/mywhoosh-to-igpsport` — MIT License, Copyright (c) 2026 mywhoosh-to-igpsport contributors
  https://github.com/luo703/mywhoosh-to-igpsport
- `kamikadzem22/igpsport-unoffical-api` — unofficial API notes, ISC-licensed OpenAPI document
  https://github.com/kamikadzem22/igpsport-unoffical-api
- `simple4wan/ride-sync` — iGPSPORT activity export flow
  https://github.com/simple4wan/ride-sync

No third-party account credentials, session files, or private data are included.

## MIT license text (mywhoosh-to-igpsport)

Copyright (c) 2026 mywhoosh-to-igpsport contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
