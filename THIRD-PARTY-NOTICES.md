# Third-party notices

Vibe SSH Monitoring is licensed under the GNU General Public License v3.0 or later (see [LICENSE](LICENSE)).
It includes or depends on the following third-party components, all under licenses compatible with the GPL-3.0.

| Component | Version | License | Copyright | Source |
|---|---|---|---|---|
| SSH.NET | 2026.0.0 | MIT | © Renci 2010-2026 | https://github.com/sshnet/SSH.NET |
| BouncyCastle.Cryptography (SSH.NET dependency) | 2.7.0 | MIT | © The Legion of the Bouncy Castle Inc. 2000-2026 | https://www.bouncycastle.org |
| Microsoft.Extensions.Logging.Abstractions (SSH.NET dependency) | 8.0.3 | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions (SSH.NET dependency) | 8.0.2 | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |
| Konscious.Security.Cryptography.Argon2 | 1.3.1 | MIT | © 2024 Keef Aragon | https://github.com/kmaragon/Konscious.Security.Cryptography |
| Konscious.Security.Cryptography.Blake2 (Argon2 dependency) | 1.1.1 | MIT | © 2024 Keef Aragon | https://github.com/kmaragon/Konscious.Security.Cryptography |
| Microsoft.Web.WebView2 (SDK) | 1.0.4258.31 | BSD-3-Clause | © Microsoft Corporation | https://aka.ms/webview |
| Velopack (library, installer and updater) | 1.2.161 | MIT | © Velopack Ltd. | https://github.com/velopack/velopack |
| xterm.js (`@xterm/xterm`) | 6.0.0 | MIT | © 2017-2019 The xterm.js authors, © 2014-2016 SourceLair, © 2012-2013 Christopher Jeffrey | https://github.com/xtermjs/xterm.js |
| `@xterm/addon-fit` | 0.11.0 | MIT | © The xterm.js authors | https://github.com/xtermjs/xterm.js |
| `@xterm/addon-web-links` | 0.12.0 | MIT | © The xterm.js authors | https://github.com/xtermjs/xterm.js |

The xterm.js files are vendored, unmodified, in [`Assets/terminal/lib/`](Assets/terminal/lib/) with their license ([`LICENSE-xterm.txt`](Assets/terminal/lib/LICENSE-xterm.txt)).

The Microsoft Edge WebView2 **Runtime** is not redistributed: it is a component of Windows 10/11, installed and updated by Windows.
The user interface theme is derived from the author's own project GoveeScreen.

---

## MIT License

Applies to: SSH.NET, BouncyCastle.Cryptography, Microsoft.Extensions.*.Abstractions, Konscious.Security.Cryptography.*, Velopack, xterm.js and its addons (copyright holders listed above).

```
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
```

## BSD 3-Clause License, Microsoft.Web.WebView2

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
