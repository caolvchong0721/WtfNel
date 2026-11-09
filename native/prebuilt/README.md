# 预编译 payload

这两个 DLL 由本仓库 `native/` 源码用 `native/build.ps1 -Configuration Release` 编出，直接入库；
C# 构建时按原样复制到 `payloads/`，不需要 Visual Studio C++ 工具链。

| 文件 | 来源 |
| --- | --- |
| `WtfNel.Payload.HwidSpoof.dll` | `native/hwid` + `native/proxy/ReflectiveLoader.c` |
| `WtfNel.Payload.Proxy.dll` | `native/proxy` |

校验：`SHA256SUMS.txt`。

改过 `native/` 下任何源码后，重新生成并覆盖这里：

```powershell
powershell -File native/build.ps1 -Configuration Release
copy native\build\WtfNel.Payload.*.dll native\prebuilt\
```

`SHA256SUMS.txt` 也要跟着更新。只想临时用本地刚编出来的产物、不动入库文件：

```powershell
dotnet build WtfNel.slnx -p:BuildNativePayload=true
```

该开关会先跑 `native/build.ps1`（固定 Release），并优先使用 `native/build/` 下的新产物。
