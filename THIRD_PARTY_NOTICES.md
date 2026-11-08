# 第三者ソフトウェアのライセンス

ProjectDeck の配布物には、次のライブラリが含まれています。それぞれのライセンスに従って利用しています。

| ライブラリ | 版 | 用途 | ライセンス | 著作権者 | 入手元 |
|---|---|---|---|---|---|
| NAudio.Wasapi / NAudio.Core | 3.3.0 | ダイヤル：音量・マイク・アプリ別音量（Windows Core Audio API） | MIT | © Mark Heath | https://github.com/naudio/NAudio |
| Tomlyn | 2.10.2 | settings.toml の読込 | BSD-2-Clause | Alexandre Mutel | https://github.com/xoofx/Tomlyn |
| System.Management | 10.0.14 | ダイヤル：画面の明るさ（WMI） | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |
| System.Numerics.Tensors | 9.0.0 | NAudio が使用 | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |

実行には .NET 10 デスクトップランタイム（MIT、© .NET Foundation and Contributors）が必要です。ランタイムは配布物に含まれていません。

---

## MIT License（NAudio・System.Management・System.Numerics.Tensors）

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

## BSD 2-Clause License（Tomlyn）

Copyright (c) Alexandre Mutel. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
