# NXE Asset Translator

Deterministically extracts the supplied retail Xbox 360 NXE 9199 dashboard and converts its XUR v5 scenes into an intermediate representation for the standalone modern-Xbox dashboard.

This tool does not use Xenia and does not treat screenshots as dashboard assets. The source XEX hash is pinned so a different dashboard build cannot silently produce a mixed asset set.

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\translate-retail-nxe.ps1
```

The clean output is written to `D:\Projects\NXE\Dashboard\RetailNXE-9199` and contains:

- raw XEX sections;
- unpacked retail assets;
- converted XUI scene documents;
- complete SHA-256 inventory;
- tool and conversion reports.

`XUIHelper` is a build-time translator only. It is not included in the modern-Xbox app.
